# Changelog

- 2026-11-04 dsh-wsl-terminal: Publish as `dsh-wsl-terminal` — the `@local/` scope and `private` are
  gone — and keep the package at the repository root instead of in `plugin/`. The Host and Client halves
  moved into `src/`, and the client bundle is registered under the package name. A profile that links
  this checkout must now point at the repository root.
