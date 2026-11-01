// DSH terminal launcher: choose cmd.exe for ordinary Windows sessions and the
// matching WSL distribution when the session working directory is a WSL UNC
// path or a Windows-drive workspace registered by dsh-wsl-workspace.
//
// The launcher stays alive while the real shell runs so Ctrl+C reaches that
// shell instead of ending the launcher.
//
// Build (Windows, .NET Framework 4.x):
//   csc /nologo /target:exe /platform:x64 /optimize+ ^
//       /reference:System.Web.Extensions.dll ^
//       /out:dsh-terminal-shell.exe DshTerminalShell.cs
//
// Probe:
//   dsh-terminal-shell.exe --probe "\\wsl.localhost\Ubuntu-24.04\home\me\proj"
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

internal static class Program
{
    private const string WslUncPattern =
        @"^\\\\wsl(?:\.localhost|\$)\\(?<distro>[^\\/]+)(?:[\\/](?<tail>.*))?$";

    private const string DrivePattern = @"^([A-Za-z]):[\\/](.*)$";
    private const string UsernamePattern = @"^[A-Za-z_][A-Za-z0-9_.-]{0,63}$";

    private static readonly Regex WslUnc = new Regex(
        WslUncPattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Drive = new Regex(
        DrivePattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The harness home passed by the Host plugin with `--dsh-home`.</summary>
    private static string dshHome;

    private static int Main(string[] args)
    {
        List<string> remaining = new List<string>();
        for (int index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--dsh-home", StringComparison.Ordinal) && index + 1 < args.Length)
            {
                dshHome = args[++index];
                continue;
            }
            remaining.Add(args[index]);
        }
        args = remaining.ToArray();

        if (args.Length == 2 && string.Equals(args[0], "--probe", StringComparison.Ordinal))
        {
            return Probe(args[1]);
        }

        if (args.Length == 1 &&
            (string.Equals(args[0], "--help", StringComparison.Ordinal) ||
             string.Equals(args[0], "-h", StringComparison.Ordinal)))
        {
            Console.WriteLine("dsh-terminal-shell: opens cmd.exe, or the WSL distribution named by a");
            Console.WriteLine("\\\\wsl.localhost\\<distro>\\... / \\\\wsl$\\<distro>\\... working directory,");
            Console.WriteLine("or by the dsh-wsl-workspace record of a Windows-drive workspace.");
            Console.WriteLine("Options: --dsh-home <path> sets the harness home holding wsl-workspaces.json.");
            return 0;
        }

        // The terminal's Ctrl+C belongs to the shell child. Keep this process
        // alive so the PTY stays attached to the same console.
        Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
        };

        string workingDirectory = Environment.CurrentDirectory;
        Launch launch = Resolve(workingDirectory);

        ProcessStartInfo start = new ProcessStartInfo();
        start.FileName = launch.FileName;
        start.Arguments = launch.Arguments;
        start.WorkingDirectory = launch.WorkingDirectory;
        start.UseShellExecute = false;

        try
        {
            using (Process child = Process.Start(start))
            {
                if (child == null)
                {
                    Console.Error.WriteLine("dsh-terminal-shell: could not start " + launch.FileName);
                    return 127;
                }
                child.WaitForExit();
                return child.ExitCode;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("dsh-terminal-shell: " + error.Message);
            return 127;
        }
    }

    private static int Probe(string workingDirectory)
    {
        Launch launch = Resolve(workingDirectory);
        Console.WriteLine("cwd=" + workingDirectory);
        Console.WriteLine("wsl=" + (launch.Distro == null ? "no" : "yes"));
        Console.WriteLine("distro=" + (launch.Distro ?? string.Empty));
        Console.WriteLine("linux=" + (launch.LinuxPath ?? string.Empty));
        Console.WriteLine("user=" + (launch.Username ?? string.Empty));
        Console.WriteLine("storeKey=" + (launch.StoreKey ?? string.Empty));
        Console.WriteLine("store=" + (launch.StoreFound ? "yes" : "no"));
        Console.WriteLine("dshHome=" + (dshHome ?? string.Empty));
        Console.WriteLine("file=" + launch.FileName);
        Console.WriteLine("args=" + launch.Arguments);
        Console.WriteLine("workdir=" + launch.WorkingDirectory);
        return 0;
    }

    private static Launch Resolve(string workingDirectory)
    {
        string path = NormalizeUncPrefix(workingDirectory);
        WslPath wsl = WslPath.Parse(path);
        string storeKey = null;
        StoreEntry entry = null;
        string linuxPath = null;

        if (wsl != null)
        {
            // The WSL UNC spelling already names the distribution.
            linuxPath = wsl.LinuxPath;
            storeKey = CanonicalUnc(wsl.Distro, wsl.LinuxPath);
            entry = WorkspaceStore.Lookup(storeKey);
        }
        else
        {
            // A Windows-drive workspace registered by dsh-wsl-workspace carries
            // its distribution in <dshHome>/wsl-workspaces.json.
            string canonical = CanonicalWindowsPath(path);
            if (canonical != null)
            {
                entry = WorkspaceStore.Lookup(canonical);
                if (entry != null && !string.IsNullOrEmpty(entry.Distro))
                {
                    wsl = new WslPath { Distro = entry.Distro, LinuxPath = WindowsToMntPath(path) };
                    linuxPath = wsl.LinuxPath;
                    storeKey = canonical;
                }
            }
        }

        if (wsl != null)
        {
            string systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
            string username = entry != null && IsValidUsername(entry.Username) ? entry.Username : null;
            return new Launch
            {
                Distro = wsl.Distro,
                LinuxPath = linuxPath,
                Username = username,
                StoreKey = storeKey,
                StoreFound = entry != null,
                FileName = WslExecutable(),
                Arguments = BuildWslArguments(wsl.Distro, username, linuxPath),
                WorkingDirectory = string.IsNullOrEmpty(systemRoot) ? "C:\\" : systemRoot
            };
        }

        string comspec = Environment.GetEnvironmentVariable("ComSpec");
        return new Launch
        {
            FileName = string.IsNullOrEmpty(comspec) ? "cmd.exe" : comspec,
            Arguments = string.Empty,
            WorkingDirectory = workingDirectory
        };
    }

    private static string BuildWslArguments(string distro, string username, string linuxPath)
    {
        StringBuilder builder = new StringBuilder();
        builder.Append(Quote("-d")).Append(' ').Append(Quote(distro));
        if (!string.IsNullOrEmpty(username))
        {
            builder.Append(' ').Append(Quote("-u")).Append(' ').Append(Quote(username));
        }
        builder.Append(' ').Append(Quote("--cd")).Append(' ').Append(Quote(linuxPath));
        return builder.ToString();
    }

    /// <summary>Prefer the inbox System32 launcher over the Store execution alias.</summary>
    private static string WslExecutable()
    {
        string systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (!string.IsNullOrEmpty(systemRoot))
        {
            string candidate = Path.Combine(systemRoot, "System32", "wsl.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return "wsl.exe";
    }

    private static string NormalizeUncPrefix(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path.Substring(8);
        }
        // Accept the forward-slash UNC spellings dsh-wsl-workspace also parses.
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            path = path.Replace('/', '\\');
        }
        return path;
    }

    private static string CanonicalWindowsPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        Match match = Drive.Match(path);
        if (!match.Success)
        {
            return null;
        }
        string rest = match.Groups[2].Value.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        return char.ToLowerInvariant(match.Groups[1].Value[0]) + ":\\" + rest;
    }

    private static string WindowsToMntPath(string path)
    {
        Match match = Drive.Match(path);
        if (!match.Success)
        {
            return null;
        }
        string rest = match.Groups[2].Value.Replace('\\', '/');
        rest = Regex.Replace(rest, "/+", "/").Trim('/');
        return "/mnt/" + char.ToLowerInvariant(match.Groups[1].Value[0]) +
               (rest.Length == 0 ? string.Empty : "/" + rest);
    }

    private static string CanonicalUnc(string distro, string linuxPath)
    {
        string normalized = "/" + (linuxPath ?? string.Empty).Trim('/');
        if (normalized == "/")
        {
            normalized = string.Empty;
        }
        return @"\\wsl.localhost\" + distro + normalized.Replace('/', '\\');
    }

    private static bool IsValidUsername(string value)
    {
        return !string.IsNullOrEmpty(value) && Regex.IsMatch(value, UsernamePattern);
    }

    /// <summary>Quote one Windows command-line argument (CommandLineToArgvW rules).</summary>
    private static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        bool needsQuotes = false;
        foreach (char character in value)
        {
            if (character == ' ' || character == '\t' || character == '\n' ||
                character == '\v' || character == '"')
            {
                needsQuotes = true;
                break;
            }
        }

        if (!needsQuotes)
        {
            return value;
        }

        StringBuilder builder = new StringBuilder();
        builder.Append('"');
        int backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            builder.Append(character);
            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private sealed class Launch
    {
        public string FileName;
        public string Arguments;
        public string WorkingDirectory;
        public string Distro;
        public string LinuxPath;
        public string Username;
        public string StoreKey;
        public bool StoreFound;
    }

    private sealed class WslPath
    {
        public string Distro;
        public string LinuxPath;

        public static WslPath Parse(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            Match match = WslUnc.Match(path);
            if (!match.Success)
            {
                return null;
            }

            string distro = match.Groups["distro"].Value;
            if (distro.Length == 0)
            {
                return null;
            }

            string tail = match.Groups["tail"].Success ? match.Groups["tail"].Value : string.Empty;
            string linux = "/" + tail.Replace('\\', '/');
            if (linux.Length > 1)
            {
                linux = linux.TrimEnd('/');
            }

            return new WslPath { Distro = distro, LinuxPath = linux };
        }
    }

    private sealed class StoreEntry
    {
        public string Distro;
        public string Username;
    }

    /// <summary>Reads the dsh-wsl-workspace store once per launch.</summary>
    private static class WorkspaceStore
    {
        private static bool loaded;
        private static Dictionary<string, object> entries;

        public static StoreEntry Lookup(string key)
        {
            if (key == null)
            {
                return null;
            }
            EnsureLoaded();
            if (entries == null)
            {
                return null;
            }

            object value;
            if (!entries.TryGetValue(key, out value))
            {
                foreach (KeyValuePair<string, object> pair in entries)
                {
                    if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        value = pair.Value;
                        break;
                    }
                }
            }
            if (value == null)
            {
                return null;
            }

            Dictionary<string, object> record = value as Dictionary<string, object>;
            if (record == null)
            {
                return null;
            }

            StoreEntry entry = new StoreEntry();
            object distro;
            if (record.TryGetValue("distro", out distro))
            {
                entry.Distro = distro as string;
            }
            object username;
            if (record.TryGetValue("username", out username))
            {
                entry.Username = username as string;
            }
            return entry;
        }

        private static void EnsureLoaded()
        {
            if (loaded)
            {
                return;
            }
            loaded = true;
            try
            {
                string home = dshHome;
                if (string.IsNullOrEmpty(home))
                {
                    home = Environment.GetEnvironmentVariable("DSH_HOME");
                }
                if (string.IsNullOrEmpty(home))
                {
                    string profile = Environment.GetEnvironmentVariable("USERPROFILE");
                    if (string.IsNullOrEmpty(profile))
                    {
                        profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    }
                    home = Path.Combine(profile, ".dsh");
                }
                string file = Path.Combine(home, "wsl-workspaces.json");
                if (!File.Exists(file))
                {
                    return;
                }
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                entries = serializer.DeserializeObject(File.ReadAllText(file, Encoding.UTF8))
                    as Dictionary<string, object>;
            }
            catch
            {
                entries = null;
            }
        }
    }
}
