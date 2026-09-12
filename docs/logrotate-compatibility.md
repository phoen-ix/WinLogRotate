# logrotate compatibility

WinLogRotate imitates logrotate's **behaviour**, not its config syntax. The directive names are
kept so that a search for `delaycompress` still finds the answer, but the container is TOML and
the file layout is ours.

This page is the honest account of what matches, what differs, and what cannot exist on Windows.

## Behaviour we reproduce exactly, including the surprising parts

| Behaviour | Note |
|---|---|
| **First sighting records a baseline and rotates nothing** | Delete the state file and nothing rotates that night. This catches everyone once. `--catchup` opts out. |
| **At most one rotation per log per invocation** | A machine off for a month rotates once, not thirty times. Missed intervals are gone, not queued. |
| **Calendar comparison, not elapsed time** | `daily` is due because the day number changed, so 23:58 and 00:02 rotates twice. |
| **`--force` does not override `notifempty`, `minsize` or `minage`** | `--force` sets the initial answer; those three run afterwards and can turn it back off. Genuinely what upstream does. |
| **Frequency keywords are mutually exclusive, last one wins** | They all write the same field. |
| `weekly 7` | Pure seven-day spacing, ignoring the weekday. |
| `monthly 31` in February | Falls back to the last day of the month, or it would never fire. |
| **`dateext` never overwrites** | Rotating twice inside one date-format period is an error, not a silent loss of the earlier archive. Numbered rotation overwrites silently, as upstream does. |
| `delaycompress` | The newest generation stays uncompressed for one cycle, and is compressed at the start of the next run so the chain is uniformly named before anything shifts. With `rotate = 1` or `rotate = 0` that generation is also the one retention disposes of this pass, so it is deleted rather than compressed first. |
| Numbered shift order | From the highest index downward, so a move never lands on a file that has not itself moved yet. |
| Exit codes 0, 1, 3 | Unchanged, and 1 still writes state - a failing job must not make the healthy ones re-rotate forever. |

## Deliberate differences

### Built-in defaults

Upstream's compiled-in defaults are `size 1M` + `rotate 0` + `ifempty`, which means a job with
no directives **deletes your logs at one megabyte and keeps none**. That is defensible for a
config language nobody writes bare. It is not defensible behind a GUI, where a half-filled form
is a normal intermediate state.

| Setting | logrotate | WinLogRotate |
|---|---|---|
| criterion | `size 1M` | `daily` |
| `rotate` | `0` | `7` |
| `compress` | off | on, `zip` |
| `notifempty` | off (`ifempty`) | on |
| `missingok` | off | off |

### Exit code 2

Added, not inherited. Upstream folds "your config has a typo" into exit 1. Windows monitoring
keys off exit codes far harder than cron does, and *nothing was attempted* is genuinely a
different condition from *we rotated forty logs and one was locked*.

### Exit code 4

Added, not inherited, and for a condition logrotate has no code for: an exception escaped a verb.
Everything else this program returns is an outcome it meant to report. 4 means it did not mean
anything - what was and was not done is unknown, and the accompanying `LR1006` diagnostic is a
bug report rather than something to fix on the machine.

It exists because the alternative was worse. Before it, an escaped exception was caught by the
command-line library's default handler, which returned 1 - and 1 promises that a run happened and
that state was written. A crash before the rotation lock was even taken reported itself to
monitoring as a rotation that had happened.

An alert rule should treat 4 as *read the diagnostic, then file a bug*, never as *some files could
not be rotated*.

### `.zip` by default

`gzip` is what logrotate produces and what log shippers expect; `.zip` is what an administrator
can double-click in Explorer on any Windows version. The audience decided it. Set
`compresstype = "gzip"` per job where the consuming ecosystem expects it - Splunk and Elastic
file inputs auto-decompress `.gz` and generally not `.zip`.

### `mail` reports the run, not the log

Upstream's `mail` / `mailfirst` / `maillast` post you the rotated log file itself, per job. We do
not, and the difference is deliberate rather than a gap: a nightly attachment of every log that
rotated is a mailbox nobody reads, and on a Windows file server it is also a compliance problem
nobody asked for.

**Instead:** the `[notify]` table reports *what a whole run did* to whoever is on call - once, per
run, and only when something changed. It delivers over SMTP, webhooks, Pushover and the Windows
Event Log. See [notifications](notifications.md).

If you genuinely want the file, a `command:` hook is the honest way to say so — see
[hooks](hooks.md). It runs behind the configuration-directory check that executing anything named
in a config file requires, which in practice means a per-machine install.

## Not implementable on Windows

| Directive | Why, and what to use instead |
|---|---|
| `su user group` | Windows has no `setuid`. `LogonUser` needs a password; the service-logon path needs `SE_TCB_NAME` and yields a token that cannot open files. Parsed, warned about, ignored. **Instead:** run the whole tool under the identity you want, via the scheduled task's principal or a gMSA. |
| `create mode owner group` | There are no mode bits. A POSIX mode is approximated as a DACL and marked protected, which is honest but lossy. **Instead:** `createsddl` and `createowner`. |
| `shred` | Not implemented; no `sdelete` dependency is taken. |
| `compresscmd` / `uncompresscmd` | Windows ships no `gzip.exe`. Compression is in-process. A config naming `/bin/gzip` is translated with a warning. |

