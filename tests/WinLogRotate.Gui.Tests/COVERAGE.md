# End-to-end coverage

What the journeys in `E2E/` drive, item by item. Run them with `scripts/e2e.sh` (Windows, elevated);
CI runs them in ci.yml's `e2e` job.

**Extracted 2026-10-31** from the source, with:

```
grep -rnE 'class \w+\s*:\s*(Form|UserControl)\b' src/WinLogRotate.Gui --include=*.cs     # windows and pages (8)
grep -rnE 'LrDialog\.(Show|Error|Info|Confirm)\(|SecretPrompt\.Ask' src/WinLogRotate.Gui --include=*.cs  # dialogs
grep -rnE 'Tool\(|new Button|_\w+\.Click \+=' src/WinLogRotate.Gui/Pages src/WinLogRotate.Gui/Ui  # actions
grep -oE 'new Command\((word|"[a-z-]+")' src/WinLogRotate.Cli/Commands/CommandTree.cs     # verbs (word = enable/disable)
```

**117 items: 88 ✅, 29 ❌** (c 22, d 5, b 2). There are no roles: the console is one
administrator's window, and the one role-like state - unelevated - is row 6. Spec names are
`Class › Method` in `tests/WinLogRotate.Gui.Tests/E2E/`.

Every ✅ passed on windows-2025 on 2026-10-31: 29 tests in about 2m45s, three runs in a row with
no failure. Two journeys run only in CI (`WINLOGROTATE_E2E_MACHINE=1`): the dark theme and Scheduling.

## Main window

| # | Item | Spec / reason |
|---|------|---------------|
| 1 | Window opens on a config directory, nav lists six pages | ✅ ConsoleJourneys › EveryPageLoads (×6) |
| 2 | Sweep: every page loads with a job and a channel, no dialog, no exception | ✅ ConsoleJourneys › EveryPageLoads |
| 3 | Sweep: every page loads on an empty configuration | ✅ ConsoleJourneys › EveryPageLoadsOnAnEmptyConfiguration |
| 4 | Banner: winlogrotate.exe missing / not ours / unreadable / wrong wire version | ❌ (c) Core.Tests CliIdentityTests (every verdict of `CliIdentity.Inspect`) |
| 5 | Banner: newer release (daily check due) | ❌ (c) Core.Tests GuiPreferencesTests (`IsCheckDue`), UpdateStatusProjectionTests (the banner text); the check itself reads the live GitHub feed, which has no local stand-in |
| 6 | Banner: running without administrator rights | ❌ (d) the CI runner's token is elevated and a test cannot drop its own elevation |
| 7 | Dark theme at load (HKCU AppsUseLightTheme = 0) | ✅ ConsoleJourneys › TheWindowOpensDarkWhenWindowsAppsAreDark (CI only: WINLOGROTATE_E2E_MACHINE=1; the value is put back afterwards) |
| 8 | "Unexpected error" dialog: an exception reaches the message loop | ❌ (c) Core.Tests GuiArchitectureTests (Program installs the handler); a working build has no such exception to raise, and the journeys fail on one (ConsoleDriver) | |
| 9 | A page's load meets a defect (exit 4): the "Jobs", "History", "Scheduling", "Notifications" or "Settings" error dialog | ❌ (c) Core.Tests EnvelopeDetailsTests (`OnlyADefectInterrupts`, `AnUnelevatedJsonVerbThatCrashesExplainsItself`); a working winlogrotate.exe does not exit 4 on a read | |

## Jobs page

