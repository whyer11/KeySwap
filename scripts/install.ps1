#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string] $SourcePath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\KeySwap.exe'),
    [string] $InstallPath = (Join-Path $env:USERPROFILE 'Applications\KeySwap\KeySwap.exe')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
foreach ($helper in @('InstallSecurity', 'InstallRuntime')) {
    if (-not ("KeySwap.Installation.$helper" -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot "$helper.cs")
    }
}
$self = [KeySwap.Installation.InstallRuntime]::Inspect($PID)
if ($self.IntegrityRid -lt 12288 -or $self.SessionId -eq 0) {
    throw 'Run this installer in an elevated interactive PowerShell terminal with High integrity. An elevated token restricted to Low integrity is insufficient.'
}
$currentSid = $self.UserSid
$currentSession = $self.SessionId
$taskName = 'KeySwap'
$taskNamespace = 'http://schemas.microsoft.com/windows/2004/02/mit/task'
$targetChanged = $false
$taskChanged = $false
$launched = $null
$stage = $null
$backupDirectory = $null
$oldBinary = $null
$originalProcesses = @()
$stoppedOriginals = @()

function Read-KeySwapTask {
    $tasks = @(Get-ScheduledTask -TaskPath '\' -ErrorAction Stop | Where-Object { $_.TaskName -eq $taskName })
    if ($tasks.Count -eq 0) { return $null }
    if ($tasks.Count -ne 1) { throw 'Ambiguous KeySwap task.' }
    $text = Export-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction Stop
    $doc = New-Object Xml.XmlDocument
    $doc.XmlResolver = $null
    $doc.LoadXml($text)
    $ns = New-Object Xml.XmlNamespaceManager($doc.NameTable)
    $ns.AddNamespace('t', $taskNamespace)
    $actions = @($doc.SelectNodes('/t:Task/t:Actions/*', $ns))
    $principals = @($doc.SelectNodes('/t:Task/t:Principals/t:Principal', $ns))
    if ($actions.Count -ne 1 -or $actions[0].LocalName -ne 'Exec' -or $principals.Count -ne 1) {
        throw 'Existing KeySwap task must have exactly one direct Exec action and one principal.'
    }
    $principal = $principals[0].SelectSingleNode('t:UserId', $ns).InnerText
    if ($principal -notmatch '^S-1-') { $principal = ([Security.Principal.NTAccount]::new($principal)).Translate([Security.Principal.SecurityIdentifier]).Value }
    if ($principal -ne $currentSid) { throw 'Existing KeySwap task belongs to another user.' }
    $command = $actions[0].SelectSingleNode('t:Command', $ns).InnerText.Trim('"')
    [KeySwap.Installation.InstallSecurity]::ValidatePath($command, $false)
    if ([IO.Path]::GetFileName($command) -ine 'KeySwap.exe') { throw 'Existing KeySwap task does not directly launch KeySwap.exe.' }
    $arguments = $actions[0].SelectSingleNode('t:Arguments', $ns)
    if ($null -eq $arguments -or $arguments.InnerText.Trim() -ine '--tray') { throw 'Unexpected KeySwap task arguments; refusing to replace them.' }
    if ($principals[0].SelectSingleNode('t:LogonType', $ns).InnerText -ne 'InteractiveToken' -or
        $principals[0].SelectSingleNode('t:RunLevel', $ns).InnerText -ne 'HighestAvailable') {
        throw 'Existing KeySwap task must use InteractiveToken and HighestAvailable.'
    }
    return [pscustomobject]@{ Xml = $text; Document = $doc; Namespace = $ns; Path = [IO.Path]::GetFullPath($command); Enabled = [bool]$tasks[0].Settings.Enabled }
}

function New-KeySwapTaskXml([string] $Executable) {
    $escapedPath = [Security.SecurityElement]::Escape($Executable)
    return @"
<Task version="1.2" xmlns="$taskNamespace">
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>$currentSid</UserId></LogonTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>$currentSid</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>false</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable><Enabled>true</Enabled><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>
  <Actions Context="Author"><Exec><Command>$escapedPath</Command><Arguments>--tray</Arguments></Exec></Actions>
</Task>
"@
}

function Get-KeySwapProcesses {
    $result = @()
    foreach ($process in @(Get-Process -Name KeySwap -ErrorAction SilentlyContinue)) {
        if ($process.SessionId -ne $currentSession) { continue }
        try { $identity = [KeySwap.Installation.InstallRuntime]::Inspect($process.Id) }
        catch {
            if ($null -eq (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) { continue }
            throw
        }
        if ($identity.UserSid -ne $currentSid) { throw "Unexpected owner for KeySwap PID $($process.Id)." }
        $result += $identity
    }
    return $result
}

function Test-ProcessIdentity($Record) {
    $process = Get-Process -Id $Record.Id -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $false }
    try {
        $actual = [KeySwap.Installation.InstallRuntime]::Inspect($Record.Id)
        if (-not [KeySwap.Installation.InstallRuntime]::Same($actual, $Record)) { throw 'Process identity changed.' }
        return $true
    } catch {
        if ($null -eq (Get-Process -Id $Record.Id -ErrorAction SilentlyContinue)) { return $false }
        throw
    }
}

function Stop-KeySwapGracefully($Record) {
    if (-not (Test-ProcessIdentity $Record)) { return }
    $sent = [KeySwap.Installation.InstallRuntime]::RequestQuit($Record)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (Test-ProcessIdentity $Record) {
        if ($watch.ElapsedMilliseconds -ge 10000) { throw "KeySwap PID $($Record.Id) did not exit after targeted Quit ($sent windows). No process was force-killed." }
        Start-Sleep -Milliseconds 100
    }
}

function Test-BridgeReady($Record, [string] $Version) {
    if (-not (Test-ProcessIdentity $Record)) { return $false }
    $owners = @(Get-NetUDPEndpoint -LocalPort 57420 -ErrorAction SilentlyContinue)
    if ($owners.Count -eq 0 -or @($owners | Where-Object { $_.OwningProcess -ne $Record.Id }).Count -gt 0) { return $false }
    $nonce = [guid]::NewGuid().ToString('N')
    $expected = "WB_STATUS_V2 nonce=$nonce host=$Version protocol=2 core=ready rules=alt_tab,windows_key"
    $client = New-Object Net.Sockets.UdpClient([Net.Sockets.AddressFamily]::InterNetwork)
    try {
        $client.Client.ReceiveTimeout = 300
        $bytes = [Text.Encoding]::ASCII.GetBytes("WB_STATUS_V2 nonce=$nonce")
        $null = $client.Send($bytes, $bytes.Length, '127.0.0.1', 57420)
        $sender = New-Object Net.IPEndPoint([Net.IPAddress]::Any, 0)
        $response = $client.Receive([ref]$sender)
        return $sender.Address.Equals([Net.IPAddress]::Loopback) -and $sender.Port -eq 57420 -and [Text.Encoding]::ASCII.GetString($response) -ceq $expected
    } catch [Net.Sockets.SocketException] { return $false }
    finally { $client.Close() }
}

# Everything through staging and backups is preflight. No process/task changes
# occur until the destination and fresh binary have passed integrity validation.
$source = [IO.Path]::GetFullPath($SourcePath)
$target = [IO.Path]::GetFullPath($InstallPath)
[KeySwap.Installation.InstallSecurity]::ValidatePath($source, $true)
[KeySwap.Installation.InstallSecurity]::ValidatePath($target, $false)
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)).TrimEnd('\') + '\'
if ($source -ieq $target) { throw 'Source and installed executable must be different files.' }
if ($target.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Install outside the repository so the executable cannot inherit its sandbox integrity label.' }
if ([IO.Path]::GetFileName($target) -ine 'KeySwap.exe') { throw 'The installed executable must be named KeySwap.exe.' }
$destination = [KeySwap.Installation.InstallSecurity]::ValidateDirectory([IO.Path]::GetDirectoryName($target))
$targetExisted = [IO.File]::Exists($target)
if ($targetExisted -and [KeySwap.Installation.InstallSecurity]::GetFileIntegrityRid($target) -lt 8192) {
    throw 'Existing installed executable has Low integrity. Choose a clean destination; this installer never changes sandbox ACLs.'
}
$originalTask = Read-KeySwapTask
$allowedPaths = @($source, $target)
if ($null -ne $originalTask) { $allowedPaths += $originalTask.Path }
$originalProcesses = @(Get-KeySwapProcesses)
foreach ($process in $originalProcesses) {
    if ($process.Path -notin $allowedPaths) { throw "Refusing to stop unexpected KeySwap PID $($process.Id) at $($process.Path)." }
}
$preferences = @{}
$key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\KeySwap')
try {
    foreach ($name in @('Enabled', 'BridgeEnabled', 'AutoStart', 'KeepAltTab')) {
        $present = $null -ne $key -and $name -in $key.GetValueNames()
        $preferences[$name] = [pscustomobject]@{ Present = $present; Value = $(if ($present) { $key.GetValue($name) } else { $null }); Kind = $(if ($present) { $key.GetValueKind($name) } else { $null }) }
    }
} finally { if ($null -ne $key) { $key.Dispose() } }
$bridgeEnabled = $preferences['BridgeEnabled'].Present -and [int]$preferences['BridgeEnabled'].Value -ne 0
if ($bridgeEnabled) { $null = Get-Command Get-NetUDPEndpoint -ErrorAction Stop }
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($source)
$version = '{0}.{1}.{2}' -f $versionInfo.FileMajorPart, $versionInfo.FileMinorPart, $versionInfo.FileBuildPart
if ($version -eq '0.0.0') { throw 'Source executable has no valid KeySwap file version.' }
$sourceHash = [KeySwap.Installation.InstallSecurity]::Sha256($source)
$targetHash = if ($targetExisted) { [KeySwap.Installation.InstallSecurity]::Sha256($target) } else { $null }
# Use the same canonical settings as AppServices.BuildTaskXml. Exported task XML
# may omit default-valued elements, and older tasks may use account names instead
# of SIDs. The original XML is retained verbatim for rollback.
$newXml = New-KeySwapTaskXml $target

try {
    $stage = [KeySwap.Installation.InstallSecurity]::StageExecutable($source, $destination)
    $backupDirectory = [KeySwap.Installation.InstallSecurity]::ValidateDirectory((Join-Path $destination ('install-backups\' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N'))))
    if ($null -ne $originalTask) { [IO.File]::WriteAllText((Join-Path $backupDirectory 'task-before.xml'), $originalTask.Xml, [Text.Encoding]::Unicode) }
    [IO.File]::WriteAllText((Join-Path $backupDirectory 'task-after.xml'), $newXml, [Text.Encoding]::Unicode)
    if ($targetExisted) {
        $copy = [KeySwap.Installation.InstallSecurity]::StageExecutable($target, $backupDirectory)
        [IO.File]::Move($copy, (Join-Path $backupDirectory 'KeySwap.before-copy.exe'))
    }
    [pscustomobject]@{ Source = $source; Target = $target; SourceHash = $sourceHash; OriginalHash = $targetHash; UserSid = $currentSid; SessionId = $currentSession; Version = $version; BridgeEnabled = $bridgeEnabled } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backupDirectory 'installation.json') -Encoding UTF8

    # Recheck files immediately before stopping an application.
    [KeySwap.Installation.InstallSecurity]::ValidatePath($target, $false)
    if ([KeySwap.Installation.InstallSecurity]::Sha256($stage) -ne $sourceHash) { throw 'Staged executable changed after verification.' }
    if ($targetExisted -and [KeySwap.Installation.InstallSecurity]::Sha256($target) -ne $targetHash) { throw 'Installed executable changed during preflight.' }
    $taskBeforeStop = Read-KeySwapTask
    if (($null -eq $originalTask) -ne ($null -eq $taskBeforeStop) -or
        ($null -ne $originalTask -and $originalTask.Xml -cne $taskBeforeStop.Xml)) { throw 'KeySwap task changed during preflight.' }
    foreach ($process in $originalProcesses) { Stop-KeySwapGracefully $process; $stoppedOriginals += $process }
    if (@(Get-KeySwapProcesses).Count -ne 0) { throw 'Another KeySwap process appeared during installation.' }
    if ($targetExisted) {
        $oldBinary = Join-Path $backupDirectory 'KeySwap.original.exe'
        [IO.File]::Move($target, $oldBinary)
        $targetChanged = $true
    }
    [IO.File]::Move($stage, $target)
    $targetChanged = $true
    $stage = $null
    if ([KeySwap.Installation.InstallSecurity]::GetFileIntegrityRid($target) -lt 8192 -or [KeySwap.Installation.InstallSecurity]::Sha256($target) -ne $sourceHash) { throw 'Installed executable failed integrity/hash validation.' }
    Register-ScheduledTask -TaskName $taskName -TaskPath '\' -Xml $newXml -Force -ErrorAction Stop | Out-Null
    $taskChanged = $true
    Start-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction Stop
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $ready = $false
    do {
        $running = @(Get-KeySwapProcesses)
        if ($running.Count -gt 1) { throw 'More than one KeySwap process is running.' }
        if ($running.Count -eq 1) {
            if ($running[0].Path -ine $target) { throw 'The scheduled task launched an unexpected executable.' }
            $launched = $running[0]
            if ($launched.IntegrityRid -lt 12288) { throw "Installed KeySwap started below High integrity (RID $($launched.IntegrityRid))." }
            $ready = [KeySwap.Installation.InstallRuntime]::HasTrayIcon($launched) -and (-not $bridgeEnabled -or (Test-BridgeReady $launched $version))
            if ($ready -and $watch.ElapsedMilliseconds -ge 1500) { break }
        }
        Start-Sleep -Milliseconds 150
    } while ($watch.ElapsedMilliseconds -lt 15000)
    if (-not $ready -or $null -eq $launched -or -not (Test-ProcessIdentity $launched)) { throw 'Installed KeySwap did not pass startup validation within 15 seconds.' }
    $verifiedTask = Read-KeySwapTask
    if ($null -eq $verifiedTask -or $verifiedTask.Path -ine $target) { throw 'Installed task does not point to the verified executable.' }
    [pscustomobject]@{ Installed = $target; SHA256 = $sourceHash; ProcessId = $launched.Id; Integrity = 'High'; SessionId = $launched.SessionId; Tray = 'Registered'; BridgeStatus = $(if ($bridgeEnabled) { 'Exact V2 reply verified' } else { 'Disabled by saved preference' }); Backup = $backupDirectory }
} catch {
    $failure = $_
    $rollbackErrors = New-Object 'Collections.Generic.List[string]'
    $safeToRestoreBinary = $true
    if ($null -ne $launched) {
        try { Stop-KeySwapGracefully $launched } catch { $safeToRestoreBinary = $false; $rollbackErrors.Add("stop new process: $_") }
    }
    if ($targetChanged -and $safeToRestoreBinary) {
        try {
            [KeySwap.Installation.InstallSecurity]::ValidatePath($target, $false)
            if ([IO.File]::Exists($target)) {
                if ([KeySwap.Installation.InstallSecurity]::Sha256($target) -ne $sourceHash) { throw 'Target changed externally; refusing to overwrite it during rollback.' }
                [IO.File]::Move($target, (Join-Path $backupDirectory 'KeySwap.failed.exe'))
            }
            if ($null -ne $oldBinary -and [IO.File]::Exists($oldBinary)) { [IO.File]::Move($oldBinary, $target) }
        } catch { $rollbackErrors.Add("restore binary: $_") }
    }
    if ($taskChanged -and $safeToRestoreBinary) {
        try {
            $currentTask = Read-KeySwapTask
            if ($null -ne $currentTask -and $currentTask.Path -ine $target) { throw 'Task changed externally; refusing to overwrite it during rollback.' }
            if ($null -ne $originalTask) {
                Register-ScheduledTask -TaskName $taskName -TaskPath '\' -Xml $originalTask.Xml -Force -ErrorAction Stop | Out-Null
            } elseif ($null -ne $currentTask) {
                Unregister-ScheduledTask -TaskName $taskName -TaskPath '\' -Confirm:$false -ErrorAction Stop
            }
        } catch { $rollbackErrors.Add("restore task: $_") }
    }
    if ($null -ne $launched -and $safeToRestoreBinary) {
        try {
            $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\KeySwap')
            try { foreach ($name in $preferences.Keys) { if ($preferences[$name].Present) { $key.SetValue($name, $preferences[$name].Value, $preferences[$name].Kind) } else { $key.DeleteValue($name, $false) } } }
            finally { $key.Dispose() }
        } catch { $rollbackErrors.Add("restore preferences: $_") }
    }
    if ($safeToRestoreBinary -and $rollbackErrors.Count -eq 0 -and $stoppedOriginals.Count -gt 0) {
        try {
            if ($null -ne $originalTask -and $originalTask.Enabled) { Start-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction Stop }
            else { Start-Process -FilePath $stoppedOriginals[0].Path -ArgumentList '--tray' -WindowStyle Hidden | Out-Null }
        } catch { $rollbackErrors.Add("restart original process: $_") }
    }
    if ($rollbackErrors.Count -gt 0) { Write-Warning ('Rollback incomplete: ' + ($rollbackErrors -join '; ')) }
    throw "Installation failed. Backup: $backupDirectory. Cause: $($failure.Exception.Message)"
} finally {
    if ($null -ne $stage -and [IO.File]::Exists($stage)) { [IO.File]::Delete($stage) }
}
