$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$out  = Join-Path $root 'dist\KeySwap.exe'

$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw '找不到 .NET Framework 自带的 csc.exe'
}

New-Item -ItemType Directory -Force -Path (Split-Path $out -Parent) | Out-Null

& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 `
    "/out:$out" `
    "/win32manifest:$(Join-Path $root 'src\app.manifest')" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    (Join-Path $root 'src\KeySwap.cs')

if ($LASTEXITCODE -ne 0) { throw "编译失败，csc 退出码 $LASTEXITCODE" }

$f = Get-Item $out
"已生成: $($f.FullName)  ($([math]::Round($f.Length/1KB,1)) KB)"

# DPI 感知已在代码里调用 SetProcessDpiAwarenessContext 声明，无需 .config，单文件即可运行
