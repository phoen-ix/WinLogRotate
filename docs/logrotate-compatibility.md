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
| `delaycompress` | The newest generation stays uncompressed for one cycle, and is compressed at the start of the next run so the chain is uniformly named before anything shifts. |
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

### `.zip` by default

`gzip` is what logrotate produces and what log shippers expect; `.zip` is what an administrator
can double-click in Explorer on any Windows version. The audience decided it. Set
`compresstype = "gzip"` per job where the consuming ecosystem expects it - Splunk and Elastic
file inputs auto-decompress `.gz` and generally not `.zip`.

## Not implementable on Windows

| Directive | Why, and what to use instead |
|---|---|
| `su user group` | Windows has no `setuid`. `LogonUser` needs a password; the service-logon path needs `SE_TCB_NAME` and yields a token that cannot open files. Parsed, warned about, ignored. **Instead:** run the whole tool under the identity you want, via the scheduled task's principal or a gMSA. |
| `create mode owner group` | There are no mode bits. A POSIX mode is approximated as a DACL and marked protected, which is honest but lossy. **Instead:** `createsddl` and `createowner`. |
| `mail` / `mailfirst` / `maillast` | Not implemented, and it would be the wrong shape anyway: upstream mails you the rotated log itself, per job. **Instead:** the `[notify]` table, which reports what a whole run did to whoever is on call - see [diagnostics](notifications.md). Configuration, validation and change detection are in place today; delivery is not, and `winlogrotate notify show` says so rather than pretending. |
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
- **Reparse-point refusal.** Any user can create a junction with no privilege at all. While
  elevated, WinLogRotate never follows one, and there is no override.

## Importing an existing config

```
winlogrotate import \\server\etc\logrotate.d\nginx --out conf.d
```

One-way, by design: the Windows-specific keys have no representation in logrotate's syntax, so a
round-trip would be lossy in a way that is worse than not offering it. Anything that cannot be
translated is written into the output **as a comment**, and the job is created disabled, so
nothing runs until a human has looked at it.
