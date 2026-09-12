; WinLogRotate installer.
;
; Adapted from the pawse installer, with the multi-user model inverted: this is a system tool,
; so per-machine is the default and the run host executes as SYSTEM. Everything that follows
; from that inversion is commented where it happens.
;
; Build:  makensis /WX /V2 /DVERSION=1.0.0 winlogrotate.nsi
;         makensis /WX /V2 /DVERSION=1.0.0 /DFULL_ONLY winlogrotate.nsi
;         makensis /WX /V2 /DVERSION=1.0.0 /DMINIMAL_ONLY winlogrotate.nsi

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
!define SERVICE_NAME    "WinLogRotate"
!define TASK_PATH       "\WinLogRotate\Rotate"
!define EVENTLOG_KEY    "SYSTEM\CurrentControlSet\Services\EventLog\Application\WinLogRotate"

; SYSTEM and Administrators full control, Users read+execute, inheritance severed.
; Duplicated from Sddl.ConfigDirectory and asserted by the installer smoke test.
!define DATA_SDDL "O:BAG:SYD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)"

!define DOTNET_URL "https://dotnet.microsoft.com/download/dotnet/10.0"

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
; Multi-user, inverted for a system tool.
;
; The reference project defines MULTIUSER_INSTALLMODE_DEFAULT_CURRENTUSER and then overrides
; the execution level back to "user", so opening its installer never raises UAC - correct for
; a per-user tray app. Both lines are deliberately absent here.
;
; Leaving MULTIUSER_EXECUTIONLEVEL Highest in place without the override means an
; administrator gets exactly one UAC prompt, at launch, before choosing anything - which is
; the right trade for a tool whose default install writes to Program Files, hardens an ACL in
; ProgramData, registers an Event Log source and creates a SYSTEM scheduled task. A standard
; user still arrives with their own token and no prompt, so a per-user install remains
; reachable rather than being refused outright.
; ---------------------------------------------------------------------------------------
!define MULTIUSER_EXECUTIONLEVEL Highest
!define MULTIUSER_MUI
!define MULTIUSER_USE_PROGRAMFILES64
!define MULTIUSER_INSTALLMODE_INSTDIR "${APP}"
!include "MultiUser.nsh"

Var RealPrivileges
Var DataDir
Var HostChoice          ; task | none
Var PurgeData           ; 1 once the uninstaller has decided to remove the data directory
Var HostPage
Var HostTask
Var HostNone

!define MUI_ICON   "winlogrotate.ico"
!define MUI_UNICON "winlogrotate.ico"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_SMALLDESC

!insertmacro MUI_PAGE_WELCOME
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

!insertmacro MULTIUSER_PAGE_INSTALLMODE
Page custom HostPageCreate HostPageLeave
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
; The reference project force-kills a stuck tray app after a timeout, because nothing is lost
; by doing so. Here the process we might kill is halfway through a MoveFileEx on a
; multi-gigabyte log, and losing a log file is the one outcome this product exists to prevent.
; So the timeout CANCELS by default; /FORCEIDLE is the explicit opt-out.
Function WaitForIdle
  Push $R0
  Push $R1
  StrCpy $R1 0

  wait_loop:
    System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "${ROTATION_MUTEX}") i .r0'
    ${If} $0 == 0
      Goto wait_done
    ${EndIf}
    System::Call 'kernel32::CloseHandle(i r0)'

    IntOp $R1 $R1 + 1
    ${If} $R1 > 120
      ClearErrors
      ${GetParameters} $R0
      ${GetOptions} $R0 "/FORCEIDLE" $R0
      ${IfNot} ${Errors}
        Goto wait_done
      ${EndIf}

      ; /SD IDCANCEL: NSIS does not suppress message boxes under /S, so a box without a silent
      ; default hangs an unattended install forever on a dialog nobody can see. Cancelling is
      ; also the safe answer - see the note above about what we would otherwise be killing.
      MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION \
        "A log rotation has been running for two minutes.$\n$\nWaiting avoids interrupting it \
mid-file. Press OK to keep waiting, or Cancel to abort this install." \
        /SD IDCANCEL IDOK continue_waiting
      Abort "A rotation was still running; nothing was changed."

      continue_waiting:
        StrCpy $R1 0
    ${EndIf}

    Sleep 1000
    Goto wait_loop

  wait_done:
  Pop $R1
  Pop $R0
FunctionEnd

