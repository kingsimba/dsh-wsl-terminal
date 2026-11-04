/**
 * Host half of the WSL terminal bundle.
 *
 * The Web terminal controller discovers shells through its `shell` config. This
 * plugin replaces that default with a small launcher that opens `cmd.exe` for
 * ordinary Windows sessions and `wsl.exe -d <distro>` for sessions whose
 * working directory is a WSL UNC path.
 *
 * The launcher must live outside the session workspace: DSH's workspace
 * sandbox marks the workspace directory, and WSL refuses a launch whose process
 * image comes from that marked tree. The plugin therefore materializes a copy
 * under %LOCALAPPDATA% once per package version.
 */

import { copyFileSync, existsSync, mkdirSync, readFileSync, statSync } from "node:fs";
import { homedir, tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const LAUNCHER_NAME = "dsh-terminal-shell.exe";
const DEFAULT_SHELL_NAME = "Terminal (WSL / cmd)";

/** Services required before the shell profile can be installed. */
export const inject = ["terminalController"];

function packageVersion() {
  try {
    return JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8")).version;
  } catch {
    return "0";
  }
}

/**
 * Copy the bundled launcher to a stable user-local path and return it.
 * A linked bundle lives inside the workspace, where WSL rejects an executable
 * image; running the user-local copy avoids that restriction.
 *
 * @param ctx - Host context used for diagnostics.
 * @returns the executable to hand to the terminal controller, or undefined.
 */
function materializeLauncher(ctx) {
  const bundled = fileURLToPath(new URL(`../shell/${LAUNCHER_NAME}`, import.meta.url));
  if (!existsSync(bundled)) {
    ctx.logger.warn(`[wsl-terminal] launcher is missing: ${bundled}`);
    return undefined;
  }

  const root = process.env.LOCALAPPDATA || process.env.XDG_CACHE_HOME || tmpdir();
  const target = join(root, "dsh-wsl-terminal", packageVersion(), LAUNCHER_NAME);

  try {
    mkdirSync(join(root, "dsh-wsl-terminal", packageVersion()), { recursive: true });
    const source = statSync(bundled);
    let current;
    try {
      current = statSync(target);
    } catch {
      current = undefined;
    }
    if (current === undefined || current.size !== source.size || current.mtimeMs < source.mtimeMs) {
      copyFileSync(bundled, target);
    }
    return target;
  } catch (error) {
    // A running copy can be locked during a plugin reload. Reuse it when it is
    // already there; otherwise fall back to the bundled path so the plugin is
    // still usable in profiles whose bundle was installed outside a workspace.
    if (existsSync(target)) return target;
    ctx.logger.warn(
      `[wsl-terminal] could not prepare ${target}; using the bundled launcher`,
      error,
    );
    return bundled;
  }
}

/**
 * Install the WSL-aware default shell profile.
 *
 * @param ctx - Host context with `terminalController`.
 * @param config - row config: `shellName`, `replaceShell`.
 */
export function apply(ctx, config = {}) {
  const launcher = materializeLauncher(ctx);
  if (launcher === undefined) return;

  const shellName =
    typeof config.shellName === "string" && config.shellName.trim() !== ""
      ? config.shellName.trim()
      : DEFAULT_SHELL_NAME;
  // The terminal child starts from the scrubbed subprocess environment, which
  // drops DSH_* variables. Pass the harness home explicitly so the launcher can
  // read dsh-wsl-workspace's wsl-workspaces.json for drive-path workspaces.
  const dshHome = process.env.DSH_HOME || join(homedir(), ".dsh");
  const shell = { path: launcher, name: shellName, args: ["--dsh-home", dshHome] };

  ctx.effect(() => {
    const controller = ctx.terminalController;
    if (config.replaceShell === false) return undefined;

    const previous = controller.config.shell;
    controller.config.shell = shell;
    return () => {
      if (controller.config.shell === shell) controller.config.shell = previous;
    };
  }, "wsl-terminal.shell-profile");
}
