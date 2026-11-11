# Status, honestly

What has been verified, on what, and what has not. Keep it true when behaviour or test coverage
changes; the [README](../README.md#status-honestly) carries a summary.

**v0.21.x.** The installer and scheduler are verified end to end on Windows. Rotation itself ran
for the first time in 0.4.0 — before that the engine skipped every `kind = "rotate"` job silently,
which is to say the headline feature had never executed at all. It is verified only against files
nothing holds open. The GUI's window is drawn on every CI build now, by the docs tour that makes
the screenshots, but nothing has been saved through it there; what it does with the CLI's
output — following the stream, wording an exit code, reading a failure out of an envelope — lives
in a plain library and is covered, which is the part that was silently wrong.

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

**And manage mode never pruned its own archives until after 0.21.0.** A manage job's pattern names
what the application writes — `u_ex*.log` — and what the job compressed last night is
`u_ex*.log.zip`, which that pattern does not match. From the second night on, `rotate` and `maxage`
counted only the files not yet compressed, so every archive the job made was kept for ever; the IIS
example in the [README](../README.md#and-the-thing-that-actually-fills-windows-disks) was exactly that job, and the installer smoke only ever ran it for one night. A
manage job now counts the archives it compressed, so **the first run after upgrading prunes the ones
that piled up** to `rotate` and `maxage`. `winlogrotate run --dry-run` lists them first.

The same job kind judged every file a pattern matched as one series, whichever folder it was in.
`C:/inetpub/logs/LogFiles/**/u_ex*.log` therefore left only the newest file of all the sites alone,
planned every other site's live log for compression, and kept `rotate` files across all of them.
Each folder is its own series now; a manage job whose pattern spans folders keeps more after
upgrading, never less.

The same audit found values that `config check` accepted and that destroyed data: `maxage = -1`
deleted every archive, and a number where a name belongs — `compresstype = "3"` — compressed a log
over itself and deleted it. Those are refused now, along with negative sizes; the job that holds
one is skipped and named until it is fixed, and the rest of the machine rotates.

## What has actually run on Windows

Every push exercises an `installer-smoke` job on a fresh GitHub-hosted Windows Server 2025
runner: it builds a real installer, installs it silently, and asserts the result — then
upgrades, uninstalls, and does the per-user variant too. **A release waits for it**, which it did
not until v0.10.1: the release workflow was independent of CI, so v0.9.0 was published from a
commit whose `installer-smoke` had failed.

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
setting Task Scheduler gets wrong by default; **SYSTEM running the binary and rotating a log
nothing else could have rotated** — a size-triggered job staged below its threshold while the
administrator runs go past, grown past it afterwards, with the archive asserted absent before the
task starts; an upgrade blocking on a held rotation mutex for 18.3s and resuming the
moment it was released; a silent uninstall keeping the configuration; `/PURGEDATA` removing it;
and a per-user install leaving HKLM, the machine PATH and the Event Log alone.

That job found three bugs nothing else could have. The scheduled task **never registered at all**
in v0.0.1 — `LogonType: ServiceAccount` is a value from the COM enumeration and does not exist in
the XML schema, so `schtasks` rejected the file, and because the installer treats a failed
registration as non-fatal the install reported success with nothing scheduled to run.
`NT AUTHORITY\SYSTEM` is localised and would have failed on a German machine, so the account is
now named by SID. And a per-user install was editing the machine PATH, because scope was taken
from elevation rather than from the install.

## What has not

- **The installed product still rotates narrow configurations.** `installer-smoke` now asserts
  the compressing job's whole output directory — which archives survive, that the live log was not
  touched, and that the zip opens and holds the log it is named for — and gives the scheduled task
  a size-triggered job only it can do. What it still does not exercise is `delaycompress`,
  `maxage`, `dateext` or a multi-generation chain; those meet real files only in the Windows unit
  suite, and a directory model everywhere else. Milestone 23's five planner defects all lived in
  that gap.
- **The GUI is used only from a build, by an administrator, against a local sink.** ci.yml's
  `e2e` job drives the console end to end (`tests/WinLogRotate.Gui.Tests/E2E`): it saves, toggles
  and removes jobs, runs Rotate now, stores a credential down the pipe, sends a test to a local
  webhook, repairs permissions, switches the scheduled task and opens in the dark theme, and checks
  the files each leaves. It does so on the JIT build beside the tests, not on an install, and never
  through a UAC prompt, an OS file dialog, Explorer, a real mail server or the live update feed;
  `tests/WinLogRotate.Gui.Tests/COVERAGE.md` lists what is left and why. Every install is silent,
  so the installer's own wizard pages have never been drawn either.
- **Nothing unelevated.** The runner is an administrator throughout, so the read-only banner, the
  "needs administrator" refusals and the UAC child-process elevation are untested.
- **The installer's own elevation.** It opens without UAC and asks only when all-users is picked;
  that copy then continues at the run-host page with `/RESUME`, and an all-users uninstall hands
  over to an elevated `uninstall.exe` with its switches. The runner already holds a full admin
  token, so neither handoff happens in CI. Checked by hand: a standard user picks all-users → one
  prompt → the installer opens on *Scheduling* with Back disabled → the install lands in Program
  Files; *Installed apps* → Uninstall → one prompt → removed, data kept.
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
  unexercised, and so is the `-min` installer's refusal (exit 4) when the runtime is missing: the
  runner always has it. The updater's own check is covered on the Linux leg with a fake probe.
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

## What is thoroughly tested, everywhere

The platform-neutral half of the suite is where the subtle bugs live:

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
