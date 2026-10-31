# End-to-end coverage

What the journeys in `E2E/` drive, item by item. Run them with `scripts/e2e.sh` (Windows, elevated);
CI runs them in ci.yml's `e2e` job.

**Extracted 2026-10-31** from the source, with:

```
grep -rnE 'class \w+\s*:\s*(Form|UserControl)\b' src/WinLogRotate.Gui --include=*.cs     # windows and pages (8)
grep -rnE 'LrDialog\.(Show|Error|Info|Confirm)\(|SecretPrompt\.Ask' src/WinLogRotate.Gui  # dialogs
grep -rnE 'Tool\(|new Button|_\w+\.Click \+=' src/WinLogRotate.Gui/Pages src/WinLogRotate.Gui/Ui  # actions
grep -oE 'new Command\((word|"[a-z-]+")' src/WinLogRotate.Cli/Commands/CommandTree.cs     # verbs (word = enable/disable)
```

**106 items: 82 ✅, 24 ❌** (c 15, d 6, b 2, a 1). There are no roles: the console is one
administrator's window, and the one role-like state - unelevated - is row 6. Spec names are
`Class › Method` in `tests/WinLogRotate.Gui.Tests/E2E/`.

> **Not yet run.** The journeys were written on 2026-10-31 and compile, but this repo has no
> Windows machine outside CI. The first run is the `e2e` job on the next push or pull request;
> until then every ✅ means "written", not "passed" (see the Wave 2 report).

## Main window

| # | Item | Spec / reason |
|---|------|---------------|
| 1 | Window opens on a config directory, nav lists six pages | ✅ ConsoleJourneys › EveryPageLoads (×6) |
| 2 | Sweep: every page loads with a job and a channel, no dialog, no exception | ✅ ConsoleJourneys › EveryPageLoads |
| 3 | Sweep: every page loads on an empty configuration | ✅ ConsoleJourneys › EveryPageLoadsOnAnEmptyConfiguration |
| 4 | Banner: winlogrotate.exe missing / not ours / unreadable / wrong wire version | ❌ (c) Core.Tests CliIdentityTests (every verdict of `CliIdentity.Inspect`) |
| 5 | Banner: newer release (daily check due) | ❌ (c) Core.Tests UpdatePanelTests (`GuiPreferences.IsCheckDue`, the banner text); the check itself needs the GitHub feed |
| 6 | Banner: running without administrator rights | ❌ (d) the CI runner's token is elevated and a test cannot drop its own elevation |
| 7 | Dark theme at load (HKCU AppsUseLightTheme = 0) | ❌ (d) the theme is read once, at load, from HKCU; flipping it changes the machine's user, and switching after load is not what the app does |

## Jobs page

| # | Item | Spec / reason |
|---|------|---------------|
| 8 | List of jobs (name, state, kind, paths, policy) | ✅ JobJourneys › EditingAJobChangesOnlyTheLineThatChanged |
| 9 | Empty state: sentence + "Add your first job" | ✅ JobJourneys › TheFirstJobIsAddedFromTheEmptyStateAndWritesItsFile |
| 10 | First job's follow-up hint after Save | ✅ JobJourneys › TheFirstJobIsAddedFromTheEmptyStateAndWritesItsFile |
| 11 | New (toolbar) | ✅ via the same editor as row 9; the toolbar button runs the same `EditAsync(null)` |
| 12 | Edit (toolbar, selected row) | ✅ JobJourneys › TheEditorChecksAndPreviewsWithoutWritingAndCancelKeepsTheFile |
| 13 | Double-click a row opens that job | ✅ JobJourneys › EditingAJobChangesOnlyTheLineThatChanged |
| 14 | Enable/Disable, elevated | ✅ JobJourneys › AJobIsSwitchedOffAndOnAgain |
| 15 | "Select a job first." with nothing selected | ✅ JobJourneys › AJobIsSwitchedOffAndOnAgain |
| 16 | Remove: confirm dialog, No keeps the file | ✅ JobJourneys › RemovingAJobAsksFirstThenDeletesItsFile |
| 17 | Remove: Yes deletes the file, back to the empty state | ✅ JobJourneys › RemovingAJobAsksFirstThenDeletesItsFile |
| 18 | Refresh picks up a file written behind the window's back | ✅ JobJourneys › CheckConfigurationSaysCleanOrNamesTheProblems |
| 19 | Check configuration: "No problems found." | ✅ JobJourneys › CheckConfigurationSaysCleanOrNamesTheProblems |
| 20 | Check configuration: broken job, error tone, details expander | ✅ JobJourneys › CheckConfigurationSaysCleanOrNamesTheProblems |
| 21 | Check configuration: warning tone ("n warning(s)") | ❌ (c) Core.Tests ConfigCheckProjectionTests |
| 22 | Open folder (Explorer) | ❌ (d) opens Explorer through the shell; nothing in the window changes |
| 23 | "Elevation was cancelled. Nothing was written." | ❌ (d) needs a UAC prompt answered No; the runner has no consent UI |
| 24 | Unexpected response / version-mismatch dialog | ❌ (c) Core.Tests JobsProjectionTests, EnvelopeShapeTests |

