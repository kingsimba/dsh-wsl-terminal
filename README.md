# dsh-wsl-terminal

A local DeepSeek Harness plugin that makes the Web right-sidebar terminal behave
the way a normal Windows/WSL terminal does:

1. **Ctrl+C / Ctrl+V** — Ctrl+C copies the terminal selection when there is one,
   and otherwise reaches the shell as `SIGINT` (`^C`). Ctrl+V lets the browser's
   native paste event reach xterm instead of being turned into a control
   character.
2. **Ctrl+`** — opens a terminal through a small launcher that selects
   `cmd.exe` for an ordinary Windows session and the matching WSL distribution
   for a WSL project session. The launcher reads the Session's working
   directory, so it also follows `dsh-wsl-workspace`'s UNC and Windows-drive
   workspace registrations.

## Install

The bundle is at [`plugin/`](plugin). Install it in the active profile with the
Plugin Manager (`install_bundle` on the absolute `plugin` directory), or:

```powershell
dsh plugin --profile desktop add C:\Users\kings\Documents\deepseek-harness\dsh-wsl-terminal\plugin
```

After installation, **refresh the Web GUI once**. The Host half is live
immediately; the browser half (the keyboard handlers) is a client module that
loads with the page. The terminal's default shell also becomes the launcher, so
Ctrl+` works even before the refresh when the remembered shell is not an
explicit `cmd.exe` choice.

## What Ctrl+` does

The Host half replaces the terminal controller's default shell with
`dsh-terminal-shell.exe`. For each new terminal:

| Session working directory | Terminal opens |
|---|---|
| `C:\...` (ordinary Windows project) | `cmd.exe` in that directory |
| `\\wsl.localhost\<distro>\<linux>` | `<distro>`, with `--cd <linux>` |
| `\\wsl$\<distro>\<linux>` | same, legacy UNC spelling |
| Windows-drive WSL workspace registered by `dsh-wsl-workspace` | the registered distro, with `--cd /mnt/<drive>/...` |
| a registered workspace with an optional username | adds `wsl.exe -u <username>` |

The WSL detection is path-based. The `dsh-wsl-workspace` plugin keeps optional
per-workspace usernames and Windows-drive → distro records in
`<DSH_HOME>\wsl-workspaces.json`; the launcher reads that file on each start.
`DSH_HOME` is passed explicitly as `--dsh-home` because the terminal child
environment is scrubbed of `DSH_*` variables.

### Why the launcher is copied

The bundle is linked from this workspace, but WSL refuses to start when the
launcher's process image lives under the DSH workspace tree (Windows returns
`Wsl/E_ACCESSDENIED`). The Host half therefore materializes the launcher under:

```
%LOCALAPPDATA%\dsh-wsl-terminal\<version>\dsh-terminal-shell.exe
```

and points the terminal controller at that copy. The copied file is updated
when the bundled launcher changes.

## Copy and paste

- **Ctrl+C with a selection** — dispatches xterm's own `copy` DOM event to read
  the selection, copies it with the Clipboard API, and consumes the key.
- **Ctrl+C without a selection** — does not consume the key, so xterm sends
  `^C` to the shell.
- **Ctrl+V** — suppresses only xterm's keydown handler; the browser delivers
  its native `paste` event to xterm's hidden textarea. The terminal's
  bracketed-paste handling is preserved.
- On macOS the copy/paste modifier is `Cmd`; `Ctrl+C` remains `SIGINT`.

## Layout

```
plugin/
  package.json           bundle + dsh.client manifest
  cordis.patch.yml       inserts the wsl-terminal row
  index.js               Host half: launcher copy + terminal shell profile
  client.js              browser half: Ctrl+C / Ctrl+V / Ctrl+`
  locale/{en,zh}.json    Plugin Manager metadata
  icon.svg
  shell/
    DshTerminalShell.cs  native launcher source
    dsh-terminal-shell.exe
    build.ps1            rebuilds the exe with the inbox csc.exe
```

Rebuild the launcher with:

```powershell
powershell -ExecutionPolicy Bypass -File plugin\shell\build.ps1
```

## Verification

- The Host row is active in the profile (`enabled: true`, `fiberPhase: active`).
- The copied launcher was exercised under a real ConPTY:
  - ordinary Windows directory → `cmd.exe`, Ctrl+C breaks a running command,
    the shell survives and accepts the next command;
  - `\\wsl.localhost\Ubuntu-24.04\home\simba` → the distribution's bash, correct
    `/home/simba` cwd;
  - a Windows-drive workspace registered with a distro/user → the distribution's
    bash at `/mnt/c/...`, running as the registered user;
  - Ctrl+C in WSL interrupts `sleep` and the shell survives.
- The client half follows xterm's public `copy`/`paste` DOM contracts. It was
  syntax-checked and installed, but the browser keyboard interaction could not
  be exercised from this session (no browser control); refresh the page and try
  Ctrl+C / Ctrl+V / Ctrl+` in a terminal.

## Uninstall

Use the Plugin Manager `remove_bundle` for `@local/dsh-wsl-terminal`, then
refresh the page. The copied launcher under `%LOCALAPPDATA%` can be deleted.
