# WinLogRotate

Log rotation for Windows — a CLI, a GUI and a scheduler, imitating Linux `logrotate`'s
*behaviour* on a platform whose filesystem works nothing like Unix's.

Same directive names, so `delaycompress` still means what the man page says. Different
container, because a config a GUI edits has to survive being rewritten.

> **Not related to LogRotateWin.** This is a new MIT-licensed project on .NET 10. The 2013
> SourceForge *LogRotateWin* and its forks are GPLv3 and unmaintained.

---

## Why another one

Every existing Windows port is GPLv3, one-shot-only, and leaves scheduling to you. None of
them handle the two things that actually go wrong.

**Files you cannot rename.** Renaming needs `DELETE` access, which every open handle must have
permitted via `FILE_SHARE_DELETE`. IIS, http.sys, SQL Server and most C++ services don't.
log4net's default `ExclusiveLock` denies write sharing too — so *nothing outside the process
can rotate that file*, by any means. WinLogRotate tells you that, instead of failing at 3am:

```
> winlogrotate probe C:\logs\app.log
  verdict     Copy
  This file can only be read, so a copy can be archived but the original will keep growing.

  strategy        supported
  rename          no
  copytruncate    no
  copy            yes
```

**`copytruncate` silently destroying your disk.** A writer that caches its own file offset
doesn't seek back to zero after truncation — its next write lands at the old offset and NTFS
zero-fills the gap. You get four gigabytes of NUL bytes plus one line of log, every rotation,
for ever, without a single error. WinLogRotate detects it and permanently refuses
`copytruncate` for that path.

### And the thing that actually fills Windows disks

It isn't rotation. **IIS, http.sys, NSSM, WinSW, Apache `rotatelogs`, Serilog, NLog, log4net
and Tomcat all roll their own logs perfectly well — and then never delete or compress any of
them.** Disks fill with correctly-rotated files.

So that's a first-class job kind:

```toml
[job]
name  = "IIS W3SVC1"
kind  = "manage"        # IIS rotates; we compress + retain, and never touch the live file
paths = ["C:/inetpub/logs/LogFiles/W3SVC1/u_ex*.log"]
compress = true
rotate   = 30
maxage   = 90
```

No locking, no truncation, no risk. IIS has no way to reopen a log — `appcmd` site stop/start
does *not* release the W3SVC handle, only `iisreset` does — so the live file is untouchable by
anything except IIS, and this mode never tries.

`winlogrotate scan` finds these for you and reports the column that matters:

```
IIS - Default Web Site
  C:\inetpub\logs\LogFiles\W3SVC1
  412 file(s), 2.9 GB
  rotates itself: yes    deletes old ones: NO
```

---

## Install

Two executables, and the split is deliberate. `winlogrotate.exe` is NativeAOT with **no
dependencies at all** — it's what the scheduler runs unattended as SYSTEM on a freshly-imaged
server. Only the GUI needs the .NET runtime, and only in the `-min` builds.

| Installer | Size | Needs anything installed? |
| --- | ---: | --- |
| [`WinLogRotate-Setup-min.exe`](../../releases/latest) | 3.3 MB | Only for the GUI — the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0), fetched for you if missing |
| [`WinLogRotate-Setup-full.exe`](../../releases/latest) | 43.8 MB | **No** — everything is inside |
| [`WinLogRotate-Setup.exe`](../../releases/latest) | 43.8 MB | Asks which of the two to install |

| Portable | Size | Needs anything installed? |
| --- | ---: | --- |
| [`WinLogRotate-min.zip`](../../releases/latest) | 4.2 MB | Only for the GUI |
| [`WinLogRotate.zip`](../../releases/latest) | 45.7 MB | **No** |

Inside a portable zip: `winlogrotate.exe` (9.2 MB, native, zero dependencies) and
`winlogrotate-gui.exe` (0.8 MB). `SHA256SUMS.txt` covers every download.

Installing per-machine puts config in `C:\ProgramData\WinLogRotate\`, registers a scheduled
task that runs as SYSTEM, and **locks that directory down to SYSTEM and Administrators**. That
last part is not optional: the task runs as SYSTEM and jobs can carry hooks, so a
world-writable config directory would let any local user run a command as SYSTEM.

Silent install, for deploying to customer sites:

```
WinLogRotate-Setup.exe /S /AllUsers /HOST=task /CONFIG=\\srv\deploy\jobs
```

---

## Use

```
winlogrotate scan                     # what on this machine needs help?
winlogrotate glob "C:/logs/**/*.log"  # what would this pattern match?
winlogrotate probe C:\logs\app.log    # can this file even be rotated?
winlogrotate config check             # problems, with file, line and column
winlogrotate run --dry-run            # exactly what would happen. changes nothing
winlogrotate doctor                   # paths, permissions, schedule — in one place
```

`--dry-run` is the same code path as a real run, stopped one step earlier — not a separate
description that can drift from what the executor does.

Everything speaks `--json` for scripting, and every verb the GUI offers exists here, because
WinForms doesn't run on Server Core and that's where IIS usually lives.

<details>
<summary>Full command surface</summary>

| Verb | |
| --- | --- |
| `run` | Rotate everything that is due |
| `probe <path>` | Which locked-file strategies a path actually supports |
| `glob <pattern>` | Resolve a pattern, or explain why it was refused |
| `config check` / `show` | Validate; print with every default resolved |
| `host use task\|service\|none` | Choose what runs rotations — switch freely, any time |
| `host status` / `repair` / `pause` | Reality vs config; re-apply permissions; suspend |
| `host export-task` | Scheduled Task XML, for GPO or DSC |
| `import <path>` | Convert a Linux `logrotate.conf` |
| `scan` | Find producers that roll but never delete |
| `journal` | Everything compressed, moved or deleted, and why |
| `doctor` | Every path, permission and schedule at once |
| `update check` | Whether a newer release exists |

</details>

---

## Configuration

`C:\ProgramData\WinLogRotate\config.toml` holds defaults; one file per job in `conf.d\`.

```toml
# conf.d\myapp.toml
schema = 1

