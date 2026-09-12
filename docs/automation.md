# Automation: the `--json` contract

Every verb speaks `--json`. This page is what a script may rely on, and what it may not.

## The envelope

One JSON object on stdout, and nothing on stderr.

```json
{
  "schema": 1,
  "product": "WinLogRotate",
  "version": "0.13.0",
  "verb": "config check",
  "ok": true,
  "exitCode": 0,
  "result": { "root": "C:\\ProgramData\\WinLogRotate", "jobs": 2, "errors": 0, "warnings": 1 },
  "diagnostics": [
    { "severity": "Warning", "code": "LR1003",
      "message": "olddir is ignored on a manage job.",
      "remedy": "Remove it, or make this a rotate job." }
  ]
}
```

| Field | |
| --- | --- |
| `schema` | The contract version. Compare it for equality; see **Versioning** below. |
| `product` | Always `WinLogRotate`. Check it if you resolved the executable from `PATH`. |
| `version` | The product version. Informational — never branch on it. |
| `verb` | The verb that ran, space-separated: `host repair`, `notify test`. |
| `ok` | `exitCode == 0`. |
| `exitCode` | The process exit code, repeated so a captured envelope is self-contained. |
| `result` | The verb's payload, or absent when the verb has none. |
| `diagnostics` | Everything worth telling you, in order. Never empty on a failure. |

## Exit codes

| | Meaning |
| --- | --- |
| `0` | Completed. |
| `1` | The work happened and something in it went wrong. |
| `2` | Nothing was attempted: a bad configuration, or a bad command line. |
| `3` | Another rotation holds the machine-wide gate. Only `run` returns this. |
| `4` | A defect. Nothing about what was or was not done can be relied on. |

**`1` and `2` are different on purpose.** A scheduled task logging `1` always means "look at
the logs"; it never means "you mistyped a flag". A mistyped flag is `2`, alongside a bad
configuration file, because in both cases nothing ran.

**Exit 0 is not the same as "it did the thing."** Several verbs complete successfully having
deliberately done nothing — a `host repair --acl` on a per-user install declines and says why; a
`notify test` with nothing configured tests nothing. In every such case the reason is a
diagnostic in the same envelope. Read `diagnostics`, not only `exitCode`.

## Diagnostics

```json
{ "severity": "Error", "code": "LR9002", "message": "...", "remedy": "...",
  "path": "C:\\logs\\app.log", "job": "iis", "line": 4, "column": 12, "nativeError": 32 }
```

`severity` is `Info`, `Warning`, `Error` or `Critical`. `code` is the stable identifier — branch
on that, never on `message`, which is written for a human and will be reworded. Every code is
listed in [diagnostics](diagnostics.md) with its Event Log id.

Optional fields are omitted when they do not apply rather than sent as `null`.

## Errors a script must handle

**A mistyped flag answers in JSON too.** It is exit 2 with the parse error as a diagnostic, on
stdout — so `ConvertFrom-Json` does not fail on the response to a typo.

```
$ winlogrotate run --dry-runn --json
{"verb":"run","ok":false,"exitCode":2,"diagnostics":[
  {"severity":"Error","code":"LR1007",
   "message":"Unrecognized command or argument '--dry-runn'.",
   "remedy":"Run 'winlogrotate run --help' for usage."}]}
```

**Nothing else is written to stdout.** Progress lines, tables and confirmations are suppressed
under `--json`, so the whole of stdout is one object and `ConvertFrom-Json` can take all of it.

**`--help` is the one exception**, and deliberately: it prints usage text for a person and exits
0, whatever else is on the command line. Nothing else in the product answers a `--json`
invocation with anything but an envelope.

## Streaming

`--json-stream` implies `--json` and additionally writes newline-delimited events to stdout as
work happens — one object per line, then the envelope last. Use it to follow a long run; use
plain `--json` for everything else.

`--output <file>` sends that stream to a file instead. It exists because an elevated `runas`
child cannot have its pipes redirected, which is how the GUI follows an elevated run.

## Versioning

`schema` changes only when an existing field changes meaning or disappears. **New fields are
added without changing it**, so parse permissively: read the fields you need and ignore the rest.

A script that wants to be strict should compare `schema` for equality and stop if it differs.
The shape of every verb's envelope is pinned by a snapshot test in this repository, so a change
to any of them is a deliberate act with a diff attached.

## Examples

PowerShell, checking a configuration:

```powershell
$check = winlogrotate config check --json | ConvertFrom-Json

if ($check.exitCode -eq 2) { throw "Configuration is broken; nothing will run." }

foreach ($d in $check.diagnostics | Where-Object severity -in 'Warning','Error') {
    Write-Warning "$($d.code): $($d.message)"
}
```

Confirming a rotation host is registered:

```powershell
$doctor = winlogrotate doctor --json | ConvertFrom-Json
if ($doctor.result.runHost -eq 'None') { throw 'Nothing is registered to run rotations.' }
```

Bash, on a machine where the CLI is on `PATH`:

```bash
if ! winlogrotate run --json > run.json; then
    jq -r '.diagnostics[] | "\(.severity) \(.code): \(.message)"' run.json
    exit 1
fi
```
