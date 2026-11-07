# WinLogRotate installer (NSIS)

`winlogrotate.nsi` builds three installers from one script:

```sh
makensis /WX /V2 /DVERSION=<v> winlogrotate.nsi                  # WinLogRotate-Setup-<v>.exe
makensis /WX /V2 /DVERSION=<v> /DFULL_ONLY winlogrotate.nsi      # WinLogRotate-Setup-<v>-full.exe
makensis /WX /V2 /DVERSION=<v> /DMINIMAL_ONLY winlogrotate.nsi   # WinLogRotate-Setup-<v>-min.exe
```

`./build.sh <version>` (Git Bash) or `build.bat <version>` publishes the exes the way ci.yml's
release job does and then runs all three.

Each carries `winlogrotate.exe` (the NativeAOT CLI) and `winlogrotate-gui.exe` (the console).
The standard installer and `-full` ship the self-contained console. `-min` ships the
framework-dependent one, and only `-min` offers to fetch the .NET 10 Desktop Runtime. CI stages
the console exe that matches each variant before running makensis (`ci.yml`, release job).
`installer-smoke` installs, upgrades and uninstalls for real on Windows.

The structure is the sibling apps' (displayRotate's installer is the reference) with one
inversion: all-users is the preselected scope, because the run host executes as SYSTEM, hooks
need a directory an ordinary account cannot write, and the Event Log source needs an
administrator.

## Switches

| Switch | Meaning |
|---|---|
| `/S` | Silent. Every MessageBox carries a silent default (`/SD`); `ArchitectureTests` fails the build if one does not. |
| `/AllUsers`, `/CurrentUser` | Scope. An explicit switch is obeyed. Without one, an existing install's scope is kept (per-user first), and a new install is all-users. |
| `/HOST=task\|none` | Register the scheduled task, or nothing. Anything else exits 2 before anything is installed. Without it, an existing install's run host is kept. |
| `/CONFIG=<dir>` | Copy every `*.toml` from that directory into `conf.d`, before the directory is hardened. |
| `/NOPATH` | Leave PATH alone. PATH is otherwise edited by the CLI, never by NSIS (which truncates at 1024 characters). |
| `/NORUNTIME` | `-min` only: never fetch the .NET runtime. A runtime that is already there is still used. |
| `/FORCEIDLE` | After two minutes of waiting for a rotation in progress, go ahead anyway. Off by default: the process being replaced may be mid-file on a multi-gigabyte log. |
| `/RESTART` | With `/S`, start the console again through `explorer.exe`, as the desktop user rather than elevated. What an in-app update passes. |
| `/D=<dir>` | Install folder (NSIS standard, must be last). |
| `/RESUME` | Internal. The elevated copy that picking all-users starts gets it, and opens on the run-host page (Back disabled) instead of starting over. |
| `uninstall.exe /PURGEDATA` | Remove the configuration, state, history and the secret store's entropy too. Without it the data is kept, silent or not. |

| Exit code | Meaning |
|---|---|
| 0 | Installed. |
| 2 | `/HOST=` named no run model this build has. Nothing was installed. |
| 3 | A silent all-users run without administrator rights. Nothing was installed; run it from an elevated prompt. |

These are WinLogRotate's own numbers and differ from the siblings' (where 2 is the silent
all-users refusal): deployment scripts already test for them.

## Elevation

The installer runs `asInvoker` (`RequestExecutionLevel user` after the MultiUser include), so
opening it never shows a UAC prompt. `.onInit` records the real account type, then tells
MultiUser it is `Admin` so the scope page renders for a standard user too. If all-users is
picked by an unelevated token, `ElevateForAllUsers` (the page's leave hook) re-runs the
installer through `runas` with the switches it was given, plus `/AllUsers /RESUME`, and quits.
That copy skips Welcome, the licence and the scope page, and continues at *Scheduling*.
Declining UAC falls back to a per-user install and says what that leaves out.

The uninstaller works out its own scope from which hive's `InstallLocation` names its folder.
For an all-users install without an administrator token, it hands over to an elevated
`$INSTDIR\uninstall.exe` with the same switches, so `/S` stays quiet and `/PURGEDATA` still
purges. It removes only its own uninstall key, and the scheduled task only when it is the
all-users install.

A silent run never elevates: `/S /AllUsers` without an administrator token exits 3.

## Closing what is running

- **A rotation:** `WaitForIdle` waits on the rotation mutex. After two minutes it asks, and the
  silent answer is Cancel: nothing is changed (`/FORCEIDLE` overrides). The installer and the
  uninstaller share the one macro.
- **The console:** the siblings' close macro (`GUI_CLOSE_FUNCS`). It asks the window to quit
  over its quit event and waits on its single-instance mutex. Then it asks, offers to force it,
  and finally to force it as an administrator. Forcing the console is safe where forcing a
  rotation is not: the window holds no work of its own.

## Names shared with the app

`ROTATION_MUTEX`, `GUI_QUIT_EVENT`, `GUI_INSTANCE_MUTEX`, `SERVICE_NAME`, `TASK_PATH`,
`UNINST_KEY`, `HostKind`, `BuildVariant`, `DATA_SDDL` and `EVENTLOG_KEY` are duplicated from
`src/WinLogRotate.Hosting/Names.cs` and `Sddl`. `HostingTests.InstallerNamePinningTests` holds
the two together. `UpdateHelpersTests` holds the switches the updater passes, and the asset
names the release publishes, to this script and `ci.yml`.
