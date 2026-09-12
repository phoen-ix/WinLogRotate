# Diagnostics and the Windows Event Log

WinLogRotate has no application log file of its own. There are three channels, and each exists
for a different reader:

| Channel | Reader |
|---|---|
| **stdout / stderr** | whoever typed the command |
| **Windows Event Log** | whoever is monitoring the machine |
| **the journal** (`winlogrotate journal`) | whoever is asking what happened to a specific file |

A fourth, half-used sink would be one more thing to rotate and one more place to look.

## What reaches the Event Log

Everything at **Warning and above**, written to `Application` under the source `WinLogRotate`.
Informational progress does not: a system log is not where progress belongs.

The source is registered by the installer, because creating one requires administrator rights
and the unelevated CLI would otherwise fail on its first write. **A per-user install registers
nothing**, deliberately — on such a machine nothing is written and nothing is reported as
broken. `winlogrotate doctor` tells you which state you are in, under **Notifications**:

```
  event log     writable
  event log     not registered - by design for a PerUser installation
  event log     NOT REGISTERED - nothing reaches Event Viewer
```

The third is the only one that is a fault: a per-machine install whose source was never created
or has been removed. `doctor --json` carries the same answer as `notify.eventLog`, with
`notify.eventLogExpected` saying whether a source was ever going to exist here.

### The `--json` exception

Invocations that pass `--json` or `--json-stream` write **nothing** to the Event Log. The caller
is reading the output itself, and the caller is usually the GUI — which polls `doctor --json`
from several pages on every refresh. On a machine with a loosened configuration directory that
is a `Critical` diagnostic, so mirroring it would write an event every time somebody clicked a
tab. That is exactly how an administrator concludes a source is noise and filters it away,
taking the one event that mattered with it.

The scheduled task runs `run --config-dir ... --lock-held-exit 0` with no `--json`, so the run
that actually needs a record still gets one.

Pass `--no-event-log` to suppress it explicitly.

### Volume

Two allowances, and they are deliberately not one.

**Mirrored diagnostics: at most 50 per invocation.** After that a single further event (999,
Warning) says so and the rest are dropped. A job whose directory has become unreachable can
otherwise produce a diagnostic per file, and a few hundred near-identical entries is how an
administrator decides this source is noise and filters it away — taking the one event that
mattered with it. The full record is always in the journal.

The count is of **attempts**, not of events that landed, and the announcement says so. Counting
successes would leave the allowance unbounded on a log that refuses everything: the cap would
stop capping at exactly the moment it is needed.

**Notification digests (155): no count ceiling.** The argument above is about volume nobody
chose — a diagnostic per file. A digest is one per job plus one for the run, so the number is
bounded by the configuration somebody wrote. Giving digests the diagnostics' fifty would silently
drop the tail on an install with more jobs than that.

That is not a hypothesis. The two streams shared one allowance until v0.10.5, and a digest that
arrived over budget was reported to the notification machinery as **delivered**: the job's state
advanced, the incident was recorded as reported, and nothing was written anywhere. It happened on
the runs noisy enough to spend fifty diagnostics, which are the runs a digest exists for.

## Event IDs

**This table is a contract.** IDs are never renumbered and never reused: an alert rule names an
ID, and nothing tells its author when that ID silently stops being produced. A unit test asserts
that every `DiagnosticCode` maps to exactly one ID here, that no two share one, and that all of
them fall in the range below.

An ID that has **never** been emitted may be withdrawn, and its number stays spent. 100, 101 and
110 - "run completed", quiet, with changes and with failures - were published here and written
by nothing, and 110 was the worked example below for *did last night's run fail?*. A test now asserts that every declared ID is named by code that can emit it, which is what
makes withdrawal safe: an alert rule can only be broken by an ID that used to be produced.

> **Why 1–1000.** The installer registers
> `EventMessageFile = %SystemRoot%\System32\EventCreate.exe`, whose message table defines that
> range with a single insertion string per entry. An ID outside it renders in Event Viewer as
> *"The description for Event ID N … cannot be found"*. For the same reason every event carries
> exactly one insertion string holding the whole message, and the category is always 0.

**155 is the one to alert on.** It carries the message the `[notify]` machinery decided to send -
what an operator asked to be told - whereas 150 to 154 report on the notification machinery itself.
An alert rule naming 155 fires on "a job started failing", not on "a webhook could not be reached".
See [notifications](notifications.md) for how to configure it.

The **Type** column is the type these events normally carry. It is derived from the severity of
the diagnostic at the time, and a few codes legitimately appear at more than one - a refused path
is an Error from the configuration validator and from the runtime guard, but an override of the
same guard is a Warning. `LR3003` is the one written as *varies*, because its three types mean
three genuinely different things; the others are listed at the type they normally carry. The
**ID** never varies, which is what an alert rule should match on.

Where a code has exactly one emitter, a unit test now reads the severity out of the source and
fails if this column disagrees. It found two rows that had been wrong since they were written:
121 said Warning and is an Error, and 124 said Warning and is an Info - which matters, because at
Warning a first-run baseline would be counted among the failures of a run that had not failed.

`Severity.Info` is **not** mirrored to the Application log: the sink takes Warning and above, on
the grounds that a system log is not where progress belongs. So 122, 123 and 124 will not appear
in Event Viewer however the job is configured. They are in this table because the ID is also what
the journal and the `--json` envelope carry, and because an ID an alert rule can look up should
mean the same thing wherever it is read.