[job]
name  = "MyApp"
kind  = "rotate"
paths = ["D:/apps/myapp/logs/*.log"]

daily         = true
rotate        = 14
compress      = true
delaycompress = true          # logrotate's own word, for the same reason
dateext       = true
missingok     = true
notifempty    = true
lockstrategy  = "copytruncate"

postrotate = ["service:paramchange:MyAppSvc"]
```

TOML, and specifically Tomlyn's syntax tree, for one reason: **the GUI can change one value
without touching anything else.** Load-then-save is byte-identical; load-change-one-value-save
differs in exactly one line. Your comments survive — including the "do NOT enable compress, the
shipper can't read .gz" note that explains why a setting is the way it is. YAML and JSON both
destroy comments on write.

`postrotate` replaces `kill -HUP`, which Windows doesn't have:
`service:paramchange:NAME` sends `SERVICE_CONTROL_PARAMCHANGE`, `event:Global\Name` signals a
named event, plus `http:`, `command:` and raw scripts. Every hook has a timeout, and hooks are
refused outright if the config directory isn't locked down.

Coming from Linux? `winlogrotate import \\srv\etc\logrotate.d\nginx` converts it. One way, and
nothing is guessed: `kill -USR1` becomes `service:paramchange:nginx`, but anything that can't be
translated exactly is written into the output **as a TODO comment** and the job is created
disabled.

### Where things live, and the history

Per-machine installs keep everything in `C:\ProgramData\WinLogRotate\` — `config.toml`,
`conf.d\`, `state.json` (the rotation clocks) and `journal\`. Per-user installs use
`%APPDATA%\WinLogRotate\`. `winlogrotate doctor` prints them all.

The **journal** is the record of every file compressed, moved or deleted, and the rule that
caused it. It is what the GUI's History page shows and what `winlogrotate journal` queries —
deliberately not Task Scheduler's Last Run Result, since the registered task exits 0 when a run
overlaps and `0x0` therefore proves nothing.

**It rotates itself**, through the same manage-mode code that tidies IIS logs. That is not a
coincidence: the journal is a directory of dated files written by a producer that rolls them
itself and never cleans up, which is precisely the case manage mode exists for. If manage mode
ever regresses, it shows up in this tool's own output before it shows up in anyone's logs.

```toml
[journal]
enabled  = true
retain   = 30        # days
compress = "zip"     # zip | gzip | none
maxsize  = "50M"     # roll mid-day past this, so one busy day can't produce an unopenable file
```

`enabled = false` writes no history at all — at the cost of making "what happened to my logs?"
unanswerable. Today's journal is never touched while it is being written, for the same reason
the live IIS log never is.

There is deliberately no separate application log file. Diagnostics go to stdout for whoever
ran the command and to the Windows Event Log at Warning and above; the record of what was
*done* is the journal. A third half-used sink would be one more thing to rotate and one more
place to look.

See [logrotate compatibility](docs/logrotate-compatibility.md) for what matches, what
deliberately differs, and what cannot exist on Windows.

---

## Status, honestly

**v0.0.1. The pipeline is proven; the product is not.**

The release builds end to end: 273 tests pass, the NativeAOT binary links and is genuinely
native, all three installers compile under `-WX`, and the assets publish with matching
checksums.

**But nothing has been executed on Windows yet.** The installer compiles but has never
installed. The scheduled task has never been registered. The GUI has never rendered. No file
has ever actually been rotated by this program.

What *is* thoroughly verified is everything platform-neutral, and that's the part where the
subtle bugs live:

- logrotate's scheduling rules, including that `--force` does **not** override `notifempty`,
  `minsize` or `minage` — one test per gate
- DST transitions, `monthly 31` in February, `weekly 7`, a clock moved backwards by a snapshot
  restore
- retention arithmetic, the numbered shift order, the `delaycompress` hand-off
- glob matching — including a test asserting we reject `something.logfile` for `*.log` **and**
  that Windows itself accepts it, via 8.3 short names
- the TOML round-trip, and that generated imports parse back through our own binder
- the gzip output, cross-checked against GNU `gzip`: `gzip -t` passes and `gunzip -N` restores
  the original name and timestamp

Treat this as something to try on a machine you don't mind breaking, and use `--dry-run` first.

---

## Build from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet build WinLogRotate.slnx -c Release
dotnet test  WinLogRotate.slnx -c Release
```

`global.json` pins the SDK band and opts into the Microsoft.Testing.Platform runner, which
xunit.v3 requires on .NET 10.

Everything except the WinForms project builds and tests on Linux — deliberately, and it's how
the engine's portable half stays honest.

---

## License

MIT. See [LICENSE](LICENSE).
