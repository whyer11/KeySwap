param([switch]$DesktopProbe, [switch]$StartupProbe, [string]$LegacySource)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $root 'dist\tests'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $outDir 'KeyboardIntegrationTests.exe'
$sources = @((Join-Path $PSScriptRoot 'KeyboardIntegrationTests.cs'))
$defines = @()
if ($LegacySource) {
    $sources += (Resolve-Path -LiteralPath $LegacySource).Path
    $defines = @('/define:LEGACY')
} else {
    $sources += Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
}
& $csc /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /main:KeyboardIntegrationTests "/out:$exe" /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll $defines $sources
if ($LASTEXITCODE -ne 0) { throw 'Integration probe compilation failed' }
if ($StartupProbe) {
    & $exe --startup-probe
    exit $LASTEXITCODE
} elseif ($DesktopProbe) {
    & $exe --desktop-probe
    exit $LASTEXITCODE
}
Write-Output 'Compiled. Run with -DesktopProbe to temporarily create two windows and send synthetic Alt/Tab/C input.'
