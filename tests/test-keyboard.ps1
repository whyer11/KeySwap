param([string]$LegacySource)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $root 'dist/tests'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources = @((Join-Path $PSScriptRoot 'KeyboardStateTests.cs'))
$defines = @()
if ($LegacySource) {
    $defines = @('/define:LEGACY')
    $old = Get-Content -Raw -LiteralPath $LegacySource
    $hook = [regex]::Match($old, '(?s)        private IntPtr HookProc\(.*?(?=        private void LogKey)').Value
    $reset = [regex]::Match($old, '(?s)        private void ResetComboState\(.*?(?=        private IntPtr HookProc)').Value
    $shim = @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
namespace KeySwap {
public struct ScanEvent { public ushort Scan; public bool Up; public ScanEvent(ushort s,bool u){Scan=s;Up=u;} }
public class KeyAction { public bool Handled; public ScanEvent[] Events; public KeyAction(bool h,ScanEvent[] e){Handled=h;Events=e;} }
static class Native {
public const int WM_KEYUP=0x101,WM_SYSKEYUP=0x105; public const uint LLKHF_EXTENDED=1,VK_LMENU=0xA4,VK_LCONTROL=0xA2,VK_TAB=9; public const ushort SC_LEFTALT=0x38,SC_LEFTCTRL=0x1D,SC_TAB=15;
public static IntPtr MAGIC=new IntPtr(123); public static List<KeySwap.ScanEvent> Events=new List<KeySwap.ScanEvent>();
[StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT { public uint vkCode,scanCode,flags,time; public IntPtr dwExtraInfo; }
public struct ScanEvent { public ushort scan;public bool up;public ScanEvent(ushort s,bool u){scan=s;up=u;} }
public static void SendScan(ushort s,bool u){Events.Add(new KeySwap.ScanEvent(s,u));}
public static void SendBatch(ScanEvent[] events){foreach(var e in events)SendScan(e.scan,e.up);}
public static IntPtr CallNextHookEx(IntPtr h,int n,IntPtr w,IntPtr l){return IntPtr.Zero;}
}
public class KeyboardState {
public void Confirm(){}
class CheckBox {public bool Checked;}
CheckBox chkKeepAltTab=new CheckBox(); IntPtr _hookId=new IntPtr(1); bool _physAltDown,_altTabEngaged,_logKeys;
void LogKey(Native.KBDLLHOOKSTRUCT i,bool u){}
public KeyAction Handle(uint vk,uint scan,uint flags,bool up,bool preserve){
Native.Events.Clear();chkKeepAltTab.Checked=preserve;var data=new Native.KBDLLHOOKSTRUCT{vkCode=vk,scanCode=scan,flags=flags};
var p=Marshal.AllocHGlobal(Marshal.SizeOf(data));try{Marshal.StructureToPtr(data,p,false);var r=HookProc(0,new IntPtr(up?0x101:0x100),p);return new KeyAction(r!=IntPtr.Zero,Native.Events.ToArray());}finally{Marshal.FreeHGlobal(p);}}
public KeyAction ReleaseAll(){ResetComboState();return new KeyAction(false,new KeySwap.ScanEvent[0]);}
'@
    $legacy = Join-Path $outDir 'LegacyAdapter.cs'
    Set-Content -LiteralPath $legacy -Value ($shim + $reset + $hook + "`n}}") -Encoding utf8
    $sources += $legacy
} else { $sources += Join-Path $root 'src/KeyboardState.cs' }
$exe = Join-Path $outDir 'KeyboardStateTests.exe'
& $csc /nologo /platform:x64 /out:$exe $defines $sources
if ($LASTEXITCODE -ne 0) { throw 'Test compile failed' }
& $exe
exit $LASTEXITCODE
