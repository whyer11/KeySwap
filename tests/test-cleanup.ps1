$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$compilerPath = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('KeySwap-cleanup-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
try {
    $testExecutable = Join-Path $testDirectory 'CleanupQueueTests.exe'
    $sources = @((Join-Path $repoRoot 'src\KeyboardState.cs'), (Join-Path $repoRoot 'src\CleanupQueue.cs'), (Join-Path $PSScriptRoot 'CleanupQueueTests.cs'))
    & $compilerPath /nologo /target:exe /platform:x64 /codepage:65001 "/out:$testExecutable" /r:System.dll $sources
    if ($LASTEXITCODE -ne 0) { throw "Cleanup test compilation failed: $LASTEXITCODE" }
    & $testExecutable
    if ($LASTEXITCODE -ne 0) { throw "Cleanup tests failed: $LASTEXITCODE" }
}
finally {
    $resolvedTestDirectory = [IO.Path]::GetFullPath($testDirectory)
    $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedTestDirectory.StartsWith($resolvedTempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedTestDirectory -Recurse -Force
    }
}
