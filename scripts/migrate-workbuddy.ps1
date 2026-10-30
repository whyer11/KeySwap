#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $CandidatePath,
    [string] $KeySwapPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\KeySwap.exe'),
    [string] $LegacyPath = (Join-Path $env:USERPROFILE 'Applications\WorkBuddyAltTab\WorkBuddyAltTabHost.exe'),
    [string] $IntegrationTestPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$currentSession = (Get-Process -Id $PID).SessionId
$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$preferencesKey = 'Software\KeySwap'
$runKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$legacyTaskName = 'WorkBuddyAltTabHost'
$backupDirectory = $null
$targetTouched = $false
$startupTouched = $false
$preferencesTouched = $false
$mergedRecord = $null
$integrationRecord = $null
$originalProcesses = @()

function Assert-NoReparsePath {
    param([string] $Path)
    $cursor = [IO.Path]::GetFullPath($Path)
    if ($cursor -notmatch '^[A-Za-z]:\\') { throw "A local absolute path is required: $Path" }
    while (-not [string]::IsNullOrEmpty($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a reparse-point path: $cursor"
            }
        }
        $parent = [IO.Directory]::GetParent($cursor)
        $cursor = if ($null -eq $parent) { $null } else { $parent.FullName }
    }
}

function Resolve-ExecutablePath {
    param([string] $Path, [string] $RequiredName, [switch] $MustExist)
    $resolved = [IO.Path]::GetFullPath($Path)
    Assert-NoReparsePath $resolved
    if ($RequiredName -and [IO.Path]::GetFileName($resolved) -ine $RequiredName) {
        throw "Expected executable name $RequiredName, received: $resolved"
    }
    if ([IO.Path]::GetExtension($resolved) -ine '.exe') { throw "Not an executable path: $resolved" }
    if ($MustExist -and -not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Executable is missing: $resolved"
    }
    if (Test-Path -LiteralPath $resolved -PathType Container) { throw "Executable path is a directory: $resolved" }
    return $resolved
}

function Get-RegistryValuesSnapshot {
    param([string] $SubKey)
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($SubKey, $false)
    try {
        $values = @()
        if ($null -ne $key) {
            foreach ($name in $key.GetValueNames()) {
                $values += [pscustomobject]@{
                    Name = $name
                    Kind = $key.GetValueKind($name).ToString()
                    Value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                }
            }
        }
        return [pscustomobject]@{ Present = ($null -ne $key); Values = $values }
    } finally { if ($null -ne $key) { $key.Dispose() } }
}

function Restore-Preferences {
    param($Snapshot)
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($preferencesKey, $true)
    if ($null -eq $key -and $Snapshot.Present) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($preferencesKey)
    }
    if ($null -eq $key) { return }
    try {
        $originalNames = @($Snapshot.Values | ForEach-Object { $_.Name })
        foreach ($name in $key.GetValueNames()) {
            if ($name -notin $originalNames) { $key.DeleteValue($name, $false) }
        }
        foreach ($value in $Snapshot.Values) {
            $key.SetValue($value.Name, $value.Value, [Microsoft.Win32.RegistryValueKind]$value.Kind)
        }
        $removeEmptyKey = -not $Snapshot.Present -and $key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0
    } finally { $key.Dispose() }
    if ($removeEmptyKey) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($preferencesKey, $false) }
}

function Restore-LegacyRun {
    param($Snapshot)
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKey, $true)
    if ($Snapshot.Present -and $null -eq $key) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($runKey)
    }
    if ($null -eq $key) { return }
    try {
        if ($Snapshot.Present) {
            $key.SetValue($legacyTaskName, $Snapshot.Value, [Microsoft.Win32.RegistryValueKind]$Snapshot.Kind)
        } else { $key.DeleteValue($legacyTaskName, $false) }
    } finally { $key.Dispose() }
}

function Test-DirectLaunch {
    param($Value, [string] $Kind, [string] $ExpectedPath)
    if ($Kind -notin @('String', 'ExpandString') -or $Value -isnot [string]) { return $false }
    $command = $Value.Trim()
    if ($Kind -eq 'ExpandString') { $command = [Environment]::ExpandEnvironmentVariables($command) }
    if ($command.Length -ge 2 -and $command[0] -eq '"' -and $command[$command.Length - 1] -eq '"') {
        $command = $command.Substring(1, $command.Length - 2)
    }
    # Arguments and shell wrappers are deliberately not interpreted or removed.
    return $command -ieq $ExpectedPath
}

