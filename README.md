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
| [`WinLogRotate-Setup-min.exe`](../../releases/latest) | 3.9 MB | Only for the GUI — the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0), fetched for you if missing |
| [`WinLogRotate-Setup-full.exe`](../../releases/latest) | 44.4 MB | **No** — everything is inside |
| [`WinLogRotate-Setup.exe`](../../releases/latest) | 44.4 MB | Asks which of the two to install |

| Portable | Size | Needs anything installed? |
| --- | ---: | --- |
| [`WinLogRotate-min.zip`](../../releases/latest) | 5.1 MB | Only for the GUI |
| [`WinLogRotate.zip`](../../releases/latest) | 46.6 MB | **No** |

Inside a portable zip: `winlogrotate.exe` (11.3 MB, native, zero dependencies) and
`winlogrotate-gui.exe` (1.0 MB). `SHA256SUMS.txt` covers every download.

Figures are from v0.3.0. They were stale for five releases because nothing recorded the real
number; CI now writes the measured binary size to every run's summary.

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
| `notify show` / `status` | What is configured to report failures, and what it last decided |
| `notify test` / `reset` | Send a real test message; clear a suppressed channel |
| `notify set-secret` | Store a provider's credential and point the config at it, in one step |
| `secret set` / `list` / `remove` / `test` | The encrypted credential store |
| `update check` | Whether a newer release exists |

</details>

---

## Configuration

`C:\ProgramData\WinLogRotate\config.toml` holds defaults; one file per job in `conf.d\`.
Every `[job]` key, with its type and default, is in
[configuration](docs/configuration.md).

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

`lockstrategy = "auto"` asks the file what its writer permits and picks accordingly — rename if it
allows `FILE_SHARE_DELETE`, copytruncate if it allows writing, a copy if it allows only reading,
and nothing at all if it allows none of those, which is what log4net's default `ExclusiveLock`
looks like. And `copytruncate` is watched: if the writer resumes at a cached offset and leaves NTFS
to zero-fill the gap, that is detected from the NUL run where the truncation left the file, and the
strategy is refused for that path from then on. See [hooks](docs/hooks.md) for the other half.

`postrotate` replaces `kill -HUP`, which Windows doesn't have: `service:paramchange:NAME` sends
`SERVICE_CONTROL_PARAMCHANGE`, `event:Global\Name` signals a named event, and `command:` runs a
program — no shell, ever, and an ambiguous command line is refused rather than guessed at. Hooks run
once per job and only when a live log actually moves; every one has a timeout that kills the whole
process tree; a failing `prerotate` skips the job untouched while a failing `postrotate` leaves the
rotation standing. All of it is refused outright if the config directory isn't locked down. See
[hooks](docs/hooks.md).

The same target grammar reports failures. `[notify]` sends **one message per run, only when
something changed**, over `smtp:`, `http:`, `pushover:` or `eventlog:` — with credentials in an
encrypted store rather than in the config file, a wall-clock budget so alerting can never delay a
rotation, and a breaker so one dead webhook cannot silence the rest. `winlogrotate notify test`
proves a channel works before you need it. See [notifications](docs/notifications.md).

The GUI's Notifications page shows all of that and can store a credential — over a private named
pipe, because elevating a child process on Windows makes redirecting its standard input
impossible, and a password has no business on a command line that `Win32_Process` exposes and 4688
audit events record verbatim.

> **Hooks need a per-machine install.** They run commands, and that is only safe from a directory
> an ordinary account cannot write. A per-user install keeps its configuration in
> `%APPDATA%\WinLogRotate\`, which its owner can necessarily write — so hooks are refused there,
> permanently and by design. Nothing is wrong with such an install and there is nothing to repair;
> `winlogrotate doctor` says so rather than reporting it as a fault. Everything else — rotation,
> compression, retention, scheduling, and reporting failures by email or webhook — works exactly
> the same either way. The one further exception is `eventlog:`, which needs a source only an
> administrator can register.

Coming from Linux? `winlogrotate import \\srv\etc\logrotate.d\nginx` converts it. One way, and
nothing is guessed: `kill -USR1` becomes `service:paramchange:nginx`, but anything that can't be
translated exactly is written into the output **as a TODO comment** and the job is created
disabled.

### Where things live, and the history

Per-machine installs keep everything in `C:\ProgramData\WinLogRotate\` — `config.toml`,
`conf.d\`, `state.json` (the rotation clocks) and `journal\`. Per-user installs use
`%APPDATA%\WinLogRotate\`, and consequently cannot run hooks (see above). `winlogrotate doctor`
prints them all.

The **journal** is the record of every file compressed, moved or deleted, and the rule that
caused it. It is what the GUI's History page shows and what `winlogrotate journal` queries —
deliberately not Task Scheduler's Last Run Result, since the registered task exits 0 when a run
overlaps and `0x0` therefore proves nothing.

It **records two lines per operation and reports one**, and both halves of that are deliberate.
Each destructive step is journaled once as `plan` and once as `apply`, so a run killed between
them leaves a readable record of an intention that was never carried out. A person reading it
wants the last word instead: told "would delete" and then nothing, you cannot tell a completed
deletion from an abandoned one.

So `winlogrotate journal` reports one line per operation — the half that settled it, or the
plan half where nothing ever did, which is exactly how a run that died mid-operation shows
itself. Nothing is hidden by that, which is why it is the default; `--all` shows the record as
it was written, for auditing whether the record itself is well formed.

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
ran the command and to the Windows Event Log at Warning and above (see
[diagnostics](docs/diagnostics.md) for the event-ID table); the record of what was
*done* is the journal. A third half-used sink would be one more thing to rotate and one more
place to look.

See [logrotate compatibility](docs/logrotate-compatibility.md) for what matches, what
deliberately differs, and what cannot exist on Windows.

---

## Status, honestly

**v0.10.x.** The installer and scheduler are verified end to end on Windows. Rotation itself ran
for the first time in 0.4.0 — before that the engine skipped every `kind = "rotate"` job silently,
which is to say the headline feature had never executed at all. It is verified only against files
nothing holds open. The GUI has still never rendered a window; what it does with the CLI's
output — following the stream, wording an exit code, reading a failure out of an envelope — now
lives in a plain library and is covered, which is the part that was silently wrong.

**And the planner was wrong in five ways until 0.11.0.** No test anywhere had ever *executed* a
rotate plan — the executor refuses to run off Windows, and every planner test stopped at the list
of intentions, asserting that an operation of the right kind existed and never what it would do to
a directory. Underneath that: `delaycompress` renamed the archive compression had just consumed,
failing one operation a night for ever; two spellings of one generation ended the whole run at
exit 4, from the exact state the crash-recovery path warns about and promises the planners cope
with; `maxage` deleted yesterday's archive and reported the month-old one it had left behind;
archives above the retention count were probed for at up to a thousand existence checks per log
and then never touched; and `rotate = n` kept `n + 1` under `dateext`. A plan is now judged by the
directory it leaves behind, and one is carried out on real files on every Windows build.

### What has actually run on Windows

Every push exercises an `installer-smoke` job on a fresh GitHub-hosted Windows Server 2025
runner: it builds a real installer, installs it silently, and asserts the result — then
upgrades, uninstalls, and does the per-user variant too.

```
installed to C:\Program Files\WinLogRotate
ACL ok: O:BAG:SYD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)
task LastTaskResult = 0x0
  run.start  (dry run)
  [smoke] skip C:\smoke\logs\app-2026-09-07.log  (newest file - the application is still writing it)
  [smoke] would delete C:\smoke\logs\app-2026-08-03.log  (rotate = 2 keeps 2 archive(s); this is number 3)
  [smoke] would compress C:\smoke\logs\app-2026-08-05.log -> C:\smoke\logs\app-2026-08-05.log.zip  (compress = zip)