; Asks a running GUI to close, rather than killing it.
Function CloseGui
  Push $0
  System::Call 'kernel32::OpenEventW(i 0x0002, i 0, w "${GUI_QUIT_EVENT}") i .r0'
  ${If} $0 != 0
    System::Call 'kernel32::SetEvent(i r0)'
    System::Call 'kernel32::CloseHandle(i r0)'
    Sleep 2000
  ${EndIf}
  Pop $0
FunctionEnd

; Is a .NET 10 Desktop Runtime present?
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
!ifndef FULL_ONLY
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

  FindFirst $1 $2 "$0\shared\Microsoft.WindowsDesktop.App\10.*"
  FindClose $1
  ${If} $2 != ""
    StrCpy $3 "1"
  ${EndIf}

  DetailPrint "Desktop Runtime 10 under $0: $2"

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

  ClearErrors
  ${GetParameters} $0
  ${GetOptions} $0 "/NORUNTIME" $1
  ${IfNot} ${Errors}
    Goto manual
  ${EndIf}

  Call DotnetPresent
  Pop $0
  ${If} $0 == "1"
    Goto done
  ${EndIf}

  ; /SD IDYES so a scripted deployment provisions the runtime unattended rather than stalling.
  MessageBox MB_YESNO|MB_ICONQUESTION \
    "The graphical console needs the .NET 10 Desktop Runtime, which is not installed.$\n$\n\
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

  DetailPrint "Installing the .NET 10 Desktop Runtime..."
  nsExec::ExecToLog '"$1" install --id Microsoft.DotNet.DesktopRuntime.10 -e --silent \
--accept-package-agreements --accept-source-agreements'
  Pop $0

  DetailPrint "winget exited $0."

  ; Re-verify: winget reports success in cases where the runtime does not actually land, and
  ; the only answer that means anything is whether the runtime is now on disk.
  Call DotnetPresent
  Pop $0
  ${If} $0 == "1"
    DetailPrint "The .NET 10 Desktop Runtime is now installed."
    Goto done
  ${EndIf}
  DetailPrint "winget ran but the Desktop Runtime is still not present."

  manual:
    ; /SD IDNO: an unattended run must never block, and must never open a browser.
    MessageBox MB_YESNO|MB_ICONINFORMATION \
      "The .NET 10 Desktop Runtime could not be installed automatically.$\n$\n\
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

  ${NSD_Check} $HostTask
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
  Call CloseGui

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

  ; Carry a configuration forward from a previous location, without ever overwriting one that
  ; is already there.
  WriteUninstaller "$INSTDIR\uninstall.exe"

  WriteRegStr   SHCTX "${UNINST_KEY}" "DisplayName"          "${APP}"
  WriteRegStr   SHCTX "${UNINST_KEY}" "DisplayVersion"       "${VERSION}"
  WriteRegStr   SHCTX "${UNINST_KEY}" "Publisher"            "${PUBLISHER}"
  WriteRegStr   SHCTX "${UNINST_KEY}" "DisplayIcon"          "$INSTDIR\winlogrotate.ico"
  WriteRegStr   SHCTX "${UNINST_KEY}" "InstallLocation"      "$INSTDIR"
  WriteRegStr   SHCTX "${UNINST_KEY}" "DataDir"              "$DataDir"
  WriteRegStr   SHCTX "${UNINST_KEY}" "HostKind"             "$HostChoice"
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

!ifndef FULL_ONLY
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
; Init
; =======================================================================================

Function .onInit
  StrCpy $HostChoice "task"

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
  ${GetParameters} $R0

  ; Parsed by hand, after the fib. Stock MultiUser answers an unelevated /AllUsers with a
  ; message box that has no silent default, so "/S /AllUsers" would hang forever on an
  ; invisible dialog and then report success having installed nothing.
  ClearErrors
  ${GetOptions} $R0 "/CurrentUser" $R1
  ${IfNot} ${Errors}
    Call MultiUser.InstallMode.CurrentUser
  ${EndIf}
  ClearErrors
  ${GetOptions} $R0 "/AllUsers" $R1
  ${IfNot} ${Errors}
    Call MultiUser.InstallMode.AllUsers
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

  ; Match an existing install's scope rather than creating a second copy beside it.
  ReadRegStr $0 HKCU "${UNINST_KEY}" "UninstallString"
  ${If} $0 == ""
    ReadRegStr $0 HKLM "${UNINST_KEY}" "UninstallString"
    ${If} $0 != ""
      Call MultiUser.InstallMode.AllUsers
    ${EndIf}
  ${EndIf}

  ; Upgrade in place, so a custom directory is not orphaned.
  ReadRegStr $0 SHCTX "${UNINST_KEY}" "InstallLocation"
  ${If} $0 != ""
  ${AndIf} ${FileExists} "$0\${CLI}"
    StrCpy $INSTDIR $0
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

  Call ResolveDataDir

  ; A seeded configuration for unattended customer deployment: /CONFIG=\\srv\deploy\jobs
  ClearErrors
  ${GetOptions} $R0 "/CONFIG=" $R1
  ${IfNot} ${Errors}
    CreateDirectory "$DataDir\conf.d"
    CopyFiles /SILENT "$R1\*.toml" "$DataDir\conf.d"
  ${EndIf}

  Pop $R1
  Pop $R0
