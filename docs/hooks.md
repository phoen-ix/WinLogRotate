# Hooks

A hook is something WinLogRotate runs around a job's rotation. It is the Windows answer to
logrotate's `prerotate` and `postrotate` scripts, and to `kill -HUP`, which Windows does not have.

```toml
[job]
name  = "iis"
paths = ["C:/inetpub/logs/LogFiles/W3SVC1/*.log"]

postrotate = ["service:paramchange:W3SVC"]
```

## The grammar

Three schemes, and nothing else runs.

| Written as | Does | Notes |
|---|---|---|
| `service:paramchange:NAME` | sends `SERVICE_CONTROL_PARAMCHANGE` | the `kill -HUP` equivalent |
| `service:NAME` | the same | the verb defaults to `paramchange` |
| `command:C:\path\to\program.exe args` | runs a program | no shell, ever |
| `C:\path\to\program.exe args` | the same | a hook with no scheme is a command line |
| `event:Global\Name` | signals a named kernel event | the event must already exist |

`http:`, `smtp:`, `pushover:` and `eventlog:` are **not** hooks. They report, and reporting belongs
in `[notify]`, where it gets a time budget, a circuit breaker and change detection. Writing one in
`postrotate` is refused with `LR9003` naming where it should go.

### `service:` sends one control code

`paramchange` only. It is the one verb that maps onto what `postrotate` is for, and the one that
cannot cause an outage. `service:stop:`, `service:start:` and `service:restart:` are refused,
naming the alternative:

```toml
postrotate = ["command:C:\\Windows\\System32\\net.exe stop MyService"]
```

A rotation that stops a service should have to say so in full, rather than reading like a reload.

### `command:` never guesses which program it means

There is no shell, so something has to decide where the program ends and its arguments begin. When
that is ambiguous, the hook is **refused** rather than guessed at:

```toml
# Refused: is the program C:\Program, or C:\Program Files\App\reload.exe?
postrotate = ["command:C:\\Program Files\\App\\reload.exe --now"]

# Accepted, and unambiguous.
postrotate = ["command:\"C:\\Program Files\\App\\reload.exe\" --now"]
```

This is deliberately **not** Windows' own rule. `CreateProcess`, handed the first form, tries
`C:\Program.exe` before `C:\Program Files\App\reload.exe` — a well-known privilege-escalation class,
and this code runs as SYSTEM.

**The decision is made from the text alone.** An extension — `.exe`, `.com`, `.bat`, `.cmd` — says
where the program ends; nothing else does, and the disk is never consulted. So the same hook is
accepted or refused identically on every machine and in every order, you can tell which by reading
the file, and `winlogrotate config check` cannot disagree with `run` because something appeared
between them.

> **Fixed in v0.11.1, and worth knowing if you are upgrading.** Until then the first token was
> looked up on disk, so a file at `C:\Program` made the refused form above resolve to
> `C:\Program` — with `Files\App\reload.exe --now` as its arguments — and start it as SYSTEM.
> That is the first leg of the rule this section says is not used. One consequence for existing
> configurations: an unquoted program path that contains a space and has **no** extension is now
> refused rather than looked up. Quote it, as the second example does.

Two further rules, for the same reason:

- **The program must be an absolute path.** `command:net stop x` is refused; resolving a bare name
  through `PATH` would let a planted executable in a writable directory run as SYSTEM. Write
  `command:C:\Windows\System32\net.exe stop x`.
- **Batch files are refused**, because `CreateProcess` cannot start one. Run it through the
  interpreter that can: `command:C:\Windows\System32\cmd.exe /c C:\tools\reload.bat`.

Arguments are passed as a list, not as a joined string, so an argument containing a space or a
quote cannot become two arguments — or part of the program name.

### `event:` opens, never creates

`event:Global\AppReload` opens an existing named event and signals it. If nothing has created that
event, the hook **fails** rather than quietly creating one and setting it — a hook that reports
success while the program it was meant to poke is not even running is worse than no hook at all.

## When they run

**Once per job, not once per file.** A directory of forty logs signals a service once. This is
logrotate's `sharedscripts` behaviour, and it is the only behaviour here — `sharedscripts` and
`nosharedscripts` in an imported file are recognised and noted, not honoured.

**Only when a live log actually moves.** A night on which nothing was due, or on which only an old
archive was compressed or deleted, runs no hooks. A reload hook that fired because of a deletion
would page somebody about a rotation that never happened.

**A dry run runs none of them.** `--dry-run` reports the hook it would run and does not run it.

## When one fails