dry run: 2 job(s), 5 operation(s) planned. Nothing was changed.
  [smoke-rotate] did rename C:\smoke\rot\svc.log -> C:\smoke\rot\svc.log.1  (due; lockstrategy = rename)
  [smoke-rotate] did create C:\smoke\rot\svc.log  (recreating the log the writer expects to find)
  [smoke-rotate] did run hook C:\Windows\System32\cmd.exe /c echo ran> C:\smoke\rot\hook-ran.txt  (postrotate)
  [smoke] did compress C:\smoke\logs\app-2026-08-05.log -> C:\smoke\logs\app-2026-08-05.log.zip  (compress = zip)
2 job(s), 7 operation(s), 58.6 KB freed, 0 failure(s).
upgrade took 18.3s
```

Proven, on a real machine: the silent per-machine install; the configuration directory coming
out with exactly the intended descriptor, protected and administrator-only; the Event Log source;
PATH edited without harming what was already there; the scheduled task registered with every
setting Task Scheduler gets wrong by default; **SYSTEM running the binary and actually deleting
and compressing files**; an upgrade blocking on a held rotation mutex for 18.3s and resuming the
moment it was released; a silent uninstall keeping the configuration; `/PURGEDATA` removing it;
and a per-user install leaving HKLM, the machine PATH and the Event Log alone.

That job found three bugs nothing else could have. The scheduled task **never registered at all**
in v0.0.1 — `LogonType: ServiceAccount` is a value from the COM enumeration and does not exist in
the XML schema, so `schtasks` rejected the file, and because the installer treats a failed
registration as non-fatal the install reported success with nothing scheduled to run.
`NT AUTHORITY\SYSTEM` is localised and would have failed on a German machine, so the account is
now named by SID. And a per-user install was editing the machine PATH, because scope was taken
from elevation rather than from the install.

### What has not

- **The installed product still rotates one file, once, with compression off.** `installer-smoke`
  runs a real `rotate` job as SYSTEM, and that is the configuration it runs — `rotate = 3`,
  `compress = false`, from an empty directory. It is the one shape in which none of the five
  planner defects above can occur, which is why they survived on a green pipeline. Multi-generation
  chains, `delaycompress`, `maxage` and `dateext` are exercised against real files only by the
  Windows unit suite, and against a directory model everywhere else.
- **The GUI has never rendered.** No window, no theme, no dialog. The runner is Server and every
  install is silent, so the installer's own wizard pages have never been drawn either. That now
  includes the Notifications page and the credential dialog; the pipe *behind* that dialog is
  covered on the Windows leg, but nothing has ever clicked the button that uses it.
- **Nothing unelevated.** The runner is an administrator throughout, so the read-only banner, the
  "needs administrator" refusals and the UAC child-process elevation are untested.
- **No writer that genuinely misbehaves.** The NUL-fill is reproduced by a handle that caches its
  own file offset and resumes at it — which is what log4net and most C++ `std::ofstream`
  implementations do — but it is not log4net. No runner has IIS, SQL Server or http.sys installed,
  so the share masks those take are read from documentation rather than observed.
- **Nothing probed as SYSTEM against a handle held by a different account.** Every probe in CI is
  run by, and against a handle held by, the same administrator. `ProbeIdentity` exists because a
  verdict reached by SYSTEM says nothing about what a desktop user can do to the same file, and
  that asymmetry has been reasoned about and never seen.
- **The retry backoff is untested above one attempt.** `RetryPolicy` sleeps for real, deliberately,
  because it is waiting for another process to release a handle and no test clock can hurry that
  along. That the delay doubles and caps at five seconds is asserted as arithmetic, never as
  elapsed time.
- **`auto` resolving to `rename` is the one case CI cannot tell from the old bug.** Before
  milestone 14 `auto` silently meant `rename`, so that case produces identical files either way. It
  is separated only by the journal recording which strategy was chosen.
- **The .NET bootstrap is skipped** (`/NORUNTIME`), so the winget detection and install path is
  unexercised.
- **English locale, no domain, no UNC paths, no IIS installed.**
- **The Windows Service host is not implemented.** `host use service` says so and refuses.
- **No notification has ever been delivered on Windows by CI.** The senders and their whole
  decision layer are exercised on the Linux leg against local listeners and fakes, but no runner
  has yet sent real mail, reached a real webhook or written an `eventlog:` digest.
- **No `service:` or `event:` hook has run against a real target in CI.** A `command:` hook does
  run as SYSTEM in the installer smoke, out of the directory the installer locked down, and the run
  asserts that a dry run did not fire it, a rotation did, and a run that rotated nothing did not.
  What has never happened on a runner is `service:paramchange:` reaching a real service, or
  `event:` signalling a real waiter.

### What is thoroughly tested, everywhere

1,188 tests, and the platform-neutral half is where the subtle bugs live:

- logrotate's scheduling rules, including that `--force` does **not** override `notifempty`,
  `minsize` or `minage` — one test per gate
- DST transitions, `monthly 31` in February, `weekly 7`, a clock moved backwards by a snapshot
  restore
- retention arithmetic, the numbered shift order, the `delaycompress` hand-off — and, since the
  planner turned out to be wrong about all three, the **directory each plan leaves behind**: which
  original file ends up under which name, rather than which operations were intended
- glob matching — including a test asserting we reject `something.logfile` for `*.log` **and**
  that Windows itself accepts it, via 8.3 short names
- the TOML round-trip, and that generated imports parse back through our own binder
- the gzip output, cross-checked against GNU `gzip`: `gzip -t` passes and `gunzip -N` restores
  the original name and timestamp
- that changing one value in a config file changes exactly one line and every comment survives —
  the property the whole TOML decision rests on, and which nothing asserted until there was an
  API able to change a value at all
- notification delivery: retries, the wall-clock budget and the breaker, all driven by a fake
  clock against fake transports, so no test sleeps and none opens a socket — plus the rule that a
  message counts as reported only when every channel that was tried accepted it
- that a templated webhook body cannot be broken out of by a log file called `a"b.log`

Use `--dry-run` first, and prefer a machine you don't mind breaking.

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
