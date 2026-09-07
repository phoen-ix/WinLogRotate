# WinLogRotate

Log rotation for Windows, with a GUI, a CLI, and a scheduler that actually schedules.

It imitates Linux `logrotate`'s **behaviour** — the same directive names, the same
frequency and retention semantics, the same surprising edge cases — on a platform whose
filesystem works nothing like Unix's.

> **Not related to LogRotateWin.** WinLogRotate is a new MIT-licensed project on .NET 10.
> The 2013 SourceForge *LogRotateWin* and its forks are GPLv3 and unmaintained.

## Why another one

Every existing Windows port is GPLv3, one-shot-only, and leaves scheduling entirely to you.
None of them handle the two things that actually go wrong on Windows:

- **Files you cannot rename.** Renaming needs `DELETE` access, which every open handle must
  have permitted via `FILE_SHARE_DELETE`. IIS, http.sys, SQL Server and most C++ services
  don't. log4net's default `ExclusiveLock` denies write sharing too, so *nothing outside the
  process can rotate it* — WinLogRotate detects that and says so, instead of failing quietly.
- **`copytruncate` silently destroying your disk.** A writer that caches its own file
  position doesn't seek back to 0 after truncation, so its next write lands at the old
  offset and NTFS zero-fills the gap: a multi-gigabyte file of NUL bytes plus one log line,
  every rotation, forever. WinLogRotate detects this and stops.

And the biggest source of Windows log pain isn't rotation at all. **IIS, http.sys, NSSM,
WinSW, Apache `rotatelogs`, Serilog, NLog, log4net and Tomcat all rotate their own logs and
then never delete or compress them.** So that's a first-class job kind:

```toml
[job]
name  = "IIS W3SVC1"
kind  = "manage"        # IIS rotates; we compress + retain, and never touch the live file
paths = ["C:/inetpub/logs/LogFiles/W3SVC1/u_ex*.log"]
compress = true
rotate   = 30
maxage   = 90
```

No locking, no truncation, no risk.

## Status

Early. Milestone 0 of 22 — the skeleton builds, tests pass, and the AOT toolchain is gated
in CI. Nothing rotates anything yet. See the build order in the project plan.

## Two executables, on purpose

| | What it is | Size | Needs anything installed? |
|---|---|---|---|
| `winlogrotate.exe` | The engine and CLI. What the scheduler runs. | ~8 MB | **No** — NativeAOT, zero dependencies |
| `winlogrotate-gui.exe` | The admin console. | ~200 KB | Yes — the .NET 10 Desktop Runtime |

The half that runs unattended on a freshly-imaged server needs nothing. The half where a
human is already sitting at the machine is tiny because WinForms ships *inside* the runtime.

## Building

Needs the .NET 10 SDK.

```
dotnet build WinLogRotate.slnx -c Release
dotnet test  WinLogRotate.slnx -c Release
```

`global.json` pins the SDK band and opts the repo into the Microsoft.Testing.Platform
runner, which xunit.v3 requires on .NET 10.

Everything except the WinForms project builds and tests on Linux — that's deliberate, and
it's how the engine's platform-neutral half stays honest.

## License

MIT. See [LICENSE](LICENSE).