The two stages are deliberately asymmetric, and this is logrotate's asymmetry:

| Stage | On failure |
|---|---|
| `prerotate` | **the job is skipped and no file is touched** |
| `postrotate` | the rotation stands; the failure is reported against the job |

A `prerotate` hook is the job's precondition. If the service did not stop or the buffer was not
flushed, rotating anyway does exactly the damage the hook was written to prevent. A `postrotate`
hook runs after the files have already moved, so there is nothing to undo — rolling a rotation back
because a reload script exited 1 would be much the more surprising of the two.

Either way the run's exit code is 1 and the failure is `LR3103`, with the exit code or the timeout
and **whatever the hook wrote to standard error**.

Standard output is deliberately not repeated, not even when standard error is empty. That string
ends up in the notification digest that is mailed or posted to a webhook, and in the Windows
Application log. `stderr` is where a program explains itself; `stdout` is its product - a token, a
connection string, a report - and plenty of tools print theirs and only then discover they cannot
finish. If a hook fails without saying anything, `LR3103` says so and tells you to run it by hand.

## Timeouts

Every hook has one. It defaults to 60 seconds and is set per job:

```toml
hook_timeout = "120s"
```

A hook that outlives it is killed, **along with its children** — a hook that launches a helper and
returns would otherwise leave the helper holding a pipe and outliving the rotation it belonged to.

The timeout is also clamped by what is left of the run's own deadline. The scheduled task carries an
`ExecutionTimeLimit`, and a task killed at that limit is reported by Task Scheduler as `0x41306` —
which is indistinguishable from an operator pressing Stop. A rotation that succeeded would leave
evidence saying it was terminated. So a hook is never the thing that trips it: if there is not
enough time left, the hook is not started, and `LR3103` says so.

## Hooks need a per-machine install

**They run commands, and that is only safe from a directory an ordinary account cannot write.**

Before the first hook of a run is dispatched, the permissions of every path the run took its
configuration from are checked: the data root, `conf.d`, `config.toml`, and each job file inside
`conf.d`. If anyone who is not an administrator can write one of them — or owns it, which amounts
to the same thing — every hook in that run is refused with `LR9003`, and the offending path is
reported once as `LR9001`.

Files, and not only the directory holding them, because rewriting a directory's permissions
recomputes what a child inherits and leaves a child's own entries and its owner alone. An owner can
rewrite permissions at will, so a job file created while `ProgramData` still granted its author
full control stays that author's to edit — and a hook in `[defaults]` inside `config.toml` is
inherited by every job. `winlogrotate host repair --acl` fixes all of them, and every file this
product writes there is left owned by Administrators as it is written.

The paths are judged outermost first and the first refusal is the one reported: fixing a job file
inside a directory an ordinary account can write fixes nothing.

The check is taken **once per run, immediately before the first hook**, not when the configuration
is loaded. A run reads its configuration and then works for an hour, and a directory's permissions
can be changed in between by exactly the person the check exists to stop.

A **per-user install** keeps its configuration in `%APPDATA%\WinLogRotate\`, which its owner can
necessarily write, so hooks are refused there permanently and by design. Nothing is wrong with such
an install and there is nothing to repair: `winlogrotate doctor` says so rather than reporting it
as a fault. Everything else works identically.

To see where you stand:

```
winlogrotate doctor
```

## What is not supported

logrotate has five script kinds. Two map onto this model and three do not:

| logrotate | Here |
|---|---|
| `prerotate` | `prerotate` |
| `postrotate` | `postrotate` |
| `firstaction` | **no equivalent** — it runs whether or not anything rotated |
| `lastaction` | **no equivalent** — same |
| `preremove` | **no equivalent** — it is handed the name of each condemned file |

`winlogrotate import` writes the three unsupported kinds into the generated file as `# TODO:`
comments with the original script quoted beneath, and marks the job for review. It does not emit
them as live keys — a directive that looks like it works and silently does nothing is the failure
this whole area was built to remove.

A key in `[job]` that is not a setting — `postrotae`, or one of those three — is reported every run
as a warning, with a spelling suggestion where there is exactly one candidate.

## Diagnostics

| Code | Means |
|---|---|
| `LR9003` | the hook will not be run: the gate said no, or the hook itself is not usable |
| `LR3103` | the hook ran and failed, timed out, or could not be started |
| `LR9001` | `conf.d` is writable by a non-administrator, so all hooks are refused |

The distinction between the first two is worth an alert rule: "somebody's configuration directory is
writable" and "the reload script returned 1" are different problems, and they go to different
people.
