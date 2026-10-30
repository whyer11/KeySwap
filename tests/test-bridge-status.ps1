$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$compilerPath = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('KeySwap-bridge-status-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
try {
    $testExecutable = Join-Path $testDirectory 'BridgeStatusTests.exe'
    & $compilerPath /nologo /target:exe /platform:x64 /codepage:65001 "/out:$testExecutable" /r:System.dll (Join-Path $repoRoot 'src\BridgeStatusServer.cs') (Join-Path $PSScriptRoot 'BridgeStatusTests.cs')
    if ($LASTEXITCODE -ne 0) { throw "Bridge status test compilation failed: $LASTEXITCODE" }
    & $testExecutable
    if ($LASTEXITCODE -ne 0) { throw "Bridge status tests failed: $LASTEXITCODE" }
}
finally {
    $resolvedTestDirectory = [IO.Path]::GetFullPath($testDirectory)
    $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedTestDirectory.StartsWith($resolvedTempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedTestDirectory -Recurse -Force
    }
}
