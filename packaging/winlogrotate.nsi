; WinLogRotate installer.
;
; The same structure as the sibling installers (displayRotate's is the reference), with one
; inversion: this is a system tool, so all-users is the preselected scope and the run host
; executes as SYSTEM. Everything that follows from that is commented where it happens.
;
; Build:  makensis /WX /V2 /DVERSION=1.0.0 winlogrotate.nsi
;         makensis /WX /V2 /DVERSION=1.0.0 /DFULL_ONLY winlogrotate.nsi
;         makensis /WX /V2 /DVERSION=1.0.0 /DMINIMAL_ONLY winlogrotate.nsi
;
; Switches:   /S  /AllUsers  /CurrentUser  /HOST=task|none  /CONFIG=<dir>  /NOPATH  /NORUNTIME
;             /FORCEIDLE  /RESTART  /D=<dir> (last)
;             /RESUME is internal: the elevated copy ElevateForAllUsers starts gets it, and opens
;             on the step after the scope page instead of on Welcome again.
; Uninstall:  /S  /PURGEDATA  /FORCEIDLE
; Exit codes: 2 = /HOST= names no run model this build has (nothing installed)
;             3 = silent all-users run without administrator rights (nothing installed)
;             4 = silent /NORUNTIME -min install with the .NET Desktop Runtime missing (nothing installed)
; packaging/README.md explains the design; ci.yml's installer-smoke job runs it on Windows.

Unicode true

!ifndef VERSION
  !define VERSION "0.0.0"
!endif

!define APP        "WinLogRotate"
!define PUBLISHER  "phoen-ix"
!define CLI        "winlogrotate.exe"
!define GUI        "winlogrotate-gui.exe"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP}"

; ---------------------------------------------------------------------------------------
; Names duplicated from src/WinLogRotate.Hosting/Names.cs. A test pins these two files
; together, because renaming one alone fails silently: the installer simply stops noticing a
; running rotation and overwrites the executable underneath it.
; ---------------------------------------------------------------------------------------
!define ROTATION_MUTEX  "Global\WinLogRotate.Rotation"
!define GUI_QUIT_EVENT  "Local\WinLogRotate-quit-7f3a1c"
!define GUI_INSTANCE_MUTEX "Local\WinLogRotate.Gui"
!define SERVICE_NAME    "WinLogRotate"
!define TASK_PATH       "\WinLogRotate\Rotate"
!define EVENTLOG_KEY    "SYSTEM\CurrentControlSet\Services\EventLog\Application\WinLogRotate"

; SYSTEM and Administrators full control, Users read+execute, inheritance severed.
; Duplicated from Sddl.ConfigDirectory and asserted by the installer smoke test.
!define DATA_SDDL "O:BAG:SYD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)"

; Two values under the uninstall key that the application reads back: which run host this
; install was set up with, and which GUI build it carries. Both are what `update apply` needs
; to fetch the matching installer and re-run it with the switches this install was made with.
!define REG_HOST_KIND     "HostKind"
!define REG_BUILD_VARIANT "BuildVariant"

!define DOTNET_URL "https://dotnet.microsoft.com/download/dotnet/11.0"

!ifdef FULL_ONLY
  !define SINGLE_BUILD
  !define VARIANT   " (all-in-one)"
  !define OUTSUFFIX "-full"
!endif
!ifdef MINIMAL_ONLY
  !define SINGLE_BUILD
  !define VARIANT   " (needs .NET)"
  !define OUTSUFFIX "-min"
!endif
!ifndef VARIANT
  !define VARIANT   ""
  !define OUTSUFFIX ""
!endif

; Which GUI build this installer carries, recorded so an in-app update fetches the same kind.
; Named after the GUI, not the installer file: the unsuffixed installer ships the self-contained
; GUI exactly as -full does, and an update from either should land on -full.
!ifdef MINIMAL_ONLY
  !define BUILD_VARIANT "min"
!else
  !define BUILD_VARIANT "full"
!endif

Name        "${APP} ${VERSION}${VARIANT}"
OutFile     "WinLogRotate-Setup-${VERSION}${OUTSUFFIX}.exe"
BrandingText "${APP} ${VERSION}${VARIANT}"
SetCompressor /SOLID lzma

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "nsDialogs.nsh"
!include "FileFunc.nsh"
!include "WordFunc.nsh"
!include "x64.nsh"

; FileFunc and WordFunc make their macros available on demand rather than automatically. The
; installer-side ${GetOptions} and ${GetParameters} come for free, but the uninstaller's un.
; variants and every WordFunc function have to be requested by name - and the failure mode is
; "Invalid command: ${WordFind}" at compile time, not something subtler.
!insertmacro WordFind
!insertmacro un.GetParameters
!insertmacro un.GetOptions

; ---------------------------------------------------------------------------------------
; Multi-user, the siblings' flow with all-users preselected.
;
; Opening the installer raises no UAC prompt: RequestExecutionLevel user, after the include,
; overrides the "highest" MULTIUSER_EXECUTIONLEVEL emits (the define stays because the mode
; page refuses to compile without it). The prompt comes when it is needed - ElevateForAllUsers
; re-launches the installer elevated the moment all-users is picked, and that copy continues at
; the run-host page rather than starting over (/RESUME).
;
; MULTIUSER_INSTALLMODE_DEFAULT_CURRENTUSER stays absent, but that alone no longer makes
; all-users the default: under asInvoker even an administrator arrives with a filtered token,
; which MultiUser reads as a standard user and puts in per-user mode. .onInit therefore selects
; all-users itself when no switch and no existing install say otherwise.
; ---------------------------------------------------------------------------------------
!define MULTIUSER_EXECUTIONLEVEL Highest
!define MULTIUSER_MUI
!define MULTIUSER_USE_PROGRAMFILES64
!define MULTIUSER_INSTALLMODE_INSTDIR "${APP}"
!include "MultiUser.nsh"
RequestExecutionLevel user

Var RealPrivileges
Var DataDir
Var HostChoice          ; task | none
Var HostGiven           ; 1 when /HOST= was on the command line, so it beats the recorded kind
Var PurgeData           ; 1 once the uninstaller has decided to remove the data directory
Var HostPage
Var HostTask
Var HostNone
Var ForceIdle           ; 1 when /FORCEIDLE was passed: a rotation stuck past two minutes is not waited for
Var GuiRunning          ; 1 | 0, set by ${UN}GuiIsRunning
Var Resume              ; "1" in the elevated copy ElevateForAllUsers started (/RESUME), "2" once it
                        ; has shown its first page: Welcome, License and the scope page were done

!define ERROR_ACCESS_DENIED 5

!define MUI_ICON   "winlogrotate.ico"
!define MUI_UNICON "winlogrotate.ico"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_SMALLDESC
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Start the WinLogRotate console now"
!define MUI_FINISHPAGE_RUN_FUNCTION "LaunchGui"

