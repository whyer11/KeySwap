# Read-only installer checks. Execute the production planning functions against
# task snapshots; never register tasks, stop processes, or launch KeySwap.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path $PSScriptRoot -Parent
foreach ($helper in @('InstallSecurity', 'InstallRuntime')) {
    if (-not ("KeySwap.Installation.$helper" -as [type])) { Add-Type -Path (Join-Path $repoRoot "scripts\$helper.cs") }
}
$errors = $null
$tokens = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot 'scripts\install.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw ($errors | Out-String) }
foreach ($name in @('Read-KeySwapTask', 'New-KeySwapTaskXml')) {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }.GetNewClosure(), $true)
    if ($null -eq $definition) { throw "Production function missing: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}
$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskName = 'KeySwap'
$taskNamespace = 'http://schemas.microsoft.com/windows/2004/02/mit/task'
$fixtureXml = $null
$failures = 0
function Get-ScheduledTask { param($TaskPath, $ErrorAction) return [pscustomobject]@{ TaskName = 'KeySwap'; Settings = [pscustomobject]@{ Enabled = $true } } }
function Export-ScheduledTask { param($TaskName, $TaskPath, $ErrorAction) return $script:fixtureXml }
function Assert-True($Value, [string] $Message) { if (-not $Value) { throw $Message } }
function Test([string] $Name, [scriptblock] $Action) {
    try { & $Action; Write-Output "PASS $Name" }
    catch { $script:failures++; Write-Output "FAIL $Name`: $_" }
}
function Assert-Rejected([scriptblock] $Action, [string] $Match) {
    try { & $Action | Out-Null } catch { if ($_.Exception.Message -like "*$Match*") { return }; throw }
    throw 'Expected unsafe input to be rejected.'
}
$target = Join-Path ([IO.Path]::GetTempPath()) 'KeySwap installer test\KeySwap.exe'
$canonical = New-KeySwapTaskXml $target

Test 'fresh task XML is accepted by Windows without registration' {
    $service = New-Object -ComObject Schedule.Service
    $definition = $null
    try {
        $service.Connect()
        $definition = $service.NewTask(0)
        $definition.XmlText = $canonical
        Assert-True ($definition.Actions.Item(1).Path -ceq $target) 'XML executable path did not round-trip.'
        Assert-True ($definition.Principal.LogonType -eq 3 -and $definition.Principal.RunLevel -eq 1) 'Task must be interactive and elevated.'
        Assert-True ($definition.Settings.ExecutionTimeLimit -eq 'PT0S') 'Task lifetime is bounded.'
        Assert-True (-not $definition.Settings.DisallowStartIfOnBatteries -and -not $definition.Settings.StopIfGoingOnBatteries) 'Battery setting can stop the app.'
    } finally {
        if ($null -ne $definition) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($definition) | Out-Null }
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($service) | Out-Null
    }
}
Test 'existing task with omitted default Enabled is accepted and normalized' {
    $doc = New-Object Xml.XmlDocument
    $doc.LoadXml($canonical)
    $ns = New-Object Xml.XmlNamespaceManager($doc.NameTable)
    $ns.AddNamespace('t', $taskNamespace)
    $node = $doc.SelectSingleNode('/t:Task/t:Settings/t:Enabled', $ns)
    $null = $node.ParentNode.RemoveChild($node)
    $script:fixtureXml = $doc.OuterXml
    $snapshot = Read-KeySwapTask
    Assert-True ($snapshot.Path -ceq $target -and $snapshot.Enabled) 'Default-enabled task was rejected.'
    $normalized = New-Object Xml.XmlDocument
    $normalized.LoadXml((New-KeySwapTaskXml $target))
    $ns2 = New-Object Xml.XmlNamespaceManager($normalized.NameTable)
    $ns2.AddNamespace('t', $taskNamespace)
    Assert-True ($normalized.SelectSingleNode('/t:Task/t:Settings/t:Enabled', $ns2).InnerText -eq 'true') 'Task was not normalized.'
}
Test 'task backup retains the original XML unchanged' {
    $script:fixtureXml = $canonical
    $snapshot = Read-KeySwapTask
    $null = New-KeySwapTaskXml (Join-Path $env:USERPROFILE 'Applications\KeySwap\KeySwap.exe')
    Assert-True ($snapshot.Xml -ceq $canonical) 'Original task XML was altered.'
}
Test 'multiple task actions are rejected before process changes' {
    $script:fixtureXml = $canonical.Replace('</Actions>', '<Exec><Command>C:\Windows\System32\cmd.exe</Command></Exec></Actions>')
    Assert-Rejected { Read-KeySwapTask } 'exactly one direct Exec'
}
Test 'a task owned by another user is rejected' {
    $script:fixtureXml = $canonical.Replace($currentSid, 'S-1-5-18')
    Assert-Rejected { Read-KeySwapTask } 'another user'
}
Test 'unexpected command arguments are rejected' {
    $script:fixtureXml = $canonical.Replace('--tray', '--quit')
    Assert-Rejected { Read-KeySwapTask } 'arguments'
}
Test 'process inspection reads actual path session owner and integrity' {
    $record = [KeySwap.Installation.InstallRuntime]::Inspect($PID)
    $process = Get-Process -Id $PID
    Assert-True ($record.Id -eq $PID -and $record.Path -ieq $process.Path -and $record.SessionId -eq $process.SessionId -and $record.UserSid -eq $currentSid) 'Process identity mismatch.'
    Assert-True ($record.IntegrityRid -gt 0 -and $record.CreationTime -gt 0) 'Process security information missing.'
}
Test 'targeted Quit refuses a stale process identity before messaging' {
    $record = [KeySwap.Installation.InstallRuntime]::Inspect($PID)
    $record.CreationTime--
    Assert-Rejected { [KeySwap.Installation.InstallRuntime]::RequestQuit($record) } 'identity changed'
}
if ($failures -ne 0) { throw "Installer checks failed: $failures" }
Write-Output 'Installer checks: 8 passed; no task/process changes performed.'