FunctionEnd

; =======================================================================================
; Uninstall
; =======================================================================================

Function un.onInit
  !insertmacro MULTIUSER_UNINIT

  ; Work out on our own whether this uninstaller belongs to a machine-wide install: the
  ; registered uninstall strings carry no /AllUsers, and stock MultiUser puts every token into
  ; per-user mode.
  ;
  ; NOTE, and this differs from the reference: because MULTIUSER_EXECUTIONLEVEL Highest is no
  ; longer overridden back to "user", an administrator DOES arrive here already elevated. The
  ; handoff below is therefore only needed when we are not.
  ReadRegStr $0 HKCU "${UNINST_KEY}" "InstallLocation"
  ${If} $0 != "$INSTDIR"
    ReadRegStr $0 HKLM "${UNINST_KEY}" "InstallLocation"
    ${If} $0 == "$INSTDIR"
      StrCpy $MultiUser.InstallMode "AllUsers"
      SetShellVarContext all
    ${EndIf}
  ${EndIf}

  ReadRegStr $DataDir SHCTX "${UNINST_KEY}" "DataDir"
FunctionEnd

Section "Uninstall"
  Call un.WaitForIdle
  Call un.CloseGui

  ; Remove the run host BEFORE the executable it points at, or a task fires against a missing
  ; binary and logs a failure every day forever.
  nsExec::ExecToLog '"$INSTDIR\${CLI}" host use none'
  Pop $0
  nsExec::ExecToLog '"$SYSDIR\schtasks.exe" /delete /tn "${TASK_PATH}" /f'
  Pop $0
  nsExec::ExecToLog '"$SYSDIR\sc.exe" stop "${SERVICE_NAME}"'
  Pop $0
  nsExec::ExecToLog '"$SYSDIR\sc.exe" delete "${SERVICE_NAME}"'
  Pop $0

  ${If} $MultiUser.InstallMode == "AllUsers"
    nsExec::ExecToLog '"$INSTDIR\${CLI}" host path-remove --machine'
  ${Else}
    nsExec::ExecToLog '"$INSTDIR\${CLI}" host path-remove'
  ${EndIf}
  Pop $0

  DeleteRegKey HKLM "${EVENTLOG_KEY}"

  Delete "$INSTDIR\${CLI}"
  Delete "$INSTDIR\${GUI}"
  Delete "$INSTDIR\winlogrotate.ico"
  Delete "$INSTDIR\LICENSE.txt"
  Delete "$SMPROGRAMS\${APP}.lnk"
  Delete "$DESKTOP\${APP}.lnk"

  ; Both hives: SHCTX can be wrong if the install scope was ever changed by hand.
  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKLM "${UNINST_KEY}"

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

; The uninstaller cannot call installer functions, so the two it needs are duplicated with the
; un. prefix. NSIS has no better answer for this.
Function un.WaitForIdle
  Push $R1
  StrCpy $R1 0
  un_wait_loop:
    System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "${ROTATION_MUTEX}") i .r0'
    ${If} $0 == 0
      Goto un_wait_done
    ${EndIf}
    System::Call 'kernel32::CloseHandle(i r0)'
    IntOp $R1 $R1 + 1
    ${If} $R1 > 120
      Goto un_wait_done
    ${EndIf}
    Sleep 1000
    Goto un_wait_loop
  un_wait_done:
  Pop $R1
FunctionEnd

Function un.CloseGui
  Push $0
  System::Call 'kernel32::OpenEventW(i 0x0002, i 0, w "${GUI_QUIT_EVENT}") i .r0'
  ${If} $0 != 0
    System::Call 'kernel32::SetEvent(i r0)'
    System::Call 'kernel32::CloseHandle(i r0)'
    Sleep 2000
  ${EndIf}
  Pop $0
FunctionEnd
