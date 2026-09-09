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
broken. `winlogrotate doctor` tells you which state you are in.

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

At most **50 events per invocation**, after which a single further event says so and the rest
are dropped. A job whose directory has become unreachable can otherwise produce a diagnostic per
file. The full record is always in the journal.

## Event IDs

**This table is a contract.** IDs are append-only and are never renumbered or reused: an alert
rule names an ID, and nothing tells its author when that ID silently stops being produced. A
unit test asserts that every `DiagnosticCode` maps to exactly one ID here, that no two share
one, and that all of them fall in the range below.

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
same guard is a Warning. The **ID** never varies, which is what an alert rule should match on.

| ID | Type | Condition | Code |
|---:|---|---|---|
| 100 | Information | Run completed, nothing was due | — |
| 101 | Information | Run completed with changes | — |
| 110 | Error | Run completed with failures | — |
| 111 | Error | Administrator rights are required | `LR1001` |
| 112 | Error | The configuration could not be read | `LR1002` |
| 113 | Error | The configuration is invalid; nothing was attempted | `LR1003` |
| 114 | Warning | No jobs are configured | `LR1004` |
| 115 | Error | The verb needs a platform this is not | `LR1005` |
| 120 | Warning | A job was skipped | `LR2001` |
| 121 | Warning | A log file was missing | `LR2002` |
| 122 | Warning | A log file was empty | `LR2003` |
| 123 | Warning | Not due yet | `LR2004` |
| 124 | Warning | First run: a baseline was recorded | `LR2005` |
| 130 | Error | A rotation failed | `LR3001` |
| 131 | Error | The file was locked by another process | `LR3002` |
| 132 | Warning | The configured lock strategy was unavailable | `LR3003` |
| 133 | Warning | A previous run abandoned the rotation mutex | `LR3101` |
| 134 | Error | NUL-fill detected; `copytruncate` quarantined for that path | `LR3102` |
| 140 | Warning | No run host is registered | `LR4001` |
| 141 | Warning | The registered run host has drifted from its definition | `LR4002` |
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
| 193 | Error | A reparse point was refused | `LR9004` |
| 194 | Error | A configuration names a secret that is not stored | `LR9005` |
| 195 | Warning | A credential is written in a world-readable configuration file | `LR9006` |
| 196 | Error | The secret store, or the key protecting it, is unsafe or was repaired | `LR9007` |
| 999 | Warning | Unclassified, or the per-invocation event cap was reached | — |

`Severity.Critical` is written as an Error event: the registered `TypesSupported` is 7, which is
Error, Warning and Information, and there is no fourth type. The distinction survives in the
event ID and in the message text.

## Reading them

```powershell
# Everything this product has written
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'WinLogRotate' }

# Just the security band
Get-WinEvent -FilterHashtable @{ LogName='Application'; ProviderName='WinLogRotate'; Id=190..193 }

# Did last night's run fail?
Get-WinEvent -FilterHashtable @{
    LogName='Application'; ProviderName='WinLogRotate'; Id=110; StartTime=(Get-Date).AddDays(-1)
}
```

If a message reads *"The description for Event ID … cannot be found"*, the event source is
registered but its `EventMessageFile` value is missing or wrong. Reinstall, or run
`winlogrotate host repair`.

## Why not `LastTaskResult`?

Because it cannot be trusted to mean what it appears to mean. The registered task passes
`--lock-held-exit 0`, so a run that did nothing because another run held the gate reports
success — which is correct, but means `0x0` does not prove work happened. Worse,
`ExecutionTimeLimit` terminating a task reports `0x41306`, which is indistinguishable from an
operator pressing **Stop**.

`host status`, the GUI and this log all read the journal instead. That is an invariant, because
reading `LastTaskResult` is the tempting shortcut.