function Get-TaskSnapshot {
    param([string] $Name, [string] $ExpectedPath)
    $matches = @(Get-ScheduledTask -TaskPath '\' -ErrorAction Stop | Where-Object { $_.TaskName -eq $Name })
    if ($matches.Count -eq 0) { return [pscustomobject]@{ Name = $Name; Present = $false; Enabled = $false; Xml = $null } }
    if ($matches.Count -ne 1) { throw "Ambiguous scheduled task: $Name" }
    $task = $matches[0]
    $actions = @($task.Actions)
    if ($actions.Count -ne 1 -or $actions[0].Execute.Trim('"') -ine $ExpectedPath) {
        throw "Scheduled task $Name does not exclusively launch the expected executable: $ExpectedPath"
    }
    $principal = $task.Principal.UserId
    if ($principal -notmatch '^S-1-') {
        $principal = ([Security.Principal.NTAccount]::new($principal)).Translate([Security.Principal.SecurityIdentifier]).Value
    }
    if ($principal -ne $currentSid) { throw "Scheduled task $Name belongs to another user." }
    return [pscustomobject]@{
        Name = $Name; Present = $true; Enabled = [bool]$task.Settings.Enabled
        Xml = (Export-ScheduledTask -TaskName $Name -TaskPath '\' -ErrorAction Stop)
    }
}

function Restore-TaskSnapshot {
    param($Snapshot, [string] $ExpectedPath)
    if ($Snapshot.Present) {
        Register-ScheduledTask -TaskName $Snapshot.Name -TaskPath '\' -Xml $Snapshot.Xml -Force -ErrorAction Stop | Out-Null
        if ($Snapshot.Enabled) { Enable-ScheduledTask -TaskName $Snapshot.Name -TaskPath '\' -ErrorAction Stop | Out-Null }
        else { Disable-ScheduledTask -TaskName $Snapshot.Name -TaskPath '\' -ErrorAction Stop | Out-Null }
    } else {
        # Validate ownership before removing a task created by the merged GUI.
        $current = Get-TaskSnapshot $Snapshot.Name $ExpectedPath
        if ($current.Present) {
            Unregister-ScheduledTask -TaskName $Snapshot.Name -TaskPath '\' -Confirm:$false -ErrorAction Stop
        }
    }
}

function New-ProcessRecord {
    param([Diagnostics.Process] $Process)
    # Keep the Start-Process object/handle solely for waits and ExitCode. Its
    # cached identity fields may not be populated immediately after creation.
    $processId = $Process.Id
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $actualPath = $null
    $actualSession = $null
    $actualStartTicks = $null
    $readError = $null
    do {
        if ($Process.HasExited) { throw "PID $processId exited before its identity could be captured." }
        $fresh = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($null -eq $fresh) { throw "PID $processId disappeared before its identity could be captured." }
        try {
            $actualPath = $fresh.Path
            $actualSession = $fresh.SessionId
            $actualStartTicks = $fresh.StartTime.ToUniversalTime().Ticks
            if (-not [string]::IsNullOrWhiteSpace($actualPath) -and $actualSession -ge 0 -and $actualStartTicks -gt 0) {
                if ($Process.HasExited -or $fresh.HasExited) { throw "PID $processId exited during identity capture." }
                return [pscustomobject]@{
                    Id = $processId; Path = $actualPath; SessionId = $actualSession
                    StartTicks = $actualStartTicks
                }
            }
        } catch { $readError = $_.Exception.Message }
        Start-Sleep -Milliseconds 25
    } while ($watch.ElapsedMilliseconds -lt 1000)
    throw "Could not capture a known process identity for PID $processId. Actual Path=<$actualPath>, SessionId=<$actualSession>, StartTicks=<$actualStartTicks>; detail=<$readError>."
}

function Get-ValidatedProcess {
    param($Record)
    $process = Get-Process -Id $Record.Id -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }
    try {
        if ($process.HasExited) { return $null }
        $actual = New-ProcessRecord $process
        $sameIdentity = $actual.Path -ieq $Record.Path -and $actual.SessionId -eq $Record.SessionId -and
            $actual.SessionId -eq $currentSession -and $actual.StartTicks -eq $Record.StartTicks
        # Graceful shutdown can finish between lookup and the identity reads.
        if ($process.HasExited) { return $null }
        if (-not $sameIdentity) {
            throw "Process identity changed; refusing to act on PID $($Record.Id). Recorded Path=<$($Record.Path)>, SessionId=<$($Record.SessionId)>, StartTicks=<$($Record.StartTicks)>; actual Path=<$($actual.Path)>, SessionId=<$($actual.SessionId)>, StartTicks=<$($actual.StartTicks)>; migration SessionId=<$currentSession>."
        }
        return $process
    } catch {
        $identityError = $_
        # A missing PID confirms shutdown. A still-live PID (including a reused
        # one) must retain the failure rather than bypass the identity checks.
        $current = Get-Process -Id $Record.Id -ErrorAction SilentlyContinue
        if ($null -eq $current) { return $null }
        try { if ($current.HasExited) { return $null } } catch { }
        throw $identityError
    }
}

function Assert-KeysReleased {
    if ($null -eq ('KeySwapMigration.KeyState' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
namespace KeySwapMigration {
    public static class KeyState {
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    }
}
'@
    }
    $keys = @(0x09, 0x11, 0x12, 0x5B, 0x5C, 0x7B, 0xA2, 0xA3, 0xA4, 0xA5)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $releasedSince = -1L
    do {
        $held = @($keys | Where-Object { ([KeySwapMigration.KeyState]::GetAsyncKeyState($_) -band 0x8000) -ne 0 })
        if ($held.Count -eq 0) {
            if ($releasedSince -lt 0) { $releasedSince = $watch.ElapsedMilliseconds }
            if ($watch.ElapsedMilliseconds - $releasedSince -ge 200) { return }
        } else { $releasedSince = -1L }
        Start-Sleep -Milliseconds 25
    } while ($watch.ElapsedMilliseconds -lt 10000)
    throw 'Alt, Ctrl, Win, Tab or F12 is still held. Migration aborted without injecting any key releases.'
}

function Stop-ValidatedForce {
    param($Record)
    if ($null -eq (Get-ValidatedProcess $Record)) { return }
    Assert-KeysReleased
    $process = Get-ValidatedProcess $Record
    if ($null -ne $process) {
        Stop-Process -InputObject $process -Force -ErrorAction Stop
        if (-not $process.WaitForExit(5000)) { throw "PID $($Record.Id) did not exit." }
    }
}

function Invoke-QuitCommand {
    param([string] $Executable)
    $command = Start-Process -FilePath $Executable -ArgumentList '--quit' -WorkingDirectory (Split-Path $Executable -Parent) -WindowStyle Hidden -PassThru
    $null = $command.Handle
    if (-not $command.WaitForExit(3000)) {
        # Successful --quit helpers often exit before their process properties
        # can be read. Capture identity only for a helper that timed out alive.
        $commandRecord = New-ProcessRecord $command
        Stop-ValidatedForce $commandRecord
        throw "Quit command timed out: $Executable"
    }
    $commandExitCode = $command.ExitCode
    if ($null -eq $commandExitCode) { throw "Quit command exit code is unavailable after completion: $Executable" }
    if ($commandExitCode -ne 0) { throw "Quit command failed ($commandExitCode): $Executable" }
}

function Stop-LegacyHost {
    param([object[]] $Records)
    $running = @($Records | Where-Object { $null -ne (Get-ValidatedProcess $_) })
    if ($running.Count -eq 0) { return }
    Invoke-QuitCommand $legacyExecutable
    foreach ($record in $running) {
        $process = Get-ValidatedProcess $record
        if ($null -ne $process -and -not $process.WaitForExit(5000)) {
            throw "Legacy Host PID $($record.Id) did not exit gracefully."
        }
    }
}

function Test-MergedReady {
    param($Record)
    $process = Get-ValidatedProcess $Record
    if ($null -eq $process) { throw 'Merged KeySwap exited during startup.' }
    $current = @(Get-Process -Name 'KeySwap' -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $currentSession })
    if ($current.Count -ne 1 -or $current[0].Id -ne $Record.Id -or $current[0].Path -ine $targetExecutable) { return $false }
    if (@(Get-Process -Name 'WorkBuddyAltTabHost' -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $currentSession }).Count -ne 0) { return $false }
    $endpoints = @(Get-NetUDPEndpoint -LocalPort 57420 -ErrorAction SilentlyContinue)
    if ($endpoints.Count -eq 0 -or @($endpoints | Where-Object { $_.OwningProcess -ne $Record.Id }).Count -ne 0) { return $false }
    $nonce = [guid]::NewGuid().ToString('N')
    $expected = "WB_STATUS_V2 nonce=$nonce host=1.1.0 protocol=2 core=ready rules=alt_tab,windows_key"
    $client = [Net.Sockets.UdpClient]::new([Net.Sockets.AddressFamily]::InterNetwork)
    try {
        $client.Client.ReceiveTimeout = 500
        $request = [Text.Encoding]::ASCII.GetBytes("WB_STATUS_V2 nonce=$nonce")
        $null = $client.Send($request, $request.Length, '127.0.0.1', 57420)
        $sender = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
        $response = $client.Receive([ref]$sender)
        return $sender.Address.Equals([Net.IPAddress]::Loopback) -and $sender.Port -eq 57420 -and
            [Text.Encoding]::ASCII.GetString($response) -ceq $expected
    } catch [Net.Sockets.SocketException] { return $false }
    finally { $client.Close() }
}

# All preflight checks and snapshots happen before stopping either application.
$candidateExecutable = Resolve-ExecutablePath $CandidatePath '' -MustExist
$targetExecutable = Resolve-ExecutablePath $KeySwapPath 'KeySwap.exe'
$legacyExecutable = Resolve-ExecutablePath $LegacyPath 'WorkBuddyAltTabHost.exe'
if ($candidateExecutable -ieq $targetExecutable -or $candidateExecutable -ieq $legacyExecutable) {
    throw 'Stage the candidate in a separate file before migration.'
}
$integrationExecutable = if ($IntegrationTestPath) { Resolve-ExecutablePath $IntegrationTestPath '' -MustExist } else { $null }
if ($integrationExecutable -and $integrationExecutable -in @($candidateExecutable, $targetExecutable, $legacyExecutable)) {
    throw 'The integration test must be a separate executable.'
}
$null = Get-Command Get-NetUDPEndpoint -ErrorAction Stop
$candidateHash = (Get-FileHash -LiteralPath $candidateExecutable -Algorithm SHA256).Hash
$targetPresent = Test-Path -LiteralPath $targetExecutable -PathType Leaf
$targetHash = if ($targetPresent) { (Get-FileHash -LiteralPath $targetExecutable -Algorithm SHA256).Hash } else { $null }
$taskSnapshots = @((Get-TaskSnapshot 'KeySwap' $targetExecutable), (Get-TaskSnapshot $legacyTaskName $legacyExecutable))
if ($taskSnapshots[1].Enabled -and -not $taskSnapshots[0].Enabled) {
    throw 'WorkBuddy startup is enabled but KeySwap startup is not. Configure the single KeySwap startup task before migrating.'
}
$preferencesSnapshot = Get-RegistryValuesSnapshot $preferencesKey
$allRunValues = Get-RegistryValuesSnapshot $runKey
$legacyRunValues = @($allRunValues.Values | Where-Object { $_.Name -eq $legacyTaskName })
$legacyRunSnapshot = if ($legacyRunValues.Count -eq 0) {
    [pscustomobject]@{ Present = $false; Kind = $null; Value = $null }
} else {
    [pscustomobject]@{ Present = $true; Kind = $legacyRunValues[0].Kind; Value = $legacyRunValues[0].Value }
}
if ($legacyRunSnapshot.Present -and -not (Test-DirectLaunch $legacyRunSnapshot.Value $legacyRunSnapshot.Kind $legacyExecutable)) {
    throw 'The WorkBuddy Run value is not a direct launch of the expected installation; refusing to change it.'
}
foreach ($name in @('KeySwap', 'WorkBuddyAltTabHost')) {
    $expectedPath = if ($name -eq 'KeySwap') { $targetExecutable } else { $legacyExecutable }
    foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
        if ($process.SessionId -eq $currentSession) {
            if ($process.Path -ine $expectedPath) { throw "Another $name installation is running in this session." }
            $originalProcesses += New-ProcessRecord $process
        } elseif ($process.Path -ieq $expectedPath) {
            throw "The $name installation is running in another Windows session."
        }
    }
}
$backupRoot = [IO.Path]::GetFullPath((Join-Path $env:USERPROFILE 'Applications\KeySwap\backups'))
Assert-NoReparsePath $backupRoot
$backupDirectory = Join-Path $backupRoot ((Get-Date -Format 'yyyyMMdd-HHmmssfff') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
if (-not $backupDirectory.StartsWith($backupRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Backup path escaped its root.' }
New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
if ($targetPresent) {
    Copy-Item -LiteralPath $targetExecutable -Destination (Join-Path $backupDirectory 'KeySwap.exe')
    if ((Get-FileHash -LiteralPath (Join-Path $backupDirectory 'KeySwap.exe') -Algorithm SHA256).Hash -ne $targetHash) { throw 'Target backup checksum mismatch.' }
}
foreach ($snapshot in $taskSnapshots) {
    if ($snapshot.Present) { $snapshot.Xml | Set-Content -LiteralPath (Join-Path $backupDirectory ($snapshot.Name + '.task.xml')) -Encoding Unicode }
}
[pscustomobject]@{
    Created = [DateTime]::UtcNow; UserSid = $currentSid; SessionId = $currentSession
    CandidatePath = $candidateExecutable; CandidateHash = $candidateHash
    KeySwapPath = $targetExecutable; OriginalHash = $targetHash; OriginalPresent = $targetPresent
    LegacyPath = $legacyExecutable; Tasks = $taskSnapshots; Preferences = $preferencesSnapshot
    LegacyRun = $legacyRunSnapshot; Processes = $originalProcesses
} | Export-Clixml -LiteralPath (Join-Path $backupDirectory 'snapshot.clixml') -Depth 12

try {
    $startupTouched = $true
    if ($taskSnapshots[1].Present) {
        $null = Get-TaskSnapshot $legacyTaskName $legacyExecutable
        Disable-ScheduledTask -TaskName $legacyTaskName -TaskPath '\' -ErrorAction Stop | Out-Null
    }
    if ($legacyRunSnapshot.Present) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKey, $true)
        try {
            $kind = $key.GetValueKind($legacyTaskName).ToString()
            $value = $key.GetValue($legacyTaskName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if (-not (Test-DirectLaunch $value $kind $legacyExecutable)) { throw 'Legacy Run value changed after preflight.' }
            $key.DeleteValue($legacyTaskName, $false)
        } finally { if ($null -ne $key) { $key.Dispose() } }
    }
    Stop-LegacyHost @($originalProcesses | Where-Object { $_.Path -ieq $legacyExecutable })
    foreach ($record in @($originalProcesses | Where-Object { $_.Path -ieq $targetExecutable })) {
        # The old 1.0.5 build has no quit command. Never force it down with held keys.
        Stop-ValidatedForce $record
    }
    if ($integrationExecutable) {
        # This opt-in desktop probe needs visible, Alt+Tab-eligible test forms.
        # SW_HIDE on startup can hide its first form and invalidate the probe.
        $test = Start-Process -FilePath $integrationExecutable -ArgumentList '--desktop-probe' -WindowStyle Normal -PassThru `
            -RedirectStandardOutput (Join-Path $backupDirectory 'integration.stdout.txt') `
            -RedirectStandardError (Join-Path $backupDirectory 'integration.stderr.txt')
        $null = $test.Handle
        $integrationRecord = New-ProcessRecord $test
        if (-not $test.WaitForExit(45000)) {
            Stop-ValidatedForce $integrationRecord
            throw 'Desktop integration test exceeded 45 seconds.'
        }
        $testExitCode = $test.ExitCode
        if ($null -eq $testExitCode) { throw 'Desktop integration test exit code is unavailable after completion. See backup logs.' }
        if ($testExitCode -ne 0) { throw "Desktop integration test failed: $testExitCode. See backup logs." }
        $integrationRecord = $null
        Assert-KeysReleased
    }
    if ((Get-FileHash -LiteralPath $candidateExecutable -Algorithm SHA256).Hash -ne $candidateHash) { throw 'Staged candidate changed after preflight.' }
    New-Item -ItemType Directory -Path (Split-Path $targetExecutable -Parent) -Force | Out-Null
    $targetTouched = $true
    Copy-Item -LiteralPath $candidateExecutable -Destination $targetExecutable -Force
    if ((Get-FileHash -LiteralPath $targetExecutable -Algorithm SHA256).Hash -ne $candidateHash) { throw 'Installed candidate checksum mismatch.' }
    $preferencesTouched = $true
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($preferencesKey)
    try { $key.SetValue('BridgeEnabled', 1, [Microsoft.Win32.RegistryValueKind]::DWord) }
    finally { $key.Dispose() }
    $process = Start-Process -FilePath $targetExecutable -ArgumentList @('--tray', '--logkeys') -WorkingDirectory (Split-Path $targetExecutable -Parent) -WindowStyle Hidden -PassThru
    $null = $process.Handle
    $mergedRecord = New-ProcessRecord $process
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $ready = $false
    do {
        if (Test-MergedReady $mergedRecord) { $ready = $true; break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $ready) { throw 'Merged KeySwap did not satisfy process, UDP owner and exact V2 readiness checks.' }
    if ($taskSnapshots[1].Present -and (Get-TaskSnapshot $legacyTaskName $legacyExecutable).Enabled) { throw 'Legacy startup task unexpectedly became enabled.' }
    $successReport = [pscustomobject]@{
        Success = $true; Executable = $targetExecutable; Pid = $mergedRecord.Id
        Sha256 = $candidateHash; HostVersion = '1.1.0'; Protocol = 2; UdpPort = 57420
        LegacyTask = $(if ($taskSnapshots[1].Present) { 'Disabled' } else { 'Absent' })
        Backup = $backupDirectory; IntegrationTestRan = [bool]$integrationExecutable
    } | ConvertTo-Json -Depth 4
    $successReport | Set-Content -LiteralPath (Join-Path $backupDirectory 'migration-result.json') -Encoding UTF8
    Write-Output $successReport
} catch {
    $migrationError = $_
    $rollbackErrors = [Collections.Generic.List[string]]::new()
    if ($null -ne $integrationRecord) {
        try { Stop-ValidatedForce $integrationRecord } catch { $rollbackErrors.Add("stop integration test: $_") }
    }
    if ($null -ne $mergedRecord) {
        try {
            if ($null -ne (Get-ValidatedProcess $mergedRecord)) {
                Invoke-QuitCommand $targetExecutable
                $process = Get-ValidatedProcess $mergedRecord
                if ($null -ne $process -and -not $process.WaitForExit(5000)) { Stop-ValidatedForce $mergedRecord }
            }
        } catch { $rollbackErrors.Add("stop merged KeySwap: $_") }
    }
    if ($targetTouched) {
        try {
            if ($targetPresent) {
                Copy-Item -LiteralPath (Join-Path $backupDirectory 'KeySwap.exe') -Destination $targetExecutable -Force
                if ((Get-FileHash -LiteralPath $targetExecutable -Algorithm SHA256).Hash -ne $targetHash) { throw 'Restored binary checksum mismatch.' }
            } elseif (Test-Path -LiteralPath $targetExecutable -PathType Leaf) {
                Remove-Item -LiteralPath $targetExecutable -Force
            }
        } catch { $rollbackErrors.Add("restore KeySwap binary: $_") }
    }
    if ($preferencesTouched) {
        try { Restore-Preferences $preferencesSnapshot } catch { $rollbackErrors.Add("restore preferences: $_") }
    }
    if ($startupTouched) {
        try { Restore-TaskSnapshot $taskSnapshots[0] $targetExecutable } catch { $rollbackErrors.Add("restore KeySwap task: $_") }
        try { Restore-TaskSnapshot $taskSnapshots[1] $legacyExecutable } catch { $rollbackErrors.Add("restore WorkBuddy task: $_") }
        try { Restore-LegacyRun $legacyRunSnapshot } catch { $rollbackErrors.Add("restore legacy Run value: $_") }
    }
    foreach ($path in @($originalProcesses | Select-Object -ExpandProperty Path -Unique)) {
        try {
            $stillRunning = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($path)) -ErrorAction SilentlyContinue |
                Where-Object { $_.Path -ieq $path -and $_.SessionId -eq $currentSession })
            if ($stillRunning.Count -eq 0) {
                if ($path -ieq $targetExecutable -and $targetPresent -and
                    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $targetHash) { throw 'Cannot restart an unrestored KeySwap binary.' }
                $startOptions = @{ FilePath = $path; WorkingDirectory = (Split-Path $path -Parent); WindowStyle = 'Hidden' }
                if ($path -ieq $targetExecutable) { $startOptions.ArgumentList = @('--tray', '--logkeys') }
                Start-Process @startOptions | Out-Null
            }
        } catch { $rollbackErrors.Add("restart previous process $path`: $_") }
    }
    $errorReport = @("Migration failed: $migrationError", "Backup: $backupDirectory") + @($rollbackErrors)
    $errorReport | Set-Content -LiteralPath (Join-Path $backupDirectory 'migration-error.txt') -Encoding UTF8
    if ($rollbackErrors.Count -gt 0) { Write-Warning ('Rollback incomplete: ' + ($rollbackErrors -join '; ')) }
    throw "Migration failed; rollback attempted. Backup: $backupDirectory. Cause: $migrationError"
}
