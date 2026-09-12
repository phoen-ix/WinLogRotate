# Configuration

WinLogRotate reads one `config.toml` and then every `*.toml` file in `conf.d`, in ordinal
order so that two machines given the same files load them the same way.

| File | Holds |
|---|---|
| `config.toml` | `schema`, `[defaults]`, `[notify]`, `[journal]` |
| `conf.d/*.toml` | one `[job]` per file |

Where those live depends on how it was installed — `C:\ProgramData\WinLogRotate` for a
per-machine install, `%APPDATA%\WinLogRotate` for a per-user one. `winlogrotate doctor`
prints the paths in use.

A key that is not a setting is reported and ignored, never silently dropped. If it is one
character away from a real key you are told which one you probably meant.

## The shortest job that works

```toml
schema = 1

[job]
name  = "iis"
paths = ["C:/inetpub/logs/LogFiles/**/u_ex*.log"]
```

Everything else comes from the built-in defaults below. Forward slashes are accepted
everywhere and are usually easier to read in TOML than escaped backslashes.

## Every `[job]` key

### Structural

| Key | Type | Default | What it does |
|---|---|---|---|
| `name` | string | *required* | Identifies the job in the journal, in diagnostics and in notifications. Must be unique across all files. |
| `paths` | list of strings | *required* | Glob patterns. `*` and `?` match within one segment, `**` matches across segments, `[abc]` matches a character set. |
| `kind` | `"rotate"` \| `"manage"` | `"rotate"` | `rotate` moves the live log aside itself. `manage` leaves rotation to the application and only compresses and retains what it left behind. |
| `enabled` | bool | `true` | A disabled job is not considered at all. |
| `allowdangerous` | list of strings | *(empty)* | Directories this job may work in despite the guard refusing them. See **Dangerous paths** below. |

### Schedule

Frequency keywords are mutually exclusive and last-one-wins, exactly as in logrotate.

| Key | Type | Default | What it does |
|---|---|---|---|
| `hourly` | bool | — | Rotate every hour. |
| `daily` | bool | `true` | Rotate every day. The default when nothing else is set. |
| `weekly` | bool | — | Rotate every week; see `weekday`. |
| `monthly` | bool | — | Rotate every month; see `monthday`. |
| `yearly` | bool | — | Rotate every year. |
| `schedule` | string | `"daily"` | The same choice written as a value: `hourly`, `daily`, `weekly`, `monthly`, `yearly`. |
| `size` | size | `1 MiB` | Rotate on size alone, ignoring the calendar. Accepts `100k`, `10M`, `1G`. |
| `weekday` | 0–7 | `0` | 0 is Sunday through 6 Saturday. 7 means every seven days regardless of weekday. |
| `monthday` | 0–31 | `0` | Day of the month for `monthly`. |

### Retention

| Key | Type | Default | What it does |
|---|---|---|---|
| `rotate` | int | `7` | How many old generations to keep, exactly — dated or numbered. `0` discards immediately; `-1` keeps everything and prunes by `maxage` alone. |
| `start` | int | `1` | The number the first archive gets, so `app.log.1`. |
| `maxage` | days | *(none)* | Delete archives older than this, whatever `rotate` says. |
| `minage` | days | *(none)* | Refuse to rotate a log younger than this. |
| `minsize` | size | *(none)* | Refuse to rotate a log smaller than this, even when it is due. |
| `maxsize` | size | *(none)* | Force an early rotation once the log exceeds this. |
| `maxfiles` | int | `1000` | Refuse a pattern matching more files than this. A pattern matching 40,000 files is a typo far more often than a plan. |

Setting `minsize` above `maxsize` cancels both, and is reported.

**`rotate = 0` still rotates.** The log is moved aside so the writer gets a fresh file, and the
archive is deleted in the same pass rather than compressed first — the rotation clock only
advances for a log that was really moved, so a job that merely emptied its log would be due again
every time it was considered. With `lockstrategy = copytruncate` that means the log is copied in
full to an archive removed seconds later, so the volume needs room for one copy in order to keep
nothing.

### Archiving

| Key | Type | Default | What it does |
|---|---|---|---|
| `compress` | bool | `true` | Compress rotated archives. |
| `compresstype` | `"zip"` \| `"gzip"` \| `"none"` | `"zip"` | `compress = false` and `compresstype = "none"` mean the same thing. |
| `delaycompress` | bool | `false` | Leave the newest archive uncompressed for one cycle, for a writer that has not let go of it yet. Does nothing without `compress`. |
| `dateext` | bool | `false` | Name archives by date rather than by number. |
| `dateformat` | string | `"-yyyyMMdd"` | The .NET format string appended when `dateext` is set. |
| `olddir` | path | *(beside the log)* | Where archives go. A relative path resolves against each log's own directory, matching logrotate. |
| `createolddir` | bool | `false` | Create `olddir` if it does not exist. Without this, a missing `olddir` refuses the job and says so. |

