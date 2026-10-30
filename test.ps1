param([switch]$Desktop)
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/tests/test-keyboard.ps1"
if ($LASTEXITCODE -ne 0) { throw 'Keyboard state tests failed' }
& "$PSScriptRoot/tests/test-services.ps1"
if ($LASTEXITCODE -ne 0) { throw 'Service tests failed' }
& "$PSScriptRoot/tests/test-calibration.ps1"
if ($LASTEXITCODE -ne 0) { throw 'Calibration tests failed' }
if ($Desktop) {
    & "$PSScriptRoot/tests/test-keyboard-integration.ps1" -DesktopProbe
    if ($LASTEXITCODE -ne 0) { throw 'Desktop integration tests failed' }
}