| # | Item | Spec / reason |
|---|------|---------------|
| 10 | List of jobs (name, state, kind, paths, policy) | ✅ JobJourneys › EditingAJobChangesOnlyTheLineThatChanged |
| 11 | Empty state: sentence + "Add your first job" | ✅ JobJourneys › TheFirstJobIsAddedFromTheEmptyStateAndWritesItsFile |
| 12 | First job's follow-up hint after Save | ✅ JobJourneys › TheFirstJobIsAddedFromTheEmptyStateAndWritesItsFile |
| 13 | New (toolbar) | ✅ via the same editor as row 11; the toolbar button runs the same `EditAsync(null)` |
| 14 | Edit (toolbar, selected row) | ✅ JobJourneys › TheEditorChecksAndPreviewsWithoutWritingAndCancelKeepsTheFile |
| 15 | Double-click a row opens that job | ✅ JobJourneys › EditingAJobChangesOnlyTheLineThatChanged |
| 16 | Enable/Disable, elevated | ✅ JobJourneys › AJobIsSwitchedOffAndOnAgain |
| 17 | "Select a job first." with nothing selected | ✅ JobJourneys › AJobIsSwitchedOffAndOnAgain |
| 18 | Remove: confirm dialog, No keeps the file | ✅ JobJourneys › RemovingAJobAsksFirstThenDeletesItsFile |
| 19 | Remove: Yes deletes the file, back to the empty state | ✅ JobJourneys › RemovingAJobAsksFirstThenDeletesItsFile |
| 20 | Refresh picks up a file written behind the window's back | ✅ JobJourneys › CheckConfigurationSaysCleanOrNamesTheProblems |
| 21 | Check configuration: "No problems found." | ✅ JobJourneys › CheckConfigurationSaysCleanOrNamesTheProblems |
| 22 | Check configuration: broken job, error tone, details expander | ✅ JobJourneys › CheckConfigurationSaysCleanOrNamesTheProblems |
| 23 | Error dialog: Copy puts the title, sentence and details on the clipboard | ✅ JobJourneys › CheckConfigurationSaysCleanOrNamesTheProblems | |
| 24 | Check configuration: warning tone ("n warning(s)") | ❌ (c) Core.Tests ConfigCheckProjectionTests |
| 25 | Open folder (Explorer) | ❌ (d) opens Explorer through the shell; nothing in the window changes |
| 26 | "Elevation was cancelled. Nothing was written." | ❌ (d) needs a UAC prompt answered No; the runner has no consent UI |
| 27 | Unexpected response / version-mismatch dialog | ❌ (c) Core.Tests JobsProjectionTests, EnvelopeShapeTests |
| 28 | Enable/Disable/Remove refused by the verb, or a defect: the error dialog | ❌ (c) Core.Tests JobEditProjectionTests (`AJobThatIsNotThereIsRefusedWithTheOnesThatAre`, `AnUnelevatedWriteIsRefusedAsSuch`), EnvelopeDetailsTests | |

## Job editor

