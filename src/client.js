/**
 * Client half of the WSL terminal bundle.
 *
 * Two keyboard behaviours that the built-in terminal does not provide:
 *
 * 1. Ctrl+C copies the terminal selection when one exists, and otherwise falls
 *    through to xterm so it sends SIGINT. Ctrl+V stops xterm's key handler from
 *    turning the gesture into a control character, letting the browser deliver
 *    its native paste event to xterm's hidden textarea.
 * 2. Ctrl+` opens a terminal through the Host launcher instead of the ordinary
 *    `terminal.new` action. The launcher selects cmd.exe or the WSL distro from
 *    the Session's working directory.
 *
 * The terminal plugin keeps its xterm instance private. The copy path therefore
 * uses xterm's own public `copy` DOM listener: dispatching a ClipboardEvent with
 * a DataTransfer makes xterm fill that DataTransfer with its selection text.
 */

window.__ModuleLoader__.load({
  id: 'dsh-wsl-terminal',
  factory() {
    const LAUNCHER_NAME = 'dsh-terminal-shell.exe';

    function basename(path) {
      if (typeof path !== 'string') return '';
      const index = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
      return path.slice(index + 1).toLowerCase();
    }

    function terminalRoot(node) {
      return node instanceof Element ? node.closest('.xterm') : null;
    }

    function insideModal(node) {
      return (
        node instanceof Element &&
        node.closest('[role="dialog"], dialog, [aria-modal="true"], [data-shortcut-modal]') !== null
      );
    }

    function terminalSelection(root) {
      if (typeof DataTransfer !== 'function' || typeof ClipboardEvent !== 'function') return '';
      const data = new DataTransfer();
      try {
        const event = new ClipboardEvent('copy', {
          bubbles: true,
          cancelable: true,
          clipboardData: data,
        });
        root.dispatchEvent(event);
      } catch {
        return '';
      }
      return data.getData('text/plain') || '';
    }

    async function writeClipboard(text) {
      try {
        await navigator.clipboard.writeText(text);
        return;
      } catch {
        // Fall through to the selection-based copy command.
      }

      const active = document.activeElement;
      const area = document.createElement('textarea');
      area.value = text;
      area.setAttribute('readonly', '');
      area.style.position = 'fixed';
      area.style.top = '0';
      area.style.left = '-9999px';
      area.style.opacity = '0';
      document.body.appendChild(area);
      area.focus();
      area.select();
      try {
        document.execCommand('copy');
      } catch {
        // Nothing else can be done without clipboard permission.
      }
      area.remove();
      if (active instanceof HTMLElement && active.isConnected) {
        try {
          active.focus({ preventScroll: true });
        } catch {
          active.focus();
        }
      }
    }

    function isPlainModifier(event, mac) {
      return mac
        ? event.metaKey && !event.ctrlKey && !event.altKey && !event.shiftKey
        : event.ctrlKey && !event.metaKey && !event.altKey && !event.shiftKey;
    }

    function isNewTerminal(event) {
      return (
        event.ctrlKey &&
        !event.metaKey &&
        !event.altKey &&
        !event.shiftKey &&
        (event.code === 'Backquote' || event.key === '`')
      );
    }

    function apply(ctx) {
      ctx.effect(() => {
        const lifetime = new AbortController();
        let launcherPath;
        let discovering;

        const discoverLauncher = (sessionId) => {
          if (launcherPath !== undefined) return Promise.resolve(launcherPath);
          if (discovering !== undefined) return discovering;
          discovering = (async () => {
            try {
              const result = await ctx.webTerminals.remote.shells(sessionId, lifetime.signal);
              if (!result || result.ok === false) return undefined;
              const shells = result.value ?? result;
              if (!Array.isArray(shells)) return undefined;
              const shell = shells.find(
                (candidate) => candidate && basename(candidate.path) === LAUNCHER_NAME,
              );
              if (shell !== undefined && typeof shell.path === 'string') launcherPath = shell.path;
              return launcherPath;
            } catch {
              return undefined;
            } finally {
              discovering = undefined;
            }
          })();
          return discovering;
        };

        const openTerminal = (event) => {
          if (event.repeat) return;
          const element = event.target instanceof Element ? event.target : null;
          if (element !== null && insideModal(element)) return;
          const target = ctx.sidebarRight.commandTarget(element ?? undefined);
          if (target === undefined) return;

          event.preventDefault();
          event.stopPropagation();
          void discoverLauncher(target.sessionId).then((path) => {
            if (path !== undefined) {
              try {
                ctx.webTerminals.selectShell(path);
              } catch {
                // The default shell profile still applies if the preference cannot be saved.
              }
            }
            ctx.sidebarRight.openTabFromTarget('terminal', target);
          });
        };

        const keydown = (event) => {
          if (event.defaultPrevented || event.isComposing) return;
          const mac = /Mac|iPhone|iPad/.test(navigator.platform);
          const root = terminalRoot(event.target);

          if (root !== null && isPlainModifier(event, mac)) {
            const key = typeof event.key === 'string' ? event.key.toLowerCase() : '';
            if (event.code === 'KeyC' || key === 'c') {
              const text = terminalSelection(root);
              if (text.length === 0) return;
              event.preventDefault();
              event.stopPropagation();
              void writeClipboard(text);
              return;
            }
            if (event.code === 'KeyV' || key === 'v') {
              // xterm would turn Ctrl+V into a control character and cancel the
              // browser default. Suppress only its keydown handler so the
              // native paste event reaches xterm's hidden textarea.
              const textarea = root.querySelector('textarea.xterm-helper-textarea');
              if (textarea !== null && document.activeElement !== textarea) {
                textarea.focus({ preventScroll: true });
              }
              event.stopPropagation();
              return;
            }
          }

          if (isNewTerminal(event)) openTerminal(event);
        };

        window.addEventListener('keydown', keydown, true);
        return () => {
          window.removeEventListener('keydown', keydown, true);
          lifetime.abort();
        };
      }, 'wsl-terminal.keyboard');
    }

    return { inject: ['webTerminals', 'sidebarRight'], apply };
  },
});
