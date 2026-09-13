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

## Editing jobs, and the one thing not to do

The `job` verbs create and change jobs without anyone opening Notepad:

```
job add    NAME --paths GLOB [--paths GLOB ...] [--kind rotate|manage] [--disabled]
                [--set KEY=VALUE ...] [--dry-run]
job set    NAME [--paths GLOB ...] [--kind ...] [--enabled|--disabled]
                [--set KEY=VALUE ...] [--unset KEY ...] [--dry-run]
job show   NAME
job enable NAME [--dry-run]    job disable NAME [--dry-run]    job remove NAME [--dry-run]
```

**Do not read `config show` and write it back.** `config show` publishes `EffectiveJob` — every
default and every `[defaults]` value already merged in. It is the right answer to *what will
happen tonight* and the wrong one to *what does this file say*. A tool that reads a job from it
and saves it severs that job from `[defaults]` on the first save, permanently and without
saying anything: the job stops inheriting, and next year's change to `[defaults]` silently skips
it.

`job show` is what to read instead. It reports each key the file actually writes, with the file's
own source text and line, and nothing resolved — `"100M"` stays `"100M"`. Feeding those keys back
through `job set` changes no byte of the file, and that round trip is pinned by a test.

Each key carries `known`, which says whether this product reads it. A key with `known: false` can
be removed with `--unset` but not written: writing one would put something in the file that does
nothing.

### Validating without writing

`--dry-run` builds the proposal, judges it with the loader's own pass, reports, and writes
nothing. **Validate and save differ by exactly that one flag** — same arguments, same code path,
same verdict — so a form cannot report a green tick through one route and then fail through
another. `--dry-run` also needs no administrator rights, so a UI can validate on every field
without a UAC prompt; everything that writes needs them, and says so with `LR1001` rather than
letting an access error escape as a defect.

### What the exit codes mean here

| | |
|---|---|
| `0` | Written. Also "nothing to change", with `changes: []` and a line saying so |
| `2` | Refused, and **nothing was attempted**: an unusable value, an unknown key, a missing job, a name already taken, or a proposal the validator rejects |
| `1` | The proposal was valid and the write itself failed — `LR1010`, or `LR1001` if it was not elevated |

Every bad value is reported at once rather than one per round trip, so a form can mark every
field in a single pass.

### Things the verbs will not do

- **Rename.** A job's name is its identity: the file is named after it and the journal is keyed
  by it, so half a rename is a job that quietly stops being the job whose history you have. Add
  the new one and remove the old one.
- **Take a whole job as JSON.** There is no `--from-json`. It is the hazard above wearing a
  different hat, and it is the shape a tool author reaches for first.
- **Notice that someone else edited the file.** Two windows, or a window and Notepad, are still
  last-writer-wins.

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

Creating a job from a script, and checking it before it is written:

```powershell
$dry = winlogrotate job add iis --paths "C:/inetpub/logs/**/*.log" `
                               --set rotate=14 --set compress=true --dry-run --json |
       ConvertFrom-Json

if ($dry.exitCode -ne 0) {
    $dry.diagnostics | ForEach-Object { Write-Warning "$($_.code): $($_.message)" }
    throw "Not writing a job the loader would refuse."
}

winlogrotate job add iis --paths "C:/inetpub/logs/**/*.log" `
                        --set rotate=14 --set compress=true | Out-Null
```

Reading a job and changing one key, leaving the rest of its file alone:

```bash
winlogrotate job show iis --json | jq -r '.result.keys[] | select(.known | not) | .key'
winlogrotate job set iis --set rotate=30 --json | jq '.result.changes'
```
