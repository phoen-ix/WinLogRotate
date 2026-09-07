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

    if ($ExpectSystem) {
        $user = $task.Principal.UserId
        if ($user -notmatch 'SYSTEM|S-1-5-18') { throw "The task runs as '$user', expected SYSTEM." }
        if ($task.Principal.RunLevel -ne 'Highest') { throw "RunLevel is '$($task.Principal.RunLevel)', expected Highest." }
    }

    Write-Host "  task hardening ok (as $($task.Principal.UserId))"
}