## Things Windows forces on us that logrotate never needed

- **`lockstrategy`.** On Unix a rename always works. On Windows it needs every open handle to
  have permitted `FILE_SHARE_DELETE`, and most loggers do not - so the strategy is explicit per
  job, defaulting to `rename`, which fails loudly rather than quietly doing something else.
  `winlogrotate probe <path>` reports what a given file actually supports.
- **`kind = "manage"`.** No Unix equivalent, and the mode most Windows installations need. See
  the README.
- **`livefiles`.** How many of the newest files the producer may still be writing.
- **`retrycount` / `retryinterval`.** Antivirus and the Search Indexer hold transient handles
  constantly; without retries, rotations fail unpredictably on healthy machines.
- **NUL-fill quarantine.** A writer that caches its own file offset does not seek back to zero
  after `copytruncate`, so NTFS zero-fills the gap and the "rotated" file instantly reappears at
  its old size. Once detected, `copytruncate` is permanently refused for that path.
- **Links are resolved, then judged.** Any user can create a junction with no privilege at all
  (`mklink /J`), so a log directory somebody else controls could be aimed at `System32` and have a
  SYSTEM-privileged retention pass delete files there. Every link is therefore resolved to where it
  really leads, and that target is put through the same protected-location rule a path typed out in
  full would face. A junction onto another volume — the ordinary way to relocate logs off a full
  system drive — is followed. One that arrives somewhere we would have refused anyway is not, with
  `LR9004` naming both the link and its target. **There is no override**, and resolving also closes
  two holes a textual check never could: an 8.3 short name and a `subst`ed drive are different
  spellings of the same directory.

  Linked log *files* are still not rotated — `copytruncate` would empty the target while a rename
  moves the link and leaves the target growing, so following them is a feature with semantics to
  settle rather than a safety fix. What changed is that the skip is now reported instead of silent.

## Importing an existing config

```
winlogrotate import \\server\etc\logrotate.d\nginx --out conf.d
```

One-way, by design: the Windows-specific keys have no representation in logrotate's syntax, so a
round-trip would be lossy in a way that is worse than not offering it. Anything that cannot be
translated is written into the output **as a comment**, and the job is created disabled, so
nothing runs until a human has looked at it.

## Scripts

logrotate has five script kinds. Two of them map onto this model; three do not, and the differences
are real rather than cosmetic.

| logrotate | Here |
|---|---|
| `prerotate` | `prerotate` |
| `postrotate` | `postrotate` |
| `firstaction` | **not supported** - it runs whether or not any log was due |
| `lastaction` | **not supported** - same |
| `preremove` | **not supported** - it is handed the name of each condemned file |

`sharedscripts` and `nosharedscripts` are recognised and not honoured: **hooks always run once per
job here**, which is `sharedscripts` behaviour. Running a reload once per matched file would signal
a service forty times for a directory of forty logs.

The other divergence is what a script *is*. There is no shell, so a hook is a program and its
arguments, a service control code or a named event - never a shell fragment. `winlogrotate import`
translates `kill -HUP` and `kill -USR1` into `service:paramchange:NAME`; anything else is preserved
as a `# TODO:` comment with the original quoted beneath, and the job is written `enabled = false`.
A half-translated script that runs the wrong thing as SYSTEM is worse than one that does not run.

See [hooks](hooks.md) for the grammar, the timeout, the configuration-directory requirement and
what a failure in each stage costs.

## Locked files

logrotate runs where a rename always works. Windows is not that: renaming or deleting a file needs
`DELETE` access, and that is only granted when **every** existing handle was opened with
`FILE_SHARE_DELETE` — which MSVCRT's `fopen("a")`, .NET's default `FileShare.Read`, IIS, http.sys
and SQL Server all withhold.

| `lockstrategy` | What it does |
|---|---|
| `rename` | Atomic `MoveFileEx`, then recreate the log. **The default**, because it fails loudly rather than silently doing something else. |
| `copytruncate` | Copy the contents aside, then truncate in place. The inode never changes, so the writer keeps working. |
| `copy` | Archive a snapshot and leave the original alone. It keeps growing. |
| `auto` | Ask the file which of those its writer permits, and take the best. Probed fresh every run. |

`copytruncate` carries a hazard logrotate does not have on Linux. A writer that caches its own file
offset — log4net, and many C++ `std::ofstream` implementations — does not seek back to zero when
the file is truncated underneath it. Its next write lands at the offset it remembers and NTFS
zero-fills everything in between, producing a file of NUL bytes plus one line of log that reappears
at its old size every rotation, for ever.

That is detected. The size the file was truncated from and the offset it was left at are recorded,
and the next run looks at the bytes **at that offset** — a NUL run there is the signature itself,
where a healthy writer leaves log text. Once confirmed the verdict is permanent for that path:
`copytruncate` is refused (`LR3102`, then `LR3003` on every later run), `auto` takes the next best
strategy instead, and a job that asks for `copytruncate` explicitly is skipped rather than
substituted — it said what it wanted, and neither alternative is what it asked for.

A path that returns to its old size but carries log text at the resume offset is **not** quarantined.
A log producing four gigabytes a day is back at four gigabytes every morning for entirely innocent
reasons, and the verdict cannot be undone.
