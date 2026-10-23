# Screenshots

Every page and dialog of the console (`winlogrotate-gui.exe`), on a fictional web server with three
jobs. Made by the docs tour (`tests/WinLogRotate.Gui.Tests`), never by hand — the README's
[Screenshots](../../README.md#screenshots) section says how to regenerate them. Everything the
console does, it does by running `winlogrotate.exe`, so each page has a CLI equivalent.

## Jobs

Every job on the machine: whether it is on, what kind it is, which files it takes and its policy in
plain words. New, Edit, Enable/Disable and Remove change job files; Check configuration validates
them (`winlogrotate config check`).

<img src="jobs-list.png" alt="The Jobs page with three jobs: IIS W3SVC1 (manage), Legacy importer (disabled) and Shop API (rotate)" width="800">

## Jobs, before the first one

With no jobs, the page says what a job is and offers to add one.

<img src="jobs-empty.png" alt="The Jobs page with no jobs: a sentence explaining jobs, an Add a job button and a hint about winlogrotate import" width="800">

## Check configuration

A job file with mistakes is skipped and named while the rest of the machine rotates. Check
configuration says which file, which line and why — here a misspelled key and a negative `maxage`.

<img src="jobs-check-problems.png" alt="The Configuration dialog with its details open: a warning that compresss is not a setting and an error that maxage = -1 would delete every archive" width="460">

## Job editor

A menu of sections — Files, How, When, Keep, Old copies, Before & after, If things go wrong — each
with its plain choices first and every other setting under **More settings**. The sentence at the
bottom restates what the job will do; **What would happen…** shows a dry run of it.

<img src="jobs-editor.png" alt="The job editor for Shop API on its Files section, with the pattern C:/WinLogRotate-demo/logs/shop/*.log matching one file" width="800">

## Job editor: Keep

How many old copies to keep and for how long. An empty field under More settings inherits its
value from `[defaults]` or the built-in default, and stays inherited unless you type in it.

<img src="jobs-editor-keep.png" alt="The job editor's Keep section: keep 7 old copies, and inherited fields for the first archive number, minimum age, minimum size and maximum matches" width="800">

## Remove a job

Removing deletes the job's file, so it asks first and points at Disable, which can be undone.

<img src="jobs-remove.png" alt="The Remove job confirmation: Delete 'Shop API' and its file? with Yes and No" width="460">

## Run

**Dry run** shows exactly what **Rotate now** would do and changes nothing; both act on every
enabled job whether or not it is due, like `logrotate -f`.

<img src="run-dry-run.png" alt="The Run page after a dry run: the IIS job would delete six old logs and compress seven, and the Shop API job would shift its archives and recreate its log" width="800">

## History

The journal: every file compressed, moved or deleted, when, by which job, and why — one line per
operation, failures included (`winlogrotate journal`).

<img src="history-journal.png" alt="The History page with three nights of operations, including a compression that failed because another process held the file" width="800">

## Scheduling

What runs rotations: the scheduled task (daily, as SYSTEM, at the time shown) or nothing. This
machine has no task registered yet, which the page says in so many words.

<img src="scheduling-task.png" alt="The Scheduling page: currently none (not registered), with the scheduled task option at 02:30 and the Neither option selected" width="800">

## Notifications

Where failures are reported: targets above, providers below with where they send and which stored
credential they use. Send test proves a channel works (`winlogrotate notify test`).

<img src="notifications-providers.png" alt="The Notifications page: reporting is on, with an email provider via smtp.contoso.example and a webhook provider, each using a stored secret" width="800">

## Set credential

Stores the selected provider's password or token in the encrypted store; the value goes to the
elevated CLI over a private pipe, never on a command line.

<img src="notifications-credential.png" alt="The credential dialog: a value field and a repeat field, both masked, with Store and Cancel" width="420">

## Settings

Updates (manual unless you ask for a daily check) and the `winlogrotate doctor` report: paths,
permissions, run host and notification state in one place.

<img src="settings-overview.png" alt="The Settings page: the Updates choice and the doctor report showing a hardened config directory with hooks permitted" width="800">