| # | Item | Spec / reason |
|---|------|---------------|
| 29 | Title "Job: NAME", Files section | ✅ JobJourneys › EditingAJobChangesOnlyTheLineThatChanged |
| 30 | Refusal: no name | ✅ JobJourneys › TheFirstJobIsAddedFromTheEmptyStateAndWritesItsFile |
| 31 | Refusal: no files | ✅ JobJourneys › TheFirstJobIsAddedFromTheEmptyStateAndWritesItsFile |
| 32 | Files preview ("Matches 1 file, …") through `glob` | ✅ JobJourneys › TheFirstJobIsAddedFromTheEmptyStateAndWritesItsFile |
| 33 | Save a new job (dry run, then elevated `job add`) | ✅ JobJourneys › TheFirstJobIsAddedFromTheEmptyStateAndWritesItsFile |
| 34 | Save an edit: exactly one line changes, comments survive | ✅ JobJourneys › EditingAJobChangesOnlyTheLineThatChanged |
| 35 | Menu navigation to Keep | ✅ JobJourneys › EditingAJobChangesOnlyTheLineThatChanged |
| 36 | Check (dry run of the edit, nothing written) | ✅ JobJourneys › TheEditorChecksAndPreviewsWithoutWritingAndCancelKeepsTheFile |
| 37 | Check refused by the verb: "Check" error dialog with details | ✅ JobJourneys › TheEditorChecksAndPreviewsWithoutWritingAndCancelKeepsTheFile |
| 38 | What would happen… preview dialog | ✅ JobJourneys › TheEditorChecksAndPreviewsWithoutWritingAndCancelKeepsTheFile |
| 39 | Cancel writes nothing | ✅ JobJourneys › TheEditorChecksAndPreviewsWithoutWritingAndCancelKeepsTheFile |
| 40 | Sections How / When / Old copies / Other, Inherit and Remove links | ❌ (c) Core.Tests JobEditorModelTests, EditorLayoutTests, JobEditProjectionTests (every field's args and judgement) |
| 41 | Browse for file / folder | ❌ (d) OS file dialogs |
| 42 | Hook warning on a per-user install | ❌ (c) Core.Tests JobEditorModelTests (`HookWarning`) |
| 43 | "Nothing to change." | ❌ (c) Core.Tests JobEditorModelTests (`Changes`) |
| 44 | Opening a job: a read defect, a refusal, or an unreadable answer ("Job" / "Unexpected response" dialogs) | ❌ (c) Core.Tests JobEditorModelTests (`AShowThatRefusedIsReportedAsTheRefusal`, the `Unreadable` cases), EnvelopeDetailsTests | |
| 45 | What would happen… meets a defect: error dialog | ❌ (c) Core.Tests EnvelopeDetailsTests, DryRunPreviewTests | |
| 46 | Check or Save meets a defect, or Save is refused by its dry run or the write: "Check" / "Save" error dialogs | ❌ (c) Core.Tests JobEditProjectionTests (`AJobTheValidatorRefusesIsRefusedAndSaysWhy`, `AnUnelevatedWriteIsRefusedAsSuch`), EnvelopeDetailsTests; the refusal path itself is row "Check refused by the verb" | |

## Run page

| # | Item | Spec / reason |
|---|------|---------------|
| 47 | Dry run: output, nothing changes on disk | ✅ ConsoleJourneys › ADryRunChangesNothingAndRotateNowAsksThenRotatesIntoTheHistory |
| 48 | Rotate now: confirm, No changes nothing | ✅ same |
| 49 | Rotate now: Yes, elevated, the log is rotated | ✅ same |
| 50 | Rotate defect dialog | ❌ (c) Core.Tests EnvelopeDetailsTests (the CliResult the dialog shows) |

## History page

| # | Item | Spec / reason |
|---|------|---------------|
| 51 | Empty journal sentence | ✅ ConsoleJourneys › EveryPageLoadsOnAnEmptyConfiguration |
| 52 | Journal rows after a rotation | ✅ ConsoleJourneys › ADryRunChangesNothingAndRotateNowAsksThenRotatesIntoTheHistory |
| 53 | Refresh | ✅ same |
| 54 | Open folder | ❌ (d) Explorer |
| 55 | Unreadable lines skipped | ❌ (c) Core.Tests JournalHistoryTests |

## Scheduling page

| # | Item | Spec / reason |
|---|------|---------------|
| 56 | "Currently:" from doctor | ✅ ConsoleJourneys › EveryPageLoads |
| 57 | Apply: Scheduled task at a chosen time (elevated) | ✅ ConsoleJourneys › SchedulingRegistersTheTaskAndSwitchesBackToNothing (CI only: WINLOGROTATE_E2E_MACHINE=1) |
| 58 | Apply: Neither | ✅ same |
| 59 | Apply: Windows service | ❌ (c) not offered yet (`SchedulingProjection.Offered`); Core.Tests SchedulingProjectionTests |
| 60 | "Could not change the schedule" dialog | ❌ (c) Core.Tests HostUseTests |

## Notifications page

| # | Item | Spec / reason |
|---|------|---------------|
| 61 | Providers and targets grid, summary in words | ✅ ConsoleJourneys › EveryPageLoads; wording in Core.Tests NotifyShowViewTests |
| 62 | Refresh | ✅ ConsoleJourneys › SendTestDeliversToTheWebhookAndNamesAChannelThatFailed | |
| 63 | "Nothing is configured…" | ✅ ConsoleJourneys › EveryPageLoadsOnAnEmptyConfiguration |
| 64 | Send test: delivered (local webhook sink) | ✅ ConsoleJourneys › SendTestDeliversToTheWebhookAndNamesAChannelThatFailed |
| 65 | Send test: channel failed | ✅ same |
| 66 | Clear suppressed: confirm, then cleared | ✅ same |
| 67 | Set credential without a provider row | ✅ ConsoleJourneys › SetCredentialWithoutAProviderSaysToPickOne |
| 68 | Credential prompt: mismatched entries refused | ✅ ConsoleJourneys › ACredentialIsStoredThroughThePromptAndAMismatchIsRefused |
| 69 | Credential prompt: stored through the elevated pipe, config points at it | ✅ same |
| 70 | Credential prompt: empty value refused | ✅ ConsoleJourneys › ACredentialIsStoredThroughThePromptAndAMismatchIsRefused |
| 71 | Credential prompt: Cancel stores nothing and says nothing | ✅ ConsoleJourneys › ACredentialIsStoredThroughThePromptAndAMismatchIsRefused | |
| 72 | The private pipe cannot be created, or the helper cannot store: "Set credential" error dialogs | ❌ (c) Windows.Tests SecretPipeTests (`TheNameIsClaimedExclusively`, `OnlyTheProcessWeExpectMayReadTheSecret`, `NothingConnectingTimesOutRatherThanHanging`) | |
| 73 | Send test to real SMTP / Pushover / Teams | ❌ (b) a real message to a real recipient; the sink in row 64 stands in |

## Settings page

| # | Item | Spec / reason |
|---|------|---------------|
| 74 | Doctor report | ✅ ConsoleJourneys › EveryPageLoads |
| 75 | Refresh | ✅ ConsoleJourneys › RepairPermissionsSecuresTheConfigurationDirectory |
| 76 | Repair permissions (elevated), "secured" dialog | ✅ same |
| 77 | "Permissions" warning on a loose directory, gone after Repair | ✅ ConsoleJourneys › RepairPermissionsSecuresTheConfigurationDirectory; the per-user wording in Core.Tests SettingsProjectionTests | |
| 78 | Updates: Only when asked / Once a day | ❌ (c) Core.Tests GuiPreferencesTests (preferences parse/save); the radio writes the user's own %LOCALAPPDATA% file |
| 79 | Updates: Check now | ❌ (c) Core.Tests UpdateCommandTests, UpdateStatusProjectionTests; the feed is the live GitHub releases URL, hard-coded, with no local simulator |
| 80 | Updates: Update now | ❌ (b) downloads and runs a real installer |

## CLI verbs

| # | Item | Spec / reason |
|---|------|---------------|
| 81 | `--version`, `--help` | ✅ CliJourneys › TheVersionHelpAndAParseErrorAnswerAsDocumented |
| 82 | Parse error: exit 2, LR1007 | ✅ same |
| 83 | `job add` (and `--dry-run` writes nothing) | ✅ CliJourneys › AJobIsAddedChangedSwitchedOffShownAndRemovedByTheVerbs |
| 84 | `job add` of an existing name is refused | ✅ same |
| 85 | `job set` changes one line | ✅ same |
| 86 | `job disable` / `job enable` | ✅ same |
| 87 | `job show` | ✅ same |
| 88 | `job remove` | ✅ same |
| 89 | `config show` | ✅ same |
| 90 | `config check`: clean | ✅ same |
| 91 | `config check`: broken job vs unreadable configuration (two exit codes) | ✅ CliJourneys › ConfigCheckExitCodesTellABrokenJobFromABrokenConfiguration |
| 92 | `run --dry-run` | ✅ CliJourneys › ADryRunChangesNothingARunRotatesAndTheJournalRecordsIt |
| 93 | `run --force` | ✅ same |
| 94 | `run` when nothing is due | ✅ same |
| 95 | `journal --job` | ✅ same |
| 96 | `host pause --for` / `host pause` (resume), `run` while paused | ✅ same |
| 97 | `glob` | ✅ CliJourneys › GlobAndProbeAnswerForRealFiles |
| 98 | `probe` | ✅ same; the contended-handle cases are installer-smoke "Leg A: the probe meets four real contended handles" |
| 99 | `notify show` | ✅ CliJourneys › NotifyVerbsShowTestAndReset |
| 100 | `notify test` (local webhook sink) | ✅ same |
| 101 | `notify status` | ✅ same |
| 102 | `notify reset` | ✅ same |
| 103 | `notify set-secret` (pipe) | ✅ ConsoleJourneys › ACredentialIsStoredThroughThePromptAndAMismatchIsRefused (the console's own call) |
| 104 | `secret set --from-file` | ✅ CliJourneys › SecretsAreSetListedTestedImportedAndRemoved |
| 105 | `secret import` | ✅ same |
| 106 | `secret list` (never values) | ✅ same |
| 107 | `secret test` | ✅ same |
| 108 | `secret remove` | ✅ same |
| 109 | `import --out` | ✅ CliJourneys › ImportConvertsALogrotateConfigurationIntoJobFiles |
| 110 | `doctor` | ✅ CliJourneys › DoctorHostStatusAndTheTaskXmlDescribeTheMachine |
| 111 | `host status` | ✅ same |
| 112 | `host export-task` | ✅ same |
| 113 | `scan` | ✅ same |
| 114 | `host use task/none` | ✅ ConsoleJourneys › SchedulingRegistersTheTaskAndSwitchesBackToNothing; installed, as SYSTEM: installer-smoke "Leg A: the scheduled task is registered and hardened" |
| 115 | `host repair --acl` | ✅ ConsoleJourneys › RepairPermissionsSecuresTheConfigurationDirectory |
| 116 | `host path-add` / `host path-remove` | ❌ (c) installer-smoke "Leg A: Event Log source and PATH" (machine PATH, on the installed product) |
| 117 | `update check` / `update apply` | ❌ (c) Core.Tests UpdateCommandTests; a live check needs the GitHub feed, a live apply installs a release |
