param([switch]$Desktop)
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/tests/test-keyboard.ps1"
if ($LASTEXITCODE -ne 0) { throw 'Keyboard state tests failed' }
& "$PSScriptRoot/tests/test-cleanup.ps1"
if ($LASTEXITCODE -ne 0) { throw 'Cleanup tests failed' }
& "$PSScriptRoot/tests/test-services.ps1"
if ($LASTEXITCODE -ne 0) { throw 'Service tests failed' }
& "$PSScriptRoot/tests/test-calibration.ps1"
if ($LASTEXITCODE -ne 0) { throw 'Calibration tests failed' }
& "$PSScriptRoot/tests/test-bridge-status.ps1"
if ($LASTEXITCODE -ne 0) { throw 'Bridge status tests failed' }
& "$PSScriptRoot/tests/test-install-security.ps1"
if ($LASTEXITCODE -ne 0) { throw 'Installation security tests failed' }
& "$PSScriptRoot/tests/test-installer.ps1"
if ($LASTEXITCODE -ne 0) { throw 'Installer tests failed' }
if ($Desktop) {
    & "$PSScriptRoot/tests/test-keyboard-integration.ps1" -DesktopProbe
    if ($LASTEXITCODE -ne 0) { throw 'Desktop integration tests failed' }
}