; The elevated copy that "anyone who uses this computer" starts skips the three pages the first
; copy already showed and continues at the run-host page (SkipIfResumed, FirstPageAfterResume).
!define MUI_PAGE_CUSTOMFUNCTION_PRE SkipIfResumed
!insertmacro MUI_PAGE_WELCOME
!define MUI_PAGE_CUSTOMFUNCTION_PRE SkipIfResumed
!insertmacro MUI_PAGE_LICENSE "..\LICENSE"

; ---------------------------------------------------------------------------------------
; The install-mode page says what the choice actually costs, on the page where it is made.
;
; Hooks - postrotate commands, service control - run programs, and that is only defensible
; from a directory an ordinary account cannot write. A per-user install keeps its
; configuration in the user's own profile, which its owner can necessarily write, so hooks
; are refused there permanently and by design. That is not a defect and there is nothing to
; repair, but somebody who picks "just me" and later finds their postrotate silently doing
; nothing deserves to have been told here rather than in a support thread.
;
; These are declared with /IfNDef inside MultiUser.nsh, so defining them first wins.
; ---------------------------------------------------------------------------------------
!define MULTIUSER_INSTALLMODEPAGE_TEXT_TOP \
  "Choose whether to install WinLogRotate for everyone on this machine or only for you.$\r$\n$\r$\n\
Rotation, compression, retention, scheduling and reporting failures by email or webhook work the \
same either way. Hooks - postrotate commands and service control - need the all-users install, \
because they run programs and that is only safe from a directory an ordinary account cannot \
write. Reporting to the Windows Event Log needs it too, because registering an event source \
needs administrator."
!define MULTIUSER_INSTALLMODEPAGE_TEXT_ALLUSERS \
  "Anyone who uses this computer (recommended - runs as SYSTEM, hooks available)"
!define MULTIUSER_INSTALLMODEPAGE_TEXT_CURRENTUSER \
  "Only for me (no administrator needed; hooks are disabled)"

!define MULTIUSER_PAGE_CUSTOMFUNCTION_PRE SkipIfResumed
!define MULTIUSER_PAGE_CUSTOMFUNCTION_LEAVE ElevateForAllUsers
!insertmacro MULTIUSER_PAGE_INSTALLMODE
Page custom HostPageCreate HostPageLeave
!define MUI_PAGE_CUSTOMFUNCTION_SHOW FirstPageAfterResume
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

; =======================================================================================
; Helpers
; =======================================================================================

; Where configuration, state and the journal live. ProgramData for a machine-wide install so
; the SYSTEM run host can reach it; APPDATA for a per-user one.
Function ResolveDataDir
  ${If} $MultiUser.InstallMode == "AllUsers"
    StrCpy $DataDir "$APPDATA\..\..\ProgramData\${APP}"
    ExpandEnvStrings $0 "%ProgramData%"
    ${If} $0 != "%ProgramData%"
      StrCpy $DataDir "$0\${APP}"
    ${EndIf}
  ${Else}
    StrCpy $DataDir "$APPDATA\${APP}"
  ${EndIf}
FunctionEnd

; Waits for any rotation in progress to finish before we replace the executable underneath it.
;
; The sibling apps force-kill a stuck tray app after a timeout, because nothing is lost by
; doing so. Here the process we might kill is halfway through a MoveFileEx on a multi-gigabyte
; log, and losing a log file is the one outcome this product exists to prevent. So the timeout
; CANCELS by default; /FORCEIDLE is the explicit opt-out. One macro for both halves: the
; uninstaller used to carry on silently after two minutes and ignore /FORCEIDLE, so the same
; switch meant two different things.
!macro WAIT_FOR_IDLE UN
Function ${UN}WaitForIdle
  Push $0
  Push $R1
  StrCpy $R1 0

  wait_loop:
    System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "${ROTATION_MUTEX}") p .r0'
    ${If} $0 == 0
      Goto wait_done
    ${EndIf}
    System::Call 'kernel32::CloseHandle(p r0)'

    IntOp $R1 $R1 + 1
    ${If} $R1 > 120
      ${If} $ForceIdle == "1"
        Goto wait_done
      ${EndIf}

      ; /SD IDCANCEL: NSIS does not suppress message boxes under /S, so a box without a silent
      ; default hangs an unattended run forever on a dialog nobody can see. Cancelling is also
      ; the safe answer - see the note above about what we would otherwise be killing.
      MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION \
        "A log rotation has been running for two minutes.$\n$\nWaiting avoids interrupting it \
mid-file. Press OK to keep waiting, or Cancel to stop and change nothing." \
        /SD IDCANCEL IDOK continue_waiting
      Abort "A rotation was still running; nothing was changed."

      continue_waiting:
        StrCpy $R1 0
    ${EndIf}

    Sleep 1000
    Goto wait_loop

  wait_done:
  Pop $R1
  Pop $0
FunctionEnd
!macroend
!insertmacro WAIT_FOR_IDLE ""
!insertmacro WAIT_FOR_IDLE "un."

; Closes a running console window before its exe is replaced: asks it to quit over the quit
; event, waits on its single-instance mutex, and only then offers to force it. The same macro
; displayRotate uses, adapted. It replaced a quiet ask-and-wait that gave up after ten seconds
; and left the File below to fail on a locked winlogrotate-gui.exe. Forcing the console is safe
; where forcing a rotation is not: the window holds no work of its own, and anything it started
; through the CLI keeps running.
!macro GUI_CLOSE_FUNCS UN ACTION

Function ${UN}GuiIsRunning
  Push $0
  Push $1
  StrCpy $GuiRunning "0"
  ; 1) The console window's single-instance mutex. "Access denied" means the mutex is there but
  ;    owned by a token we can't open (the WinLogRotate console running elevated while we aren't) - still
  ;    a running the WinLogRotate console.
  System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "${GUI_INSTANCE_MUTEX}") p .r0 ?e'
  Pop $1
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    StrCpy $GuiRunning "1"
    Goto air_done
  ${ElseIf} $1 = ${ERROR_ACCESS_DENIED}
    StrCpy $GuiRunning "1"
    Goto air_done
  ${EndIf}
  ; 2) The image name. Catches an instance in another user's session (a Local\ mutex is
  ;    per-session), and a command-line run, either of which still locks the exe.
  ;    Fully qualified so a planted tasklist.exe beside an elevated installer can't run.
  nsExec::ExecToStack /TIMEOUT=10000 '"$SYSDIR\tasklist.exe" /NH /FO CSV /FI "IMAGENAME eq ${GUI}"'
  Pop $0
  Pop $1
  ; A match is a CSV row: "winlogrotate-gui.exe","1234",... The no-match line ("INFO: No tasks...")
  ; is translated on localised Windows but is never quoted - so test the first character.
  StrCpy $1 $1 1
  ${If} $0 == 0
  ${AndIf} $1 == '"'
    StrCpy $GuiRunning "1"
  ${EndIf}
 air_done:
  Pop $1
  Pop $0
FunctionEnd

