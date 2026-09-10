# Helpers for the installer smoke test.
#
# Kept as a file rather than inlined in the workflow so it reads, lints and diffs like
# PowerShell instead of like YAML.
#
# The recurring theme here is waiting properly. An NSIS uninstaller re-launches itself from
# %TEMP% and returns immediately, schtasks reports a task as running long after it has finished,
# and the Event Log is written asynchronously - so almost every assertion needs a poll rather
# than a single check, and a naive test would be flaky in a way that trains people to re-run it.

Set-StrictMode -Version Latest

function Wait-Until {
    <#
        .SYNOPSIS
        Polls until the scriptblock returns true, or fails with a useful message.
    #>
    param(
        [Parameter(Mandatory)][scriptblock] $Condition,
        [Parameter(Mandatory)][string]      $Because,
        [int] $TimeoutSeconds = 60,
        [int] $IntervalMs = 500
    )

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds $IntervalMs
    }

    throw "Timed out after ${TimeoutSeconds}s waiting for: $Because"
}

function Get-UninstallEntry {
    <#
        .SYNOPSIS
        Reads the Add/Remove Programs entry, from whichever hive and view it landed in.

        .DESCRIPTION
        makensis produces a 32-bit installer, so its per-machine writes are redirected into
        HKLM\SOFTWARE\WOW6432Node. Checking both views means the test does not silently pass
        for the wrong reason if that ever changes.
    #>
    param([ValidateSet('machine', 'user')][string] $Scope = 'machine')

    $paths = if ($Scope -eq 'machine') {
        @(
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\WinLogRotate',
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WinLogRotate'
        )
    } else {
        @('HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WinLogRotate')
    }

    foreach ($p in $paths) {
        if (Test-Path $p) { return Get-ItemProperty $p }
    }

    return $null
}

function Wait-Removed {
    <#
        .SYNOPSIS
        Waits for an uninstall to finish.

        .DESCRIPTION
        An NSIS uninstaller copies itself to %TEMP% and re-launches, so the process we started
        returns long before anything has been removed - and it deletes its own directory through
        a detached cmd a second later. Polling is the only correct way to observe the result.
    #>
    param(
        [Parameter(Mandatory)][ValidateSet('machine', 'user')][string] $Scope,
        [Parameter(Mandatory)][string] $Directory,
        [int] $TimeoutSeconds = 90
    )

    Wait-Until -TimeoutSeconds $TimeoutSeconds -Because "the uninstall to remove its registry entry" -Condition {
        $null -eq (Get-UninstallEntry -Scope $Scope)
    }

    Wait-Until -TimeoutSeconds $TimeoutSeconds -Because "the uninstall to remove $Directory" -Condition {
        -not (Test-Path $Directory)
    }
}

function Assert-Sddl {
    <#
        .SYNOPSIS
        Asserts the configuration directory carries exactly the intended descriptor.

        .DESCRIPTION
        This is the assertion that matters most in the whole suite. The run host executes as
        SYSTEM and reads every job file, and jobs can carry hooks - so if this directory is
        writable by an ordinary user, any local user can run a command as SYSTEM. The
        descriptor is duplicated in Sddl.cs, in the .nsi, and here; a unit test binds the first
        two, and this binds them to reality.
    #>
    param([Parameter(Mandatory)][string] $Path)

    $acl = Get-Acl $Path

    if (-not $acl.AreAccessRulesProtected) {
        throw "$Path inherits its permissions. Without the protected flag a correct DACL " +
              "re-inherits ProgramData's permissive entries as soon as anyone edits them."
    }

    # Compare the DACL and owner, not the whole descriptor: the group varies by machine and is
    # vestigial on Windows anyway.
    $actual = $acl.Sddl
    foreach ($ace in @('(A;OICI;FA;;;SY)', '(A;OICI;FA;;;BA)')) {
        if ($actual -notlike "*$ace*") {
            throw "$Path is missing the required ACE ${ace}. Actual: $actual"
        }
    }

    # The specific hole this exists to close: ProgramData grants Users create rights and gives
    # CREATOR OWNER full control of what they create.
    foreach ($forbidden in @(';;;WD)', ';;;CO)', ';;;AU)')) {
        if ($actual -like "*FA$forbidden*" -or $actual -like "*GA$forbidden*") {
            throw "$Path grants full control to a non-administrator. Actual: $actual"
        }
    }

    # Checking a list of well-known SIDs is not enough, and a shipped release proved it. When a
    # directory is created under ProgramData, its inherit-only CREATOR OWNER entry materialises
    # as an explicit Full Control ACE for whoever created it - an ordinary account SID, matching
    # none of the patterns above, so the check passed while the run host refused every hook.
    #
    # We own this descriptor completely, so the right assertion is that nothing else is in it.
    $allowed = @(
        'S-1-5-18',      # LocalSystem
        'S-1-5-32-544',  # Administrators
        'S-1-5-32-545'   # Users, read and execute only, so the unelevated GUI works
    )
    foreach ($rule in (Get-Acl $Path).GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne 'Allow') { continue }
        $sid = $rule.IdentityReference.Value
        if ($allowed -notcontains $sid) {
            $who = try { $rule.IdentityReference.Translate([System.Security.Principal.NTAccount]).Value } catch { $sid }
            throw "$Path grants $($rule.FileSystemRights) to $who ($sid), which is not one of SYSTEM, Administrators or Users. Actual: $actual"
        }
    }

    Write-Host "  ACL ok: $actual"
}

