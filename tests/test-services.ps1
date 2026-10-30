$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$compilerPath = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('KeySwap-services-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
try {
    $testExecutable = Join-Path $testDirectory 'AppServicesTests.exe'
    & $compilerPath /nologo /target:exe /platform:x64 /codepage:65001 "/out:$testExecutable" /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Windows.Forms.dll (Join-Path $repoRoot 'src\AppServices.cs') (Join-Path $PSScriptRoot 'AppServicesTests.cs')
    if ($LASTEXITCODE -ne 0) { throw "Service test compilation failed: $LASTEXITCODE" }
    & $testExecutable
    if ($LASTEXITCODE -ne 0) { throw "Service tests failed: $LASTEXITCODE" }
}
finally {
    $resolvedTestDirectory = [IO.Path]::GetFullPath($testDirectory)
    $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedTestDirectory.StartsWith($resolvedTempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedTestDirectory -Recurse -Force
    }
}