Function ${UN}GuiRequestQuit
  Push $0
  Push $1
  System::Call 'kernel32::OpenEventW(i 0x0002, i 0, w "${GUI_QUIT_EVENT}") p .r0 ?e'
  Pop $1
  ${If} $0 != 0
    System::Call 'kernel32::SetEvent(p r0)'
    System::Call 'kernel32::CloseHandle(p r0)'
    DetailPrint "Asked the WinLogRotate console to close..."
  ${ElseIf} $1 = ${ERROR_ACCESS_DENIED}
    DetailPrint "The WinLogRotate console is running with higher privileges - it cannot be asked to close."
  ${Else}
    DetailPrint "the WinLogRotate console offers no quit channel here (another session, or the command line)."
  ${EndIf}
  ; Wait on the mutex, not the clock: it exists exactly as long as the console window does.
  ; Ten seconds is the ceiling; a healthy instance is gone in well under one.
  StrCpy $1 0
 arq_loop:
  Call ${UN}GuiIsRunning
  ${If} $GuiRunning == "0"
    ; The mutex goes a moment before the process does, and the exe stays locked until then.
    Sleep 500
    DetailPrint "the WinLogRotate console closed."
    Goto arq_done
  ${EndIf}
  ${If} $1 >= 20
    Goto arq_done
  ${EndIf}
  Sleep 500
  IntOp $1 $1 + 1
  Goto arq_loop
 arq_done:
  Pop $1
  Pop $0
FunctionEnd

Function ${UN}GuiForceClose
  Push $0
  DetailPrint "Force-closing the WinLogRotate console..."
  nsExec::ExecToLog '"$SYSDIR\taskkill.exe" /F /IM ${GUI}'
  Pop $0
  Pop $0
FunctionEnd

Function ${UN}EnsureGuiClosed
  Call ${UN}GuiIsRunning
  ${If} $GuiRunning == "0"
    Return
  ${EndIf}
  MessageBox MB_YESNOCANCEL|MB_ICONEXCLAMATION|MB_TOPMOST|MB_SETFOREGROUND "The WinLogRotate console is running and has to close before ${ACTION} can continue.$\n$\nYes - close the WinLogRotate console now.$\nNo - leave it to me; I'll close it myself.$\nCancel - stop and change nothing." /SD IDYES IDYES eac_ask IDNO eac_retry
  Abort "Cancelled - The WinLogRotate console is still running."
 eac_ask:
  Call ${UN}GuiRequestQuit
  ${If} $GuiRunning == "0"
    Return
  ${EndIf}
 eac_retry:
  Call ${UN}GuiIsRunning
  ${If} $GuiRunning == "0"
    Return
  ${EndIf}
  ; /SD IDIGNORE: unattended, force it rather than fail - the WinLogRotate console holds nothing that a
  ; forced close would lose (settings are written the moment they are saved).
  MessageBox MB_ABORTRETRYIGNORE|MB_ICONEXCLAMATION|MB_TOPMOST|MB_SETFOREGROUND "The WinLogRotate console is still running.$\n$\nRetry - I've closed it; check again.$\nIgnore - force it closed.$\nAbort - stop and change nothing." /SD IDIGNORE IDRETRY eac_retry IDIGNORE eac_force
  Abort "Cancelled - The WinLogRotate console is still running."
 eac_force:
  Call ${UN}GuiForceClose
  Call ${UN}GuiIsRunning
  ${If} $GuiRunning == "0"
    Return
  ${EndIf}
  ; Still there: it runs elevated, or in another account. /SD IDNO so a silent run never
  ; raises a UAC prompt nobody is there to answer.
  MessageBox MB_YESNO|MB_ICONEXCLAMATION|MB_TOPMOST|MB_SETFOREGROUND "The WinLogRotate console is running as administrator or in another account, so it can't be closed from here.$\n$\nClose it using administrator rights?" /SD IDNO IDNO eac_stuck
  ClearErrors
  ExecShellWait "runas" "$SYSDIR\taskkill.exe" "/F /IM ${GUI}" SW_HIDE
  Call ${UN}GuiIsRunning
  ${If} $GuiRunning == "0"
    Return
  ${EndIf}
 eac_stuck:
  MessageBox MB_RETRYCANCEL|MB_ICONSTOP|MB_TOPMOST|MB_SETFOREGROUND "The WinLogRotate console could not be closed.$\n$\nClose the console window, or re-run this as administrator, then Retry." /SD IDCANCEL IDRETRY eac_retry
  Abort "The WinLogRotate console could not be closed."
FunctionEnd
!macroend
!insertmacro GUI_CLOSE_FUNCS ""    "Setup"
!insertmacro GUI_CLOSE_FUNCS "un." "the uninstaller"

; =======================================================================================
; Pages: resuming after the UAC prompt, and starting the console
; =======================================================================================

; A page the first copy already showed: skipped in the elevated copy.
Function SkipIfResumed
  ${If} $Resume != ""
    Abort
  ${EndIf}
FunctionEnd

; The elevated copy's first page has no Back: the pages behind it were the first copy's.
Function FirstPageAfterResume
  ${If} $Resume == "1"
    GetDlgItem $0 $HWNDPARENT 3
    EnableWindow $0 0
    StrCpy $Resume "2"
  ${EndIf}
FunctionEnd

; Through explorer.exe, not directly. An all-users install runs elevated, and a window started
; by an elevated process inherits that token: the console would then run as administrator for
; the rest of its life, and everything it launched would too. Handing the path to the shell
; starts it with the desktop user's ordinary token instead. Server Core has no explorer.exe and
; no desktop to speak of; there the window is started directly, which is the best that can be
; done, and it is at least started.
Function LaunchGui
  ${If} ${FileExists} "$WINDIR\explorer.exe"
    Exec '"$WINDIR\explorer.exe" "$INSTDIR\${GUI}"'
  ${Else}
    Exec '"$INSTDIR\${GUI}"'
  ${EndIf}
FunctionEnd

; The mode page's leave handler, after MultiUser has applied the choice. A standard user (or an
; administrator, whose token is filtered under asInvoker) who picked all-users hands the install
; to an elevated copy of this installer. Everything this run was started with goes along -
; /HOST= /CONFIG= /NOPATH /NORUNTIME /FORCEIDLE - plus /AllUsers and /RESUME.
Function ElevateForAllUsers
  ${If} $MultiUser.InstallMode != "AllUsers"
    Return
  ${EndIf}
  ${If} $RealPrivileges == "Admin"
  ${OrIf} $RealPrivileges == "Power"
    Return                        ; already elevated - nothing to do
  ${EndIf}
  ${GetParameters} $0
  ClearErrors
  ExecShell "runas" "$EXEPATH" "$0 /AllUsers /RESUME"
  ${IfNot} ${Errors}
    Quit                          ; the elevated copy takes over
  ${EndIf}
  ; UAC declined, or no administrator account available.
  Call MultiUser.InstallMode.CurrentUser
  Call ResolveDataDir
  MessageBox MB_OK|MB_ICONINFORMATION|MB_TOPMOST|MB_SETFOREGROUND "Administrator rights weren't granted, so WinLogRotate will be installed for you only. Hooks and the Event Log are not available in a per-user install." /SD IDOK
FunctionEnd