**`dateformat` is load-bearing after the fact.** Archives are found again by globbing the
format, so changing it on a job that already has dated archives orphans every existing one —
they stop being counted for `rotate` and stop being deleted by `maxage`. `dateext` also
refuses to overwrite an existing dated archive, so a format coarser than the schedule fails
on every second rotation of a period; that combination is refused at config time.

### Behaviour

| Key | Type | Default | What it does |
|---|---|---|---|
| `missingok` | bool | `false` | Matching no files is not an error. |
| `notify` | bool | `true` | Whether this job's failures are reported. See [notifications](notifications.md). |
| `notifempty` | bool | `true` | Do not rotate an empty log. |
| `lockstrategy` | `"rename"` \| `"copytruncate"` \| `"copy"` \| `"auto"` | `"rename"` | How to get the live log out of the way when another process holds it open. `auto` probes the file and takes the best available. |
| `livefiles` | int | `1` | On a `manage` job, how many newest files to leave alone because the application is still writing them. |
| `retrycount` | int | `5` | Attempts for an operation blocked by a sharing violation. |
| `retryinterval` | ms | `100` | Starting delay between those attempts; it doubles. |

### Hooks

| Key | Type | Default | What it does |
|---|---|---|---|
| `prerotate` | list of strings | *(empty)* | Commands to run before a live log moves. A failure abandons the job. |
| `postrotate` | list of strings | *(empty)* | Commands to run after a live log has moved. |
| `hook_timeout` | duration | `"60s"` | How long a hook may take. |

Hooks run only when a live log actually moves, and only from a configuration directory this
machine considers safe to execute from. They have their own page: [hooks](hooks.md).

## Archives elsewhere: `olddir` and `createolddir`

```toml
[job]
name         = "app"
paths        = ["C:/app/logs/*.log"]
olddir       = "D:/archive/app"
createolddir = true
```

`olddir` is guarded exactly like the paths a job reads: an archive directory inside a
protected location is refused, and `createolddir` never creates a directory somewhere the
guard would then refuse to write. The check happens at plan time, so `--dry-run` tells you
about it without touching anything.

## Dangerous paths: `allowdangerous` and `maxfiles`

WinLogRotate refuses to delete inside Windows, System32 and Program Files, and refuses a
pattern anchored at a volume root. If a job genuinely needs one of those, name the directory:

```toml
[job]
name           = "cbs"
paths          = ["C:/Windows/Logs/CBS/*.log"]
allowdangerous = ["C:/Windows/Logs/CBS"]
```

Four things are worth knowing.

- **An entry unlocks a directory**, not a pattern. That is deliberate: the job also compresses
  and deletes `*.log.1` and `*.log.2.zip`, and an entry that matched only the pattern would
  refuse every one of those at the moment it tried.
- **An entry may not be a protected root, or a volume root.** `C:/**` and `C:/Windows/**` are
  refused. There is no global switch, because that is the switch everyone flips once during an
  incident and never flips back.
- **It is per job.** An entry in `[defaults]` is reported and ignored.
- **A writable `conf.d` disables it entirely.** If a non-administrator can write to the
  configuration directory, overrides are not honoured and the run says so — the same rule that
  refuses hooks there, for the same reason. `winlogrotate doctor` shows the verdict.

`maxfiles` is a separate ceiling and `allowdangerous` does not raise it. A refusal there means
the pattern matched more files than expected, which is a different question from whether the
location is allowed.

## When a key is wrong

A misspelling is a warning, not an error, and names the line and column. One job's mistake
never stops the others: a job that fails validation is skipped and named, the rest of the
machine rotates, and the run exits 1. Only a fault with no single job to blame — an
unparseable `config.toml`, a broken `[notify]` table, two jobs with the same name — stops
everything, and that exits 2.

`winlogrotate config check` reports all of it without rotating anything.

## Defaults, and why they differ from logrotate

Upstream's built-in defaults are `size 1M` + `rotate 0` + `ifempty`, which means a job with no
directives deletes your logs at one megabyte and keeps nothing. That is defensible for a
config language nobody writes bare, and indefensible behind a GUI where a half-filled form is
a normal intermediate state. The differences are set out in
[logrotate compatibility](logrotate-compatibility.md).

## Other tables

`[defaults]` takes any key from this page except `name`, `paths`, `kind`, `enabled` and
`allowdangerous`, and applies it beneath every job. `[notify]` and `[journal]` are documented
in [notifications](notifications.md).