## Job editor

| # | Item | Spec / reason |
|---|------|---------------|
| 25 | Title "Job: NAME", Files section | ✅ JobJourneys › EditingAJobChangesOnlyTheLineThatChanged |
| 26 | Refusal: no name | ✅ JobJourneys › TheFirstJobIsAddedFromTheEmptyStateAndWritesItsFile |
| 27 | Refusal: no files | ✅ JobJourneys › TheFirstJobIsAddedFromTheEmptyStateAndWritesItsFile |
| 28 | Files preview ("Matches 1 file, …") through `glob` | ✅ JobJourneys › TheFirstJobIsAddedFromTheEmptyStateAndWritesItsFile |
| 29 | Save a new job (dry run, then elevated `job add`) | ✅ JobJourneys › TheFirstJobIsAddedFromTheEmptyStateAndWritesItsFile |
| 30 | Save an edit: exactly one line changes, comments survive | ✅ JobJourneys › EditingAJobChangesOnlyTheLineThatChanged |
| 31 | Menu navigation to Keep | ✅ JobJourneys › EditingAJobChangesOnlyTheLineThatChanged |
| 32 | Check (dry run of the edit, nothing written) | ✅ JobJourneys › TheEditorChecksAndPreviewsWithoutWritingAndCancelKeepsTheFile |
| 33 | Check refused by the verb: "Check" error dialog with details | ✅ JobJourneys › TheEditorChecksAndPreviewsWithoutWritingAndCancelKeepsTheFile |
| 34 | What would happen… preview dialog | ✅ JobJourneys › TheEditorChecksAndPreviewsWithoutWritingAndCancelKeepsTheFile |
| 35 | Cancel writes nothing | ✅ JobJourneys › TheEditorChecksAndPreviewsWithoutWritingAndCancelKeepsTheFile |
| 36 | Sections How / When / Old copies / Other, Inherit and Remove links | ❌ (c) Core.Tests JobEditorModelTests, EditorLayoutTests, JobEditProjectionTests (every field's args and judgement) |
| 37 | Browse for file / folder | ❌ (d) OS file dialogs |
| 38 | Hook warning on a per-user install | ❌ (c) Core.Tests JobEditorModelTests (`HookWarning`) |
| 39 | "Nothing to change." | ❌ (c) Core.Tests JobEditorModelTests (`Changes`) |

## Run page

| # | Item | Spec / reason |
|---|------|---------------|
| 40 | Dry run: output, nothing changes on disk | ✅ ConsoleJourneys › ADryRunChangesNothingAndRotateNowAsksThenRotatesIntoTheHistory |
| 41 | Rotate now: confirm, No changes nothing | ✅ same |
| 42 | Rotate now: Yes, elevated, the log is rotated | ✅ same |
| 43 | Rotate defect dialog | ❌ (c) Core.Tests EnvelopeDetailsTests (the CliResult the dialog shows) |

## History page

| # | Item | Spec / reason |
|---|------|---------------|
| 44 | Empty journal sentence | ✅ ConsoleJourneys › EveryPageLoadsOnAnEmptyConfiguration |
| 45 | Journal rows after a rotation | ✅ ConsoleJourneys › ADryRunChangesNothingAndRotateNowAsksThenRotatesIntoTheHistory |
| 46 | Refresh | ✅ same |
| 47 | Open folder | ❌ (d) Explorer |
| 48 | Unreadable lines skipped | ❌ (c) Core.Tests JournalHistoryTests |

## Scheduling page

| # | Item | Spec / reason |
|---|------|---------------|
| 49 | "Currently:" from doctor | ✅ ConsoleJourneys › EveryPageLoads |
| 50 | Apply: Scheduled task at a chosen time (elevated) | ✅ ConsoleJourneys › SchedulingRegistersTheTaskAndSwitchesBackToNothing (CI only: WINLOGROTATE_E2E_MACHINE=1) |
| 51 | Apply: Neither | ✅ same |
| 52 | Apply: Windows service | ❌ (c) not offered yet (`SchedulingProjection.Offered`); Core.Tests SchedulingProjectionTests |
| 53 | "Could not change the schedule" dialog | ❌ (c) Core.Tests HostUseTests |

## Notifications page

| # | Item | Spec / reason |
|---|------|---------------|
| 54 | Providers and targets grid, summary in words | ✅ ConsoleJourneys › EveryPageLoads; wording in Core.Tests NotifyShowViewTests |
| 55 | "Nothing is configured…" | ✅ ConsoleJourneys › EveryPageLoadsOnAnEmptyConfiguration |
| 56 | Send test: delivered (local webhook sink) | ✅ ConsoleJourneys › SendTestDeliversToTheWebhookAndNamesAChannelThatFailed |
| 57 | Send test: channel failed | ✅ same |
| 58 | Clear suppressed: confirm, then cleared | ✅ same |
| 59 | Set credential without a provider row | ✅ ConsoleJourneys › SetCredentialWithoutAProviderSaysToPickOne |
| 60 | Credential prompt: mismatched entries refused | ✅ ConsoleJourneys › ACredentialIsStoredThroughThePromptAndAMismatchIsRefused |
| 61 | Credential prompt: stored through the elevated pipe, config points at it | ✅ same |
| 62 | Credential prompt: empty value refused | ❌ (c) same code path as row 60, one branch earlier in `SecretPrompt.Ask`; the store side is Windows.Tests SecretPipeTests |
| 63 | Send test to real SMTP / Pushover / Teams | ❌ (b) a real message to a real recipient; the sink in row 56 stands in |

## Settings page

| # | Item | Spec / reason |
|---|------|---------------|
| 64 | Doctor report | ✅ ConsoleJourneys › EveryPageLoads |
| 65 | Refresh | ✅ ConsoleJourneys › RepairPermissionsSecuresTheConfigurationDirectory |
| 66 | Repair permissions (elevated), "secured" dialog | ✅ same |
| 67 | Updates: Only when asked / Once a day | ❌ (c) Core.Tests UpdatePanelTests (preferences parse/save); the radio writes the user's own %LOCALAPPDATA% file |
| 68 | Updates: Check now | ❌ (a) the GitHub releases feed; Core.Tests UpdateCommandTests, UpdatePanelTests |
| 69 | Updates: Update now | ❌ (b) downloads and runs a real installer |

## CLI verbs

| # | Item | Spec / reason |
|---|------|---------------|
| 70 | `--version`, `--help` | ✅ CliJourneys › TheVersionHelpAndAParseErrorAnswerAsDocumented |
| 71 | Parse error: exit 2, LR1007 | ✅ same |
| 72 | `job add` (and `--dry-run` writes nothing) | ✅ CliJourneys › AJobIsAddedChangedSwitchedOffShownAndRemovedByTheVerbs |
| 73 | `job add` of an existing name is refused | ✅ same |
| 74 | `job set` changes one line | ✅ same |
| 75 | `job disable` / `job enable` | ✅ same |
| 76 | `job show` | ✅ same |
| 77 | `job remove` | ✅ same |
| 78 | `config show` | ✅ same |
| 79 | `config check`: clean | ✅ same |
| 80 | `config check`: broken job vs unreadable configuration (two exit codes) | ✅ CliJourneys › ConfigCheckExitCodesTellABrokenJobFromABrokenConfiguration |
| 81 | `run --dry-run` | ✅ CliJourneys › ADryRunChangesNothingARunRotatesAndTheJournalRecordsIt |
| 82 | `run --force` | ✅ same |
| 83 | `run` when nothing is due | ✅ same |
| 84 | `journal --job` | ✅ same |
| 85 | `host pause --for` / `host pause` (resume), `run` while paused | ✅ same |
| 86 | `glob` | ✅ CliJourneys › GlobAndProbeAnswerForRealFiles |
| 87 | `probe` | ✅ same; the contended-handle cases are installer-smoke "Leg A: the probe meets four real contended handles" |
| 88 | `notify show` | ✅ CliJourneys › NotifyVerbsShowTestAndReset |
| 89 | `notify test` (local webhook sink) | ✅ same |
| 90 | `notify status` | ✅ same |
| 91 | `notify reset` | ✅ same |
| 92 | `notify set-secret` (pipe) | ✅ ConsoleJourneys › ACredentialIsStoredThroughThePromptAndAMismatchIsRefused (the console's own call) |
| 93 | `secret set --from-file` | ✅ CliJourneys › SecretsAreSetListedTestedImportedAndRemoved |
| 94 | `secret import` | ✅ same |
| 95 | `secret list` (never values) | ✅ same |
| 96 | `secret test` | ✅ same |
| 97 | `secret remove` | ✅ same |
| 98 | `import --out` | ✅ CliJourneys › ImportConvertsALogrotateConfigurationIntoJobFiles |
| 99 | `doctor` | ✅ CliJourneys › DoctorHostStatusAndTheTaskXmlDescribeTheMachine |
| 100 | `host status` | ✅ same |
| 101 | `host export-task` | ✅ same |
| 102 | `scan` | ✅ same |
| 103 | `host use task/none` | ✅ ConsoleJourneys › SchedulingRegistersTheTaskAndSwitchesBackToNothing; installed, as SYSTEM: installer-smoke "Leg A: the scheduled task is registered and hardened" |
| 104 | `host repair --acl` | ✅ ConsoleJourneys › RepairPermissionsSecuresTheConfigurationDirectory |
| 105 | `host path-add` / `host path-remove` | ❌ (c) installer-smoke "Leg A: Event Log source and PATH" (machine PATH, on the installed product) |
| 106 | `update check` / `update apply` | ❌ (c) Core.Tests UpdateCommandTests; a live check needs the GitHub feed, a live apply installs a release |