; Is a .NET 11 Desktop Runtime present?
;
; Asks the host where it lives rather than assuming a path - and reads the 32-BIT view, which
; is where .NET records it. That is not a workaround for WOW64; it is the documented contract.
; dotnet/designs, install-locations.md: "this registry key is redirected ... so it's important
; that both installers and the host access only the 32-bit view of the registry", and the host
; itself opens it with KEY_WOW64_32KEY unconditionally
; (dotnet/runtime, hostmisc/pal.windows.cpp: "The registry search occurs in the 32-bit registry
; in all cases").
;
; This previously read the 64-bit view, where the key is specified never to appear, so the read
; returned empty on every machine and the $PROGRAMFILES64 fallback below did all the work. That
; is fine until somebody installs .NET somewhere else - which is precisely the case the registry
; lookup exists to handle, and precisely when it silently did not.
!ifdef MINIMAL_ONLY
Function DotnetPresent
  Push $0
  Push $1
  Push $2
  Push $3
  StrCpy $3 "0"

  SetRegView 32
  ReadRegStr $0 HKLM "SOFTWARE\dotnet\Setup\InstalledVersions\x64" "InstallLocation"
  SetRegView default
  ${If} $0 == ""
    StrCpy $0 "$PROGRAMFILES64\dotnet"
  ${EndIf}

  FindFirst $1 $2 "$0\shared\Microsoft.WindowsDesktop.App\11.*"
  FindClose $1
  ${If} $2 != ""
    StrCpy $3 "1"
  ${EndIf}

  DetailPrint "Desktop Runtime 11 under $0: $2"

  ; Restore every register and leave the answer on the stack. $3 used to be written without
  ; ever being pushed, which silently corrupted whatever the caller was holding in it.
  StrCpy $0 $3
  Pop $3
  Pop $2
  Pop $1
  Exch $0
FunctionEnd

Function EnsureDotnet
  Push $0
  Push $1

  ; Present already: nothing to do, with or without /NORUNTIME. The switch used to be checked
  ; first, so an interactive /NORUNTIME run offered the download page for a runtime that was
  ; there.
  Call DotnetPresent
  Pop $0
  ${If} $0 == "1"
    Goto done
  ${EndIf}

  ClearErrors
  ${GetParameters} $0
  ${GetOptions} $0 "/NORUNTIME" $1
  ${IfNot} ${Errors}
    Goto manual
  ${EndIf}

  ; /SD IDYES so a scripted deployment provisions the runtime unattended rather than stalling.
  MessageBox MB_YESNO|MB_ICONQUESTION \
    "The graphical console needs the .NET 11 Desktop Runtime, which is not installed.$\n$\n\
Download and install it now?$\n$\n\
(The command-line engine does not need it, and will work either way.)" \
    /SD IDYES IDNO manual

  ; Resolve winget through where.exe, both fully qualified. A bare name would be resolved
  ; through PATH by an elevated process, so a planted winget.exe in a writable directory
  ; would run as administrator.
  ;
  ; /OEM because where.exe writes console-codepage output: without it a profile path containing
  ; non-ASCII characters comes back mangled, and the path we would then execute is not the path
  ; where.exe found.
  nsExec::ExecToStack /OEM '"$SYSDIR\where.exe" winget.exe'
  Pop $0
  Pop $1
  ${If} $0 != 0
    ; Expected on most machines, and worth saying so rather than looking like a defect. winget
    ; ships as the App Installer MSIX: it first appears in Windows images at 11 22H2, lives
    ; under Program Files\WindowsApps behind a per-user execution alias, and is absent from
    ; Windows Server and from every Windows 10 image.
    DetailPrint "winget was not found on PATH (where.exe exit $0); the runtime cannot be installed automatically."
    Goto manual
  ${EndIf}

  ; WordFind returns the literal string "2" when the input contains no delimiter at all, which
  ; would then be executed as the program name. where.exe always CRLF-terminates, so this is a
  ; belt against a future caller rather than a live bug - but executing "2" is a poor failure.
  ${WordFind} "$1" "$\r$\n" "+1{" $1
  ${If} $1 == "2"
  ${OrIf} $1 == ""
    DetailPrint "Could not parse where.exe output for winget."
    Goto manual
  ${EndIf}
  DetailPrint "winget: $1"

  DetailPrint "Installing the .NET 11 Desktop Runtime..."
  nsExec::ExecToLog '"$1" install --id Microsoft.DotNet.DesktopRuntime.11 -e --silent \
--accept-package-agreements --accept-source-agreements'
  Pop $0

  DetailPrint "winget exited $0."

  ; Re-verify: winget reports success in cases where the runtime does not actually land, and
  ; the only answer that means anything is whether the runtime is now on disk.
  Call DotnetPresent
  Pop $0
  ${If} $0 == "1"
    DetailPrint "The .NET 11 Desktop Runtime is now installed."
    Goto done
  ${EndIf}
  DetailPrint "winget ran but the Desktop Runtime is still not present."

  manual:
    ; /SD IDNO: an unattended run must never block, and must never open a browser.
    MessageBox MB_YESNO|MB_ICONINFORMATION \
      "The .NET 11 Desktop Runtime could not be installed automatically.$\n$\n\
Open the download page? The command-line engine works without it." \
      /SD IDNO IDNO done
    ExecShell "open" "${DOTNET_URL}"

  done:
  Pop $1
  Pop $0
FunctionEnd
!endif

; =======================================================================================
; Run-host page
; =======================================================================================

Function HostPageCreate
  ${If} ${Silent}
    Abort
  ${EndIf}

  !insertmacro MUI_HEADER_TEXT "Scheduling" \
    "Choose what runs rotations. You can change this at any time afterwards."

  nsDialogs::Create 1018
  Pop $HostPage
  ${If} $HostPage == error
    Abort
  ${EndIf}

  ${NSD_CreateLabel} 0 0 100% 24u \
    "Rotations need something to trigger them. A scheduled task is recommended: nothing stays \
resident, missed runs are caught up after a reboot, and it is visible in Task Scheduler."
  Pop $0

  ${NSD_CreateRadioButton} 0 30u 100% 12u "Scheduled task (recommended)"
  Pop $HostTask
  ${NSD_CreateLabel} 12u 42u 100% 10u "Runs daily as SYSTEM. No resident process."
  Pop $0

  ; There is deliberately no "Windows service" option, and there was one until milestone 17.
  ; This build cannot register a service host - `host use service` says so and refuses - so the
  ; radio button offered a choice that removed the working scheduled task and then failed, one
  ; click away, with the failure reported only to a log nobody reads.
  ${NSD_CreateRadioButton} 0 58u 100% 12u "Neither - I will trigger it myself"
  Pop $HostNone
  ${NSD_CreateLabel} 12u 70u 100% 10u "Install the tools only. Nothing will run on its own."
  Pop $0

  ; What .onInit decided: the switch, else the kind an existing install recorded, else task.
  ; An upgrade of an install that was set to "none" used to open this page with the task
  ; pre-selected, and Next registered a task the operator had turned off.
  ${If} $HostChoice == "none"
    ${NSD_Check} $HostNone
  ${Else}
    ${NSD_Check} $HostTask
  ${EndIf}
  Call FirstPageAfterResume
  nsDialogs::Show
FunctionEnd

Function HostPageLeave
  ${NSD_GetState} $HostTask $0
  ${If} $0 == ${BST_CHECKED}
    StrCpy $HostChoice "task"
    Return
  ${EndIf}
  StrCpy $HostChoice "none"
FunctionEnd

; =======================================================================================
; Install
; =======================================================================================

Section "-Core" SEC_CORE
  SectionIn RO
  SetOutPath "$INSTDIR"

  Call WaitForIdle
  Call EnsureGuiClosed

  ; Remove whatever host is currently registered before replacing the binaries it points at.
  nsExec::ExecToLog '"$INSTDIR\${CLI}" host use none --config-dir "$DataDir"'
  Pop $0

  ; Both binaries, in every variant. There was a !ifndef MINIMAL_ONLY here whose two branches
  ; were byte-identical, which reads as though the minimal build ships something different and
  ; does not. What actually differs between the variants is whether the .NET bootstrap section is
  ; compiled in at all - see EnsureDotnet - not which files are installed.
  File "${CLI}"
  File "${GUI}"
  File "winlogrotate.ico"
  File /oname=LICENSE.txt "..\LICENSE"

  Call ResolveDataDir
  CreateDirectory "$DataDir"
  CreateDirectory "$DataDir\conf.d"
  CreateDirectory "$DataDir\journal"

  ; run\ holds the record of when the rotation gate was first found held. Created here so it is
  ; hardened by the pass below rather than appearing later under whatever ProgramData hands out -
  ; the local account that record exists to detect must not be the account that owns it.
  CreateDirectory "$DataDir\run"

  ; A seeded configuration for unattended customer deployment: /CONFIG=\\srv\deploy\jobs.
  ; Here, not in .onInit: there it ran before the mode page and before any elevation, so the
  ; files could land in the other scope's directory, or in a ProgramData folder created by the
  ; unelevated user before the pass below had hardened it. Copied before that pass, so the
  ; copies are re-owned with everything else.
  ClearErrors
  ${GetParameters} $0
  ${GetOptions} $0 "/CONFIG=" $1
  ${IfNot} ${Errors}
    CopyFiles /SILENT "$1\*.toml" "$DataDir\conf.d"
  ${EndIf}

  ; -------------------------------------------------------------------------------------
  ; Harden the configuration directory.
  ;
  ; This is the most important thing the installer does. The run host executes as SYSTEM and
  ; reads every job file, and jobs can carry hooks that run commands - while ProgramData's
  ; default inheritance lets any local user create files there and gives CREATOR OWNER full
  ; control of what they create. Unhardened, that is a local user getting SYSTEM.
  ;
  ; Applied by shelling the CLI so there is exactly one implementation of the rule, and so the
  ; GUI's "repair permissions" button and this line cannot drift apart. icacls with well-known
  ; SIDs is the fallback: never group names, because this machine calls the Administrators
  ; group "Administratoren" and a failed icacls leaves the permissive entry in place silently.
  ; -------------------------------------------------------------------------------------
  ${If} $MultiUser.InstallMode == "AllUsers"
    nsExec::ExecToLog '"$INSTDIR\${CLI}" host repair --acl --config-dir "$DataDir"'
    Pop $0
    ${If} $0 != 0
      DetailPrint "Falling back to icacls for the configuration directory permissions."
      nsExec::ExecToLog '"$SYSDIR\icacls.exe" "$DataDir" /inheritance:r \
/grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX'
      Pop $0

      ; The children, which the line above does not reach. Rewriting a parent's DACL recomputes
      ; what a child inherits; it changes neither a child's own explicit entries nor a child's
      ; owner, and an owner holds implicit WRITE_DAC. On an upgrade, conf.d already holds job
      ; files somebody created while CREATOR OWNER was still granting them Full Control, and
      ; those stay theirs to rewrite until these two lines run. /reset on conf.d and not on
      ; $DataDir: applied to the root it would discard the protection the line above just
      ; established, which is the same self-undoing repair this pass exists to fix.
      nsExec::ExecToLog '"$SYSDIR\icacls.exe" "$DataDir\conf.d" /reset /T /C /Q'
      Pop $1
      nsExec::ExecToLog '"$SYSDIR\icacls.exe" "$DataDir" /setowner "*S-1-5-32-544" /T /C /Q'
      Pop $1

      ${If} $0 != 0
        ; Refusing to continue is correct: an install that silently leaves a
        ; SYSTEM-executing tool reading a world-writable directory is worse than no install.
        Abort "Could not secure $DataDir. Nothing was installed."
      ${EndIf}
    ${EndIf}

    ; Event Log source. Creating one needs administrator, so it happens here rather than at
    ; first run - otherwise the unelevated CLI throws the first time it tries to log.
    WriteRegStr   HKLM "${EVENTLOG_KEY}" "EventMessageFile" "%SystemRoot%\System32\EventCreate.exe"
    WriteRegDWORD HKLM "${EVENTLOG_KEY}" "TypesSupported"   7
  ${EndIf}

  ; Seed a starter configuration, but never over one that already exists - an upgrade must not
  ; overwrite a customer's jobs.
  ${IfNot} ${FileExists} "$DataDir\config.toml"
    FileOpen $0 "$DataDir\config.toml" w
    FileWrite $0 "schema = 1$\r$\n$\r$\n"
    FileWrite $0 "# Defaults for every job. Anything a job does not set comes from here.$\r$\n"
    FileWrite $0 "# Note: these differ from logrotate's compiled-in defaults, which would$\r$\n"
    FileWrite $0 "# delete your logs at 1 MiB and keep none. See docs/logrotate-compatibility.md.$\r$\n"
    FileWrite $0 "[defaults]$\r$\n"
    FileWrite $0 "daily        = true$\r$\n"
    FileWrite $0 "rotate       = 7$\r$\n"
    FileWrite $0 "compress     = true$\r$\n"
    FileWrite $0 'compresstype = "zip"$\r$\n'
    FileWrite $0 "notifempty   = true$\r$\n"
    FileWrite $0 "$\r$\n"
    FileWrite $0 "# The rotation history: what was compressed, moved or deleted, and why.$\r$\n"
    FileWrite $0 "# It looks after itself using the same manage-mode code that tidies IIS logs,$\r$\n"
    FileWrite $0 "# so it cannot grow without limit. Set enabled = false to keep no history.$\r$\n"
    FileWrite $0 "[journal]$\r$\n"
    FileWrite $0 "enabled  = true$\r$\n"
    FileWrite $0 "retain   = 30        # days$\r$\n"
    FileWrite $0 'compress = "zip"     # zip | gzip | none$\r$\n'
    FileWrite $0 'maxsize  = "50M"     # roll mid-day past this$\r$\n'
    FileWrite $0 "$\r$\n"
    FileWrite $0 "# When the scheduled task fires, on a 24-hour clock. Change it with$\r$\n"
    FileWrite $0 "# 'winlogrotate host use task --at HH:mm' or the console's Scheduling page.$\r$\n"
    FileWrite $0 "[host]$\r$\n"
    FileWrite $0 'time = "03:00"$\r$\n'
    FileWrite $0 "$\r$\n"
    FileWrite $0 "# Who finds out when rotation stops working. Commented out because a target$\r$\n"
    FileWrite $0 "# nobody chose is a target nobody reads - but eventlog: needs no credential,$\r$\n"
    FileWrite $0 "# no network and nothing to break, so it is the one to start with. Email,$\r$\n"
    FileWrite $0 "# webhooks and Pushover are in docs/notifications.md.$\r$\n"
    FileWrite $0 "#$\r$\n"
    FileWrite $0 "# [notify]$\r$\n"
    FileWrite $0 '# to = ["eventlog:"]$\r$\n'
    FileClose $0

    ; Again, now that config.toml exists. The hardening above has to run before the seeding -
    ; an install that cannot secure the directory must abort before writing anything into it -
    ; so its per-file pass sees no config.toml at all. NSIS then creates that file itself, which
    ; leaves it owned by the installing administrator's own account rather than by
    ; BUILTIN\Administrators; the gate refuses hooks for a file whose owner it does not trust,
    ; and [defaults] is exactly where a prerotate written once is inherited by every job.
    ;
    ; Not fatal if it fails. The directory is already secured and aborting here would discard a
    ; complete install over a file permission; doctor reports it and names the repair.
    ${If} $MultiUser.InstallMode == "AllUsers"
      nsExec::ExecToLog '"$INSTDIR\${CLI}" host repair --acl --config-dir "$DataDir"'
      Pop $0
      ${If} $0 != 0
        DetailPrint "The seeded config.toml could not be secured; hooks will be refused until \
'winlogrotate host repair --acl' succeeds."
      ${EndIf}
    ${EndIf}
  ${EndIf}

  WriteUninstaller "$INSTDIR\uninstall.exe"

  WriteRegStr   SHCTX "${UNINST_KEY}" "DisplayName"          "${APP}"
  WriteRegStr   SHCTX "${UNINST_KEY}" "DisplayVersion"       "${VERSION}"
  WriteRegStr   SHCTX "${UNINST_KEY}" "Publisher"            "${PUBLISHER}"
  WriteRegStr   SHCTX "${UNINST_KEY}" "DisplayIcon"          "$INSTDIR\winlogrotate.ico"
  WriteRegStr   SHCTX "${UNINST_KEY}" "InstallLocation"      "$INSTDIR"
  WriteRegStr   SHCTX "${UNINST_KEY}" "URLInfoAbout"         "https://github.com/phoen-ix/WinLogRotate"
  WriteRegStr   SHCTX "${UNINST_KEY}" "DataDir"              "$DataDir"
  WriteRegStr   SHCTX "${UNINST_KEY}" "${REG_HOST_KIND}"     "$HostChoice"
  WriteRegStr   SHCTX "${UNINST_KEY}" "${REG_BUILD_VARIANT}" "${BUILD_VARIANT}"
  WriteRegStr   SHCTX "${UNINST_KEY}" "UninstallString"      '"$INSTDIR\uninstall.exe"'
  WriteRegStr   SHCTX "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD SHCTX "${UNINST_KEY}" "EstimatedSize" $0
  WriteRegDWORD SHCTX "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD SHCTX "${UNINST_KEY}" "NoRepair" 1

  ; PATH is edited by the CLI, never by NSIS. Stock makensis builds with NSIS_MAX_STRLEN=1024
  ; and ReadRegStr silently truncates anything longer - writing that back destroys the machine
  ; PATH for every program on the system.
  ClearErrors
  ${GetParameters} $0
  ${GetOptions} $0 "/NOPATH" $1
  ${If} ${Errors}
    ; --machine only for an all-users install. A per-user install must edit only that user's
    ; PATH even when an administrator is running it, which is most of the time.
    ${If} $MultiUser.InstallMode == "AllUsers"
      nsExec::ExecToLog '"$INSTDIR\${CLI}" host path-add --machine'
    ${Else}
      nsExec::ExecToLog '"$INSTDIR\${CLI}" host path-add'
    ${EndIf}
    Pop $0
  ${EndIf}

  ; Register the chosen run host, through the same code path the GUI uses.
  ${If} $HostChoice != "none"
    nsExec::ExecToLog '"$INSTDIR\${CLI}" host use $HostChoice --config-dir "$DataDir"'
    Pop $0
    ${If} $0 != 0
      ; Not fatal: the tools are installed and usable, and the operator can retry. Saying so
      ; is much better than a successful-looking install that never rotates anything.
      DetailPrint "The run host could not be registered. Run 'winlogrotate host use $HostChoice' as administrator."
    ${EndIf}
  ${EndIf}
SectionEnd

; Only the -min installer carries the framework-dependent console. The unsuffixed one ships the
; self-contained console exactly as -full does, and compiling this in there ran winget for a
; runtime nothing installed needs.
!ifdef MINIMAL_ONLY
Section "-Runtime" SEC_RUNTIME
  Call EnsureDotnet
SectionEnd
!endif

Section "Start Menu shortcut" SEC_SM
  CreateShortcut "$SMPROGRAMS\${APP}.lnk" "$INSTDIR\${GUI}" "" "$INSTDIR\winlogrotate.ico" 0
SectionEnd

Section /o "Desktop shortcut" SEC_DESK
  CreateShortcut "$DESKTOP\${APP}.lnk" "$INSTDIR\${GUI}" "" "$INSTDIR\winlogrotate.ico" 0
SectionEnd

; =======================================================================================
; After a successful install
; =======================================================================================

; /RESTART starts the window again once the files are in place. This is what an in-app update
; passes: the console launched `update apply`, which handed over to this installer and exited,
; and EnsureGuiClosed then asked the window to close - so nothing is left to bring it back except
; us. Only under /S: an interactive run has the finish page's checkbox, and both firing would
; start the console twice.
Function .onInstSuccess
  Push $0
  Push $1
  ${If} ${Silent}
    ClearErrors
    ${GetParameters} $0
    ${GetOptions} $0 "/RESTART" $1
    ${IfNot} ${Errors}
      Call LaunchGui
    ${EndIf}
  ${EndIf}
  Pop $1
  Pop $0
FunctionEnd

; =======================================================================================
; Init
; =======================================================================================

Function .onInit
  StrCpy $HostChoice "task"
  StrCpy $HostGiven "0"

  !insertmacro MULTIUSER_INIT
  StrCpy $RealPrivileges $MultiUser.Privileges

  ; The privilege fib, kept from the reference. Stock MultiUser does not merely disable the
  ; per-machine option for a non-admin, it skips the whole page - so a standard user who knows
  ; an administrator password could never choose a machine-wide install at all.
  ${If} $RealPrivileges != "Admin"
  ${AndIf} $RealPrivileges != "Power"
    StrCpy $MultiUser.Privileges "Admin"
  ${EndIf}

  Push $R0
  Push $R1
  Push $R2
  ${GetParameters} $R0

  ; Parsed by hand, after the fib. Stock MultiUser answers an unelevated /AllUsers with a
  ; message box that has no silent default, so "/S /AllUsers" would hang forever on an
  ; invisible dialog and then report success having installed nothing. /AllUsers wins when
  ; both are given; $R2 records that a switch was given at all.
  StrCpy $R2 "0"
  ClearErrors
  ${GetOptions} $R0 "/CurrentUser" $R1
  ${IfNot} ${Errors}
    Call MultiUser.InstallMode.CurrentUser
    StrCpy $R2 "1"
  ${EndIf}
  ClearErrors
  ${GetOptions} $R0 "/AllUsers" $R1
  ${IfNot} ${Errors}
    Call MultiUser.InstallMode.AllUsers
    StrCpy $R2 "1"
  ${EndIf}

  ; /HOST=task|none for unattended deployment.
  ;
  ; Validated here rather than taken verbatim. It used to be copied straight into $HostChoice,
  ; written to the uninstall key's HostKind, and only then handed to `host use` - so /HOST=banana
  ; was recorded as the machine's run model and /HOST=service, which this build cannot register,
  ; produced a silent install that reported success and scheduled nothing. Failing here is loud
  ; and leaves nothing behind; failing later was quiet and left a lie in the registry.
  ClearErrors
  ${GetOptions} $R0 "/HOST=" $R1
  ${IfNot} ${Errors}
    ${If} $R1 == "task"
    ${OrIf} $R1 == "none"
      StrCpy $HostChoice $R1
      StrCpy $HostGiven "1"
    ${ElseIf} $R1 == "service"
      MessageBox MB_OK|MB_ICONSTOP \
        "The service host is not implemented in this build.$\n$\n\
Use /HOST=task. A scheduled task is the better fit for a log rotator anyway: nothing stays \
resident, and a run missed while the machine was off is caught up afterwards." /SD IDOK
      SetErrorLevel 2
      Quit
    ${Else}
      MessageBox MB_OK|MB_ICONSTOP \
        "'$R1' is not a run model.$\n$\nUse /HOST=task or /HOST=none." /SD IDOK
      SetErrorLevel 2
      Quit
    ${EndIf}
  ${EndIf}

  ; The scope, as the sibling installers decide it. An explicit switch is obeyed: the in-app
  ; updater always passes the scope of the copy it is updating, which may be the machine-wide
  ; one on a PC that has both. Without a switch, follow an existing install - per-user first,
  ; the order the app reads them - rather than putting a second copy beside it: a per-user
  ; install re-run silently by an administrator must not land in Program Files. Without either,
  ; all-users, the preselected scope of a system tool (see the MultiUser defines above).
  ${If} $R2 == "0"
    ReadRegStr $0 HKCU "${UNINST_KEY}" "UninstallString"
    ${If} $0 != ""
      Call MultiUser.InstallMode.CurrentUser
    ${Else}
      Call MultiUser.InstallMode.AllUsers
    ${EndIf}
  ${EndIf}

  ; Upgrade in place, so a custom directory is not orphaned.
  ReadRegStr $0 SHCTX "${UNINST_KEY}" "InstallLocation"
  ${If} $0 != ""
  ${AndIf} ${FileExists} "$0\${CLI}"
    StrCpy $INSTDIR $0
  ${EndIf}

  ; Keep the run host an existing install was set up with, unless /HOST= said otherwise. The
  ; default used to be task regardless, so every silent upgrade of an install set to "none" -
  ; and every in-app update of one - registered the scheduled task its operator had turned off.
  ${If} $HostGiven == "0"
    ReadRegStr $0 SHCTX "${UNINST_KEY}" "${REG_HOST_KIND}"
    ${If} $0 == "task"
    ${OrIf} $0 == "none"
      StrCpy $HostChoice $0
    ${EndIf}
  ${EndIf}

  ; A silent per-machine run never sees the mode page, so nothing can elevate it. Failing
  ; loudly beats half-installing into Program Files with a token that cannot write there.
  ${If} ${Silent}
  ${AndIf} $MultiUser.InstallMode == "AllUsers"
  ${AndIf} $RealPrivileges != "Admin"
  ${AndIf} $RealPrivileges != "Power"
    SetErrorLevel 3
    Quit
  ${EndIf}

!ifdef MINIMAL_ONLY
  ; Refuse a silent /NORUNTIME install that would land a console whose runtime isn't here,
  ; before a rotation is waited for, the console is closed or a byte is written - the copy
  ; already installed keeps working. An in-app update runs exactly that (InstallerArguments), and
  ; an update from a release on the previous .NET major would otherwise leave a console that
  ; cannot open. (The standard installer defaults to the full build under /S, so it never needs
  ; this.) Same shape as displayRotate's guard.
  ${If} ${Silent}
    ClearErrors
    ${GetOptions} $R0 "/NORUNTIME" $R1
    ${IfNot} ${Errors}
      Call DotnetPresent
      Pop $R1
      ${If} $R1 != "1"
        SetErrorLevel 4
        Quit
      ${EndIf}
    ${EndIf}
  ${EndIf}
!endif

  ; /FORCEIDLE, read once for WaitForIdle.
  StrCpy $ForceIdle "0"
  ClearErrors
  ${GetOptions} $R0 "/FORCEIDLE" $R1
  ${IfNot} ${Errors}
    StrCpy $ForceIdle "1"
  ${EndIf}

  ; /RESUME (internal) - this is the elevated copy ElevateForAllUsers started, so Welcome,
  ; License and the scope page were shown by the first copy. Only for that case: interactive,
  ; and for everyone.
  StrCpy $Resume ""
  ClearErrors
  ${GetOptions} $R0 "/RESUME" $R1
  ${IfNot} ${Errors}
  ${AndIfNot} ${Silent}
  ${AndIf} $MultiUser.InstallMode == "AllUsers"
    StrCpy $Resume "1"
  ${EndIf}

  ; Again whenever the scope changes - ElevateForAllUsers' fallback does - and in -Core, which
  ; is what the files are written by.
  Call ResolveDataDir

  Pop $R2
  Pop $R1
  Pop $R0
FunctionEnd

; =======================================================================================
; Uninstall
; =======================================================================================

Function un.onInit
  !insertmacro MULTIUSER_UNINIT

  ; Work out on our own which install this uninstaller belongs to. The registered uninstall
  ; strings carry no /AllUsers or /CurrentUser, and MULTIUSER_UNINIT's default is decided by
  ; the token, not by the install. Both directions are set explicitly: a per-user install
  ; uninstalled from an elevated prompt must stay per-user, or its DataDir is read from HKLM and
  ; found empty, so /PURGEDATA removes nothing and its PATH entry is left on the user's for
  ; good. The all-users mode is assigned directly, as the siblings do:
  ; un.MultiUser.InstallMode.AllUsers refuses on a standard token, and asInvoker makes every
  ; uninstaller start as one.
  ReadRegStr $0 HKCU "${UNINST_KEY}" "InstallLocation"
  ${If} $0 == "$INSTDIR"
    Call un.MultiUser.InstallMode.CurrentUser
  ${Else}
    ReadRegStr $0 HKLM "${UNINST_KEY}" "InstallLocation"
    ${If} $0 == "$INSTDIR"
      StrCpy $MultiUser.InstallMode AllUsers
      SetShellVarContext all
    ${EndIf}
  ${EndIf}

  ${un.GetParameters} $1
  StrCpy $ForceIdle "0"
  ClearErrors
  ${un.GetOptions} $1 "/FORCEIDLE" $2
  ${IfNot} ${Errors}
    StrCpy $ForceIdle "1"
  ${EndIf}

  ; An all-users uninstall needs an administrator. Hand over to an elevated copy of the
  ; registered uninstaller - $INSTDIR\uninstall.exe, not $EXEPATH: NSIS runs uninstallers from
  ; a copy in $TEMP - with every switch this run was given, so /S stays quiet and /PURGEDATA
  ; still purges.
  ${If} $MultiUser.InstallMode == "AllUsers"
  ${AndIf} $MultiUser.Privileges != "Admin"
  ${AndIf} $MultiUser.Privileges != "Power"
    ClearErrors
    ExecShell "runas" "$INSTDIR\uninstall.exe" "$1"
    ${IfNot} ${Errors}
      Quit
    ${EndIf}
    MessageBox MB_OK|MB_ICONSTOP|MB_TOPMOST|MB_SETFOREGROUND "WinLogRotate was installed for everyone on this computer, so removing it needs administrator rights.$\n$\nRight-click uninstall.exe in the WinLogRotate folder and choose 'Run as administrator'." /SD IDOK
    Quit
  ${EndIf}

  ReadRegStr $DataDir SHCTX "${UNINST_KEY}" "DataDir"
FunctionEnd

Section "Uninstall"
  Call un.WaitForIdle
  Call un.EnsureGuiClosed

  ; Remove the run host BEFORE the executable it points at, or a task fires against a missing
  ; binary and logs a failure every day forever. --config-dir as the install passes it, so the
  ; verb records HostKind = none in the same install it was set up under rather than resolving
  ; a directory of its own; an uninstall key without a DataDir falls back to the default.
  ${If} $DataDir != ""
    nsExec::ExecToLog '"$INSTDIR\${CLI}" host use none --config-dir "$DataDir"'
  ${Else}
    nsExec::ExecToLog '"$INSTDIR\${CLI}" host use none'
  ${EndIf}
  Pop $0
  ; The machine's task and service, only from the install that owns them. A per-user uninstall
  ; run by an administrator used to remove an all-users install's task beside it.
  ${If} $MultiUser.InstallMode == "AllUsers"
    nsExec::ExecToLog '"$SYSDIR\schtasks.exe" /delete /tn "${TASK_PATH}" /f'
    Pop $0
    nsExec::ExecToLog '"$SYSDIR\sc.exe" stop "${SERVICE_NAME}"'
    Pop $0
    nsExec::ExecToLog '"$SYSDIR\sc.exe" delete "${SERVICE_NAME}"'
    Pop $0
  ${EndIf}

  ${If} $MultiUser.InstallMode == "AllUsers"
    nsExec::ExecToLog '"$INSTDIR\${CLI}" host path-remove --machine'
  ${Else}
    nsExec::ExecToLog '"$INSTDIR\${CLI}" host path-remove'
  ${EndIf}
  Pop $0

  ; Only the install that created it. A per-user uninstall is not elevated and would fail here
  ; harmlessly - but if a per-machine install exists beside it, that install's source must stay.
  ${If} $MultiUser.InstallMode == "AllUsers"
    DeleteRegKey HKLM "${EVENTLOG_KEY}"
  ${EndIf}

  Delete "$INSTDIR\${CLI}"
  Delete "$INSTDIR\${GUI}"
  Delete "$INSTDIR\winlogrotate.ico"
  Delete "$INSTDIR\LICENSE.txt"
  Delete "$SMPROGRAMS\${APP}.lnk"
  Delete "$DESKTOP\${APP}.lnk"

  ; This install's entry, and only this one: both hives use the same key name, and a per-user
  ; copy may sit beside an all-users one.
  ReadRegStr $0 HKCU "${UNINST_KEY}" "InstallLocation"
  ${If} $0 == "$INSTDIR"
    DeleteRegKey HKCU "${UNINST_KEY}"
  ${EndIf}
  ReadRegStr $0 HKLM "${UNINST_KEY}" "InstallLocation"
  ${If} $0 == "$INSTDIR"
    DeleteRegKey HKLM "${UNINST_KEY}"
  ${EndIf}

  ; Update downloads the in-app updater left behind (UpdateEngine.DownloadDirectoryPrefix).
  FindFirst $0 $1 "$TEMP\WinLogRotate-update-*"
  ${DoWhile} $1 != ""
    RMDir /r "$TEMP\$1"
    FindNext $0 $1
  ${Loop}
  FindClose $0

  ; Configuration, state and the journal are the customer's data, not ours.
  ;
  ; /SD IDNO so an unattended uninstall KEEPS them - the safe default, and the one a
  ; deployment script upgrading in place depends on. /PURGEDATA is the explicit opt-out.
  ClearErrors
  ${un.GetParameters} $0
  ${un.GetOptions} $0 "/PURGEDATA" $1
  ${IfNot} ${Errors}
    StrCpy $PurgeData 1
    RMDir /r "$DataDir"
    Goto data_done
  ${EndIf}

  MessageBox MB_YESNO|MB_ICONQUESTION \
    "Remove the configuration, rotation history and job files in$\n$DataDir?$\n$\n\
Choose No to keep them for a future install." \
    /SD IDNO IDNO data_done
  StrCpy $PurgeData 1
  RMDir /r "$DataDir"

  data_done:

  ; The secret store's per-install entropy, under the same opt-in as the data it protects.
  ; MachineEntropy's own remarks say the installer "must never remove the key on upgrade, only
  ; under /PURGEDATA" - the first half was implemented and the second was not, so a purge deleted
  ; secrets.dat and left the 32 bytes that protected it on the machine for ever.
  ;
  ; Only for a machine-wide install: a per-user one never wrote it.
  ${If} $PurgeData == 1
  ${AndIf} $MultiUser.InstallMode == "AllUsers"
    DeleteRegKey HKLM "SOFTWARE\WinLogRotate"
  ${EndIf}

  Delete "$INSTDIR\uninstall.exe"
  RMDir "$INSTDIR"

  ; NSIS runs uninstallers from a %TEMP% copy, so the folder may still be held. Sweep it up
  ; from a detached shell a moment later.
  IfFileExists "$INSTDIR\uninstall.exe" 0 +2
    Exec '"$SYSDIR\cmd.exe" /c ping 127.0.0.1 -n 3 >nul & del /f /q "$INSTDIR\uninstall.exe" & rmdir "$INSTDIR"'
SectionEnd