| ID | Type | Condition | Code |
|---:|---|---|---|
| 111 | Error | Administrator rights are required | `LR1001` |
| 112 | Error | The configuration could not be read | `LR1002` |
| 113 | Error | The configuration is invalid; nothing was attempted | `LR1003` |
| 114 | Warning | No jobs are configured | `LR1004` |
| 115 | Error | The verb needs a platform this is not | `LR1005` |
| 116 | Error | The invocation ended unexpectedly; what was done is unknown | `LR1006` |
| 117 | Error | The command line named something the verb could not use | `LR1007` |
| 120 | Warning | A job was skipped | `LR2001` |
| 121 | Error | A job matched no files and did not say `missingok` | `LR2002` |
| 122 | Info | A log was due but held back by `notifempty` | `LR2003` |
| 123 | Info | A log was due but held back by `minsize` or `minage` | `LR2004` |
| 124 | Info | First run: a baseline was recorded | `LR2005` |
| 130 | Error | A rotation failed | `LR3001` |
| 131 | Error | The file was locked by another process | `LR3002` |
| 132 | *varies* | The configured lock strategy was unavailable | `LR3003` |
| 133 | Warning | A previous run abandoned the rotation mutex | `LR3101` |
| 134 | Error | NUL-fill detected; `copytruncate` quarantined for that path | `LR3102` |
| 135 | Error | A hook ran and failed, timed out, or could not be started | `LR3103` |
| 136 | Warning | One rotation index is held by two files; both were kept | `LR3104` |
| 140 | Warning | No run host is registered | `LR4001` |
| 141 | Warning | The run host this install was set up with is no longer registered | `LR4002` |
| 142 | Error | Registering the run host failed | `LR4003` |
| 150 | Warning | A notification target is unparseable or missing its credential | `LR5001` |
| 151 | Warning | A notification channel could not be reached | `LR5002` |
| 152 | Warning | A notification channel is suppressed after repeated failures | `LR5003` |
| 153 | Warning | The notification state could not be read; change detection starts over | `LR5004` |
| 154 | Warning | The notification phase was cut short to protect the run's deadline | `LR5005` |
| 155 | Warning | A notification digest delivered to an `eventlog:` target | — |
| 190 | Error | The configuration directory is writable by a non-administrator | `LR9001` |
| 191 | Error | A dangerous path was refused | `LR9002` |
| 192 | Error | A hook was refused | `LR9003` |
| 193 | Error | A link was not followed, because its target is somewhere we will not act | `LR9004` |
| 194 | Error | A configuration names a secret that is not stored | `LR9005` |
| 195 | Warning | A credential is written in a world-readable configuration file | `LR9006` |
| 196 | Error | The secret store, or the key protecting it, is unsafe or was repaired | `LR9007` |
| 999 | Warning | Unclassified, or the per-invocation event cap was reached | — |

`LR3003`'s severity is contextual, and deliberately so. It is an **Error** when a job asking
explicitly for `copytruncate` is skipped because that path has a confirmed NUL-fill — the log is
not being rotated, and that is a failure. It is a **Warning** when `lockstrategy = "auto"` degrades
to `copy`, said every run because the live file keeps growing for as long as it lasts. It is
**Info** when a recorded truncation is abandoned unexamined, which costs nothing. `LR3102` is the
detection, raised once; `LR3003` is the ongoing condition — an alert rule needs to tell "a disk is
being destroyed tonight" from "this path has been excluded since Tuesday".

`Severity.Critical` is written as an Error event: the registered `TypesSupported` is 7, which is
Error, Warning and Information, and there is no fourth type. The distinction survives in the
event ID and in the message text.

## Reading them

```powershell
# Everything this product has written
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'WinLogRotate' }

# Just the security band
Get-WinEvent -FilterHashtable @{ LogName='Application'; ProviderName='WinLogRotate'; Id=190..193 }

# Did anything fail last night? Level 2 is Error, which is also how Critical is written.
Get-WinEvent -FilterHashtable @{
    LogName='Application'; ProviderName='WinLogRotate'; Level=2; StartTime=(Get-Date).AddDays(-1)
}
```

There is **no per-run summary event**, so silence is not proof that a run happened - a machine
that never woke up looks exactly like a machine with nothing to rotate. `winlogrotate journal`
holds the record of what ran and when, and `winlogrotate doctor` says whether anything is
registered to run at all. The Application log answers *what went wrong*.

If a message reads *"The description for Event ID … cannot be found"*, the event source is
registered but its `EventMessageFile` value is missing or wrong. **Reinstall** — the installer
writes that registry value and creating one needs administrator. `winlogrotate host repair` will
not help: it restores the run host and the configuration directory's permissions, and has never
touched the event source.

## Why not `LastTaskResult`?

Because it cannot be trusted to mean what it appears to mean. The registered task passes
`--lock-held-exit 0`, so a run that did nothing because another run held the gate reports
success — which is correct, but means `0x0` does not prove work happened. Worse,
`ExecutionTimeLimit` terminating a task reports `0x41306`, which is indistinguishable from an
operator pressing **Stop**.

The GUI and this log report from the journal instead. That is an invariant, because reading
`LastTaskResult` is the tempting shortcut.

`host status` answers a different question and reads neither: whether a run host is registered,
whether the registration has drifted from what this build would write, and where the configuration
lives. Ask the journal what happened; ask `host status` what is set up.