function Assert-PathEntry {
    <#
        .SYNOPSIS
        Asserts a directory is on PATH exactly once, or absent, without the rest being harmed.

        .DESCRIPTION
        PATH is edited by winlogrotate.exe rather than by NSIS, because stock makensis is built
        with NSIS_MAX_STRLEN=1024 and its ReadRegStr silently truncates a longer PATH - writing
        that back destroys it for every program on the machine. These assertions are what prove
        that decision is holding.
    #>
    param(
        [Parameter(Mandatory)][ValidateSet('Machine', 'User')][string] $Scope,
        [string] $Contains,
        [string] $NotContains,
        [string] $PreservedPrefix
    )

    $path = [Environment]::GetEnvironmentVariable('PATH', $Scope)
    $parts = @($path -split ';' | Where-Object { $_ })

    if ($Contains) {
        $hits = @($parts | Where-Object { $_.TrimEnd('\') -ieq $Contains.TrimEnd('\') }).Count
        if ($hits -ne 1) {
            throw "Expected '$Contains' on the $Scope PATH exactly once, found $hits times."
        }
    }

    if ($NotContains) {
        $hits = @($parts | Where-Object { $_.TrimEnd('\') -ieq $NotContains.TrimEnd('\') }).Count
        if ($hits -ne 0) {
            throw "'$NotContains' is still on the $Scope PATH ($hits times)."
        }
    }

    if ($PreservedPrefix) {
        # The catastrophic failure this guards against is not "our entry is missing", it is
        # "everything else got truncated away".
        #
        # TrimEnd on the whole value, not on each entry: the baseline is usually read back with
        # Get-Content -Raw, which keeps the trailing newline and so contaminates the LAST entry
        # only. A PATH entry may legitimately end in a space - the runners' own PATH does - so
        # the entries themselves are compared verbatim.
        $baseline = $PreservedPrefix.TrimEnd("`r", "`n")

        foreach ($entry in @($baseline -split ';' | Where-Object { $_ })) {
            if ($parts -notcontains $entry) {
                throw "The $Scope PATH lost a pre-existing entry: '$entry'. " +
                      "That is the NSIS_MAX_STRLEN truncation bug."
            }
        }
    }
}

function Wait-TaskResult {
    <#
        .SYNOPSIS
        Runs the scheduled task and waits for a real result.

        .DESCRIPTION
        LastTaskResult is 267009 while a task is running and retains the previous value until it
        finishes, so reading it immediately after Start-ScheduledTask reads the wrong thing.
    #>
    param(
        [Parameter(Mandatory)][string] $TaskPath,
        [Parameter(Mandatory)][string] $TaskName,
        [int] $TimeoutSeconds = 180
    )

    Start-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName

    Wait-Until -TimeoutSeconds $TimeoutSeconds -Because "the scheduled task to finish" -Condition {
        (Get-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName).State -ne 'Running'
    }

    $info = Get-ScheduledTaskInfo -TaskPath $TaskPath -TaskName $TaskName
    Write-Host "  task LastTaskResult = 0x$('{0:X}' -f $info.LastTaskResult)"
    return $info.LastTaskResult
}

function Measure-MutexHeldUpgrade {
    <#
        .SYNOPSIS
        Holds the rotation mutex, runs an upgrade, and reports how long the upgrade took.

        .DESCRIPTION
        This is the assertion the whole design of the upgrade path exists for. The installer is
        supposed to wait for a rotation in progress rather than overwriting the executable
        underneath it - and the only way to prove it actually waits is to time it. Creating the
        mutex here rather than driving a real rotation also PINS THE NAME: a rename in either
        Names.cs or the .nsi turns this red instead of silently disabling the wait.
    #>
    param(
        [Parameter(Mandatory)][string] $MutexName,
        [Parameter(Mandatory)][string] $Setup,
        [Parameter(Mandatory)][string[]] $Arguments,
        [int] $HoldSeconds = 20
    )

    $job = Start-Job -ScriptBlock {
        param($name, $seconds)
        $created = $false
        $m = New-Object System.Threading.Mutex($true, $name, [ref] $created)
        Start-Sleep -Seconds $seconds
        if ($created) { $m.ReleaseMutex() }
        $m.Dispose()
    } -ArgumentList $MutexName, $HoldSeconds

    # Let the job actually take the mutex before starting the installer.
    Start-Sleep -Seconds 3

    $elapsed = Measure-Command {
        Start-Process -FilePath $Setup -ArgumentList $Arguments -Wait
    }

    Receive-Job $job -Wait -AutoRemoveJob | Out-Null
    return $elapsed.TotalSeconds
}

function Assert-TaskHardening {
    <#
        .SYNOPSIS
        Asserts the settings Task Scheduler gets wrong by default.

        .DESCRIPTION
        Every one of these fails silently. StartWhenAvailable off means a machine that was
        switched off skips the day and logs Event ID 153 that nobody reads; both battery
        settings default to true and stop the task on any laptop or any VM whose host reports a
        battery; ExecutionTimeLimit defaults to PT72H.
    #>
    param(
        [Parameter(Mandatory)][string] $TaskPath,
        [Parameter(Mandatory)][string] $TaskName,
        [switch] $ExpectSystem
    )

    $task = Get-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName
    $s = $task.Settings

    if (-not $s.StartWhenAvailable) { throw 'StartWhenAvailable is off: a missed run would never be caught up.' }
    if ($s.DisallowStartIfOnBatteries) { throw 'DisallowStartIfOnBatteries is on: the task would not run on a laptop or a VM reporting a battery.' }
    if ($s.StopIfGoingOnBatteries) { throw 'StopIfGoingOnBatteries is on: the task would be killed mid-rotation.' }
    if ($s.ExecutionTimeLimit -ne 'PT1H') { throw "ExecutionTimeLimit is '$($s.ExecutionTimeLimit)', expected PT1H (the default is PT72H)." }
    if ($s.MultipleInstances -ne 'IgnoreNew') { throw "MultipleInstances is '$($s.MultipleInstances)', expected IgnoreNew." }

    # The deadline the task passes to the run must be the deadline the task actually enforces.
    # Both come from one property, and a unit test pins that they agree in the generated XML -
    # this is the same assertion against a task Windows really registered, which is the only
    # place a schtasks quirk could have mangled the argument string.
    $arguments = @($task.Actions)[0].Arguments
    if ($arguments -notmatch '--run-deadline\s+(\S+)') {
        throw "The registered task passes no --run-deadline, so the notification phase cannot stop short of being killed. Arguments: $arguments"
    }
    $deadline = [TimeSpan]::Parse($Matches[1])
    if ($deadline -ne [System.Xml.XmlConvert]::ToTimeSpan($s.ExecutionTimeLimit)) {
        throw "--run-deadline is $deadline but ExecutionTimeLimit is $($s.ExecutionTimeLimit); they must denote the same span."
    }

    if ($ExpectSystem) {
        $user = $task.Principal.UserId
        if ($user -notmatch 'SYSTEM|S-1-5-18') { throw "The task runs as '$user', expected SYSTEM." }
        if ($task.Principal.RunLevel -ne 'Highest') { throw "RunLevel is '$($task.Principal.RunLevel)', expected Highest." }
    }

    Write-Host "  task hardening ok (as $($task.Principal.UserId))"
}

function Invoke-WithHeldHandle {
    <#
        .SYNOPSIS
        Holds a real handle on a file, with a chosen share mode, while a scriptblock runs.

        .DESCRIPTION
        The share mode is the only variable, and it is the only thing CreateFile's sharing check
        consults - so it is what decides which strategies LockProbe finds available. FileShare
        ReadWrite WITHOUT Delete is what MSVCRT's fopen("a"), .NET's FileMode.Append and IIS all
        produce, and it is exactly the condition that makes rename impossible and copytruncate
        necessary.

        A separate process, because the rotation happens inside winlogrotate.exe and a handle held
        in this same PowerShell would prove nothing about cross-process sharing.

        Two named events rather than a sleep. A sleep long enough to be safe is the slowest step in
        the job; a sleep short enough to be quick is a flake that looks exactly like the product
        working - the holder dies, the file is free, the verdict comes back Rename, and everything
        passes for the wrong reason.
    #>
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Share,
        [Parameter(Mandatory)][scriptblock] $While,
        [int] $TimeoutSeconds = 120
    )

    # Unique per call. EventWaitHandle's constructor OPENS an existing name rather than creating
    # a fresh object, and a ManualReset event left signalled by the previous call stays signalled -
    # so the second holder set ready, fell straight through its already-signalled release, and
    # disposed its handle while the probe was still measuring. Everything after the first case
    # would then be measured against an unlocked file, which looks exactly like the product
    # working. Reset as well as renaming, because a crashed earlier run can leave a name behind.
    if ($null -eq $script:HeldHandleSeq) { $script:HeldHandleSeq = 0 }
    $script:HeldHandleSeq++
    $readyName   = "Global\WinLogRotate.Smoke.HolderReady.$script:HeldHandleSeq"
    $releaseName = "Global\WinLogRotate.Smoke.HolderRelease.$script:HeldHandleSeq"

    # Created here, opened there: a child that had to create them could race ahead of us.
    $ready   = New-Object System.Threading.EventWaitHandle($false, 'ManualReset', $readyName)
    $release = New-Object System.Threading.EventWaitHandle($false, 'ManualReset', $releaseName)
    $ready.Reset()   | Out-Null
    $release.Reset() | Out-Null

    $job = Start-Job -ScriptBlock {
        param($path, $share, $readyName, $releaseName, $timeout)

        $ready   = [System.Threading.EventWaitHandle]::OpenExisting($readyName)
        $release = [System.Threading.EventWaitHandle]::OpenExisting($releaseName)

        $fs = [System.IO.File]::Open(
            $path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]$share)

        try {
            $ready.Set() | Out-Null
            $release.WaitOne([int]($timeout * 1000)) | Out-Null
        } finally {
            $fs.Dispose()
        }
    } -ArgumentList $Path, $Share, $readyName, $releaseName, $TimeoutSeconds

    try {
        if (-not $ready.WaitOne(30000)) {
            Receive-Job $job | ForEach-Object { Write-Host "    holder: $_" }
            throw "The holder never opened '$Path' with share '$Share'."
        }

        if ($job.State -ne 'Running') { throw 'The holder exited before the test ran.' }

        # The job object can still say Running for a moment after the handle is gone, so ask the
        # file system instead: if we can take an exclusive handle, nothing else is holding one.
        try {
            $proof = [System.IO.File]::Open($Path, 'Open', 'Read', 'None')
            $proof.Dispose()
            throw "Nothing is actually holding '$Path' - every verdict below would be measured against a free file."
        } catch [System.IO.IOException] {
            # Expected: the holder has it.
        }

        & $While

        # If the holder died during the body, every verdict measured above was taken against an
        # unlocked file - which is the one failure this whole helper exists to rule out.
        if ($job.State -ne 'Running') {
            throw 'The holder died DURING the test; nothing measured above can be trusted.'
        }
    } finally {
        $release.Set() | Out-Null
        Receive-Job $job -Wait -AutoRemoveJob | ForEach-Object { Write-Host "    holder: $_" }
        $ready.Dispose(); $release.Dispose()
    }
}
