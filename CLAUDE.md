# WinLogRotate

Log rotation for Windows: a NativeAOT CLI (`winlogrotate.exe`), a WinForms console
(`winlogrotate-gui.exe`) that shells out to it, a scheduled task, and an NSIS installer. .NET 10,
SDK pinned in `global.json`. README is the product overview; `docs/` is the reference.

## Layout

- `src/WinLogRotate.Core` — engine: config (Tomlyn syntax tree), planners, executor, journal,
  notify, secrets. Platform-neutral where it can be; Win32 behind `Io/NativeMethods`.
- `src/WinLogRotate.Hosting` — Windows-only: scheduled task, ACLs, Event Log, DPAPI, secret pipe.
- `src/WinLogRotate.Cli` — the verbs (`Commands/CommandTree.cs` is the surface), `--json` envelopes.
- `src/WinLogRotate.Contracts` — the envelope/event wire format shared by CLI and GUI.
- `src/WinLogRotate.Gui.Model` — every piece of GUI logic that can run without WinForms.
- `src/WinLogRotate.Gui` — the console: `MainForm`, `Pages/`, `Ui/LrDialog`, `Ui/SecretPrompt`.
- `tests/WinLogRotate.Core.Tests` (runs on Linux), `tests/WinLogRotate.Windows.Tests` (skips off
  Windows), `tests/WinLogRotate.Gui.Tests` (the docs tour; Windows only, not in the slnx).
- `packaging/winlogrotate.nsi` — the installer; `.github/scripts/` — installer-smoke helpers.

## Commands

```
dotnet build WinLogRotate.slnx -c Release
dotnet test tests/WinLogRotate.Core.Tests/WinLogRotate.Core.Tests.csproj -c Release --no-build   # Linux-safe suite
dotnet test WinLogRotate.slnx -c Release                 # everything (Windows)
dotnet format WinLogRotate.slnx --verify-no-changes --severity warn
dotnet publish src/WinLogRotate.Cli/WinLogRotate.Cli.csproj -c Release -r win-x64 -p:PublishAot=true /warnaserror   # Windows only
makensis -WX -V2 -DVERSION=0.0.0 packaging/winlogrotate.nsi   # needs stub exes in packaging/
bash scripts/docs-screenshots.sh [outdir]                # Windows; elsewhere: gh workflow run docs-screenshots.yml
fleet check WinLogRotate --no-ci                         # through ~/repos/.fleet/scripts/slot heavy
```

## Invariants

- **A push to `main` can publish a release.** `ci.yml` is CI *and* release: after every gate passes,
  the `version` job bumps from commit subjects since the last `v*` tag (`feat:` minor, `fix:` patch,
  `!`/`BREAKING CHANGE` major) and `release` tags and uploads. `docs:`, `test:`, `ci:`, `chore:`,
  `refactor:` never release. Never tag by hand; a manual release is `workflow_dispatch` with `bump`.
- Version comes only from CI (`-p:Version`); `Directory.Build.props` keeps `0.0.0-dev`.
- Warnings are errors, doc comments are compiled (`CS1572`/`CS1571` stay on), and
  `dotnet format` runs in CI on Windows (fleet allows it to fail on Linux).
- The CLI is NativeAOT with zero dependencies: no reflection JSON (source-generated contexts only),
  nothing that trims badly. The `aot` job enforces size and dependency gates.
- The GUI never reimplements the CLI: every action shells out (`Gui/Cli/CliRunner`). Logic goes in
  `Gui.Model` so Linux tests cover it. No `MessageBox` (an architecture test fails it) — use `LrDialog`.
- Config edits go through `TomlEditor`: load-change-save differs in exactly the changed line;
  comments, unknown keys and line endings survive. Never re-serialize a config.
- Hooks run only from a configuration directory locked to SYSTEM + Administrators; per-user
  installs refuse them by design. Don't "fix" that.
- Windows-only code is behind `[SupportedOSPlatform]`/`OperatingSystem.IsWindows()`; the
  `linux-typecheck` job proves the rest stays neutral.
- CI runners are pinned to `windows-2025` on purpose (AOT output depends on the image);
  `runner-canary` watches `windows-latest`.
- The docs tour builds a demo machine under `C:\WinLogRotate-demo` and deletes it only if it
  carries its own marker file. Screenshots are fictional (`*.example`); regenerate, don't hand-edit.
- No services or ports: nothing here binds a port or runs Docker.

## Conventions

- Conventional Commits with a scope where it helps (`fix(engine): …`), subject saying what the
  product now does. End with the Co-Authored-By trailer.
- Comments explain *why*, often with the incident behind them; keep that style, and keep README's
  "Status, honestly" section true when behaviour or test coverage changes.
- New diagnostics get a stable `LRxxxx` code (`Contracts/DiagnosticCode.cs`); Event Log IDs live in
  `Hosting/Diagnostics/EventIds.cs` and are documented in `docs/diagnostics.md`.
