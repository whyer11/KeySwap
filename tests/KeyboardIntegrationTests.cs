using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using KeySwap;

// Explicit opt-in desktop integration probe. Creates only test-owned windows,
// never constructs MainForm (which would register startup tasks), and restores
// the original foreground window in finally. Synthetic input is not hardware.
internal static class KeyboardIntegrationTests
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    private static readonly IntPtr ProbeMarker = new IntPtr(0x54455354);
    #if LEGACY
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    #endif
    private static Native.LowLevelKeyboardProc _observer;
    private static IntPtr _observerHook;
    #if LEGACY
    private static MainForm _subject;
    #else
    private static KeyboardHook _subject;
    #endif
    private static Form _a, _b;
    private static ProbeTextBox _text;
    private static int _failures;
    private sealed class ProbeTextBox : TextBox
    {
        public int ControlCopies;
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.C)) { ControlCopies++; return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || (args[0] != "--desktop-probe" && args[0] != "--startup-probe"))
        {
            Console.WriteLine("Opt-in required: --desktop-probe (temporarily sends Alt/Tab to two test windows).");
            return 2;
        }
        IntPtr original = GetForegroundWindow();
        System.Threading.Timer watchdog = null;
        try
        {
            Application.EnableVisualStyles();
            _a = NewWindow("KeySwap probe A", 100);
            _b = NewWindow("KeySwap probe B", 540);
            _text = new ProbeTextBox { Dock = DockStyle.Fill, Multiline = true,
                Text = "KeySwap integration probe: this textbox intercepts Ctrl+C without changing the clipboard." };
            _a.Controls.Add(_text);
            _a.Show(); _b.Show(); Wait(300);
            #if !LEGACY
            if (args[0] == "--startup-probe")
            {
                watchdog = new System.Threading.Timer(delegate
                {
                    Console.WriteLine("WATCHDOG: startup probe exceeded 15 seconds; releasing probe keys.");
                    Release();
                    Thread.Sleep(150);
                    Environment.Exit(4);
                }, null, 15000, Timeout.Infinite);
                _subject = new KeyboardHook();
                _subject.Error += delegate(string error) { Console.WriteLine("HOOK ERROR " + error); Interlocked.Increment(ref _failures); };
                Check("initial startup probe hook starts", _subject.Start());
                ProbeStartWithNativeCtrlHeld();
                Console.WriteLine("STARTUP INTEGRATION failures=" + _failures);
                return _failures == 0 ? 0 : 1;
            }
            #endif
            Observe();
            Probe("native scan left Alt", false, 0, 8);
            #if LEGACY
            Probe("native virtual left Alt", false, 0xA4, 0);
            Probe("native virtual extended left Alt", false, 0xA4, 1);
            Probe("native scan extended Alt", false, 0, 9);
            #endif
            Native.UnhookWindowsHookEx(_observerHook); _observerHook = IntPtr.Zero;
            #if LEGACY
            _subject = (MainForm)FormatterServices.GetUninitializedObject(typeof(MainForm));
            typeof(MainForm).GetField("chkKeepAltTab", Private).SetValue(_subject, new CheckBox { Checked = true });
            Delegate proc = Delegate.CreateDelegate(typeof(Native.LowLevelKeyboardProc), _subject,
                typeof(MainForm).GetMethod("HookProc", Private));
            typeof(MainForm).GetField("_proc", Private).SetValue(_subject, proc);
            bool installed = (bool)typeof(MainForm).GetMethod("InstallHook", Private).Invoke(_subject, null);
            #else
            _subject = new KeyboardHook();
            _subject.KeepAltTab = true;
            _subject.Error += delegate(string error) { Console.WriteLine("HOOK ERROR " + error); Interlocked.Increment(ref _failures); };
            bool installed = _subject.Start();
            #endif
            Console.WriteLine("Subject hook installed: " + installed);
            if (!installed) return 3;
            Observe();
            Probe("subject hook, scan pseudo-physical left Alt", true, 0, 8);
            Probe("subject hook, virtual pseudo-physical left Alt", true, 0xA4, 0);
            #if !LEGACY
            ProbeHeldCycle();
            ProbeShortcut("mapped Alt+C reaches Ctrl+C");
            ProbeStop();
            ProbeStartWithNativeCtrlHeld();
            ProbeUiStall();
            ProbeShortcut("mapped shortcut after UI stall");
            #endif
            Console.WriteLine("INTEGRATION failures=" + _failures + "; synthetic desktop input only; physical keyboard still needs user verification.");
            return _failures == 0 ? 0 : 1;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
        finally
        {
            if (watchdog != null) watchdog.Dispose();
            if (_observerHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_observerHook);
            #if LEGACY
            if (_subject != null) typeof(MainForm).GetMethod("UninstallHook", Private).Invoke(_subject, null);
            #else
            if (_subject != null) _subject.Stop();
            #endif
            Release(); Wait(150);
            if (_a != null) _a.Dispose();
            if (_b != null) _b.Dispose();
            SetForegroundWindow(original);
        }
    }

    private static Form NewWindow(string title, int left)
    {
        return new Form { Text = title, Left = left, Top = 150, Width = 420, Height = 160,
            StartPosition = FormStartPosition.Manual };
    }
    private static void Observe()
    {
        _observer = delegate(int n, IntPtr w, IntPtr p)
        {
            if (n >= 0)
            {
                Native.KBDLLHOOKSTRUCT k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(p, typeof(Native.KBDLLHOOKSTRUCT));
                if (k.vkCode == 0x09 || (k.vkCode >= 0xA2 && k.vkCode <= 0xA5))
                    Console.WriteLine("event msg={0:X} vk={1:X} scan={2:X} flags={3:X} extra={4:X}", w.ToInt32(), k.vkCode, k.scanCode, k.flags, k.dwExtraInfo.ToInt64());
            }
            return Native.CallNextHookEx(_observerHook, n, w, p);
        };
        using (Process process = Process.GetCurrentProcess())
            _observerHook = Native.SetWindowsHookEx(13, _observer, Native.GetModuleHandle(process.MainModule.ModuleName), 0);
    }
    private static void Probe(string title, bool throughSubject, ushort altVk, uint altFlags)
    {
        Console.WriteLine("CASE " + title);
        Release(); Wait(150);
        _b.Activate(); SetForegroundWindow(_b.Handle); Wait(250);
        _a.Activate(); SetForegroundWindow(_a.Handle); Wait(300);
        State("before");
        Check("test window A received foreground", GetForegroundWindow() == _a.Handle);
        IntPtr marker = throughSubject ? ProbeMarker : Native.MAGIC;
        Send(altVk, 0x38, altFlags, marker); Wait(250);
        Send(0, 0x0F, 8, marker); Wait(300);
        Send(0, 0x0F, 10, marker); Wait(450);
        State("held");
        Send(altVk, 0x38, altFlags | 2, marker); Wait(700);
        State("released");
        Check("switched to test window B", GetForegroundWindow() == _b.Handle);
        Check("mapped keys released", GetAsyncKeyState(0xA4) >= 0 && GetAsyncKeyState(0xA2) >= 0);
    }
    #if !LEGACY
    private static void Prepare()
    {
        Release(); Wait(100);
        _b.Activate(); SetForegroundWindow(_b.Handle); Wait(150);
        _a.Activate(); SetForegroundWindow(_a.Handle); _text.Focus(); Wait(200);
        Check("test window A received foreground", GetForegroundWindow() == _a.Handle);
    }
    private static void TapTab(IntPtr marker)
    {
        Send(0, 15, 8, marker); Wait(160); Send(0, 15, 10, marker); Wait(160);
    }
    private static void ProbeHeldCycle()
    {
        Console.WriteLine("CASE repeated Tab while holding mapped Alt, then Shift+Tab reverse");
        Prepare();
        Send(0, 0x38, 8, ProbeMarker); Wait(150);
        TapTab(ProbeMarker); TapTab(ProbeMarker);
        Send(0, 0x2A, 8, ProbeMarker); Wait(100);
        TapTab(ProbeMarker);
        Send(0, 0x2A, 10, ProbeMarker); Wait(100);
        State("held after Tab/Tab/ShiftTab");
        Check("Alt stays down while selection is open", GetAsyncKeyState(0xA4) < 0 && GetAsyncKeyState(0xA2) >= 0);
        Send(0, 0x38, 10, ProbeMarker); Wait(600);
        Check("repeated cycle commits test window B", GetForegroundWindow() == _b.Handle);
    }
    private static void ProbeShortcut(string title)
    {
        Console.WriteLine("CASE " + title); Prepare();
        int before = _text.ControlCopies;
        Send(0, 0x38, 8, ProbeMarker); Wait(100);
        Send(0, 0x2E, 8, ProbeMarker); Wait(100);
        Send(0, 0x2E, 10, ProbeMarker); Send(0, 0x38, 10, ProbeMarker); Wait(200);
        Check("textbox saw exactly one Ctrl+C", _text.ControlCopies == before + 1);
        Check("shortcut leaves modifiers released", GetAsyncKeyState(0xA4) >= 0 && GetAsyncKeyState(0xA2) >= 0);
    }
    private static void ProbeStop()
    {
        Console.WriteLine("CASE disabling hook releases owned Ctrl"); Prepare();
        Send(0, 0x38, 8, ProbeMarker); Wait(150);
        Check("mapped Ctrl is down before Stop", GetAsyncKeyState(0xA2) < 0);
        // Remove this UI-thread observer before waiting for worker shutdown.
        Native.UnhookWindowsHookEx(_observerHook); _observerHook = IntPtr.Zero;
        _subject.Stop(); Wait(150);
        Check("Stop releases mapped Ctrl", GetAsyncKeyState(0xA2) >= 0 && GetAsyncKeyState(0xA4) >= 0);
        Send(0, 0x38, 10, ProbeMarker); Wait(100);
        Check("same worker wrapper can restart", _subject.Start());
    }
    private static void ProbeUiStall()
    {
        Console.WriteLine("CASE hook survives UI message pump stall exceeding one second"); Prepare();
        Thread sender = new Thread(delegate()
        {
            Thread.Sleep(100);
            Send(0, 0x38, 8, ProbeMarker);
            Thread.Sleep(100);
            Send(0, 0x0F, 8, ProbeMarker);
            Thread.Sleep(100);
            Send(0, 0x0F, 10, ProbeMarker);
            Thread.Sleep(100);
            Send(0, 0x38, 10, ProbeMarker);
        });
        sender.IsBackground = true; sender.Start();
        Thread.Sleep(1700); // Deliberately no Application.DoEvents on the UI thread.
        sender.Join(); Wait(300);
        Check("window switching survives UI stall", GetForegroundWindow() == _b.Handle);
        Check("UI stall leaves modifiers released", GetAsyncKeyState(0xA4) >= 0 && GetAsyncKeyState(0xA2) >= 0);
    }
    private static void ProbeStartWithNativeCtrlHeld()
    {
        Console.WriteLine("CASE enabling while native LeftCtrl is already held");
        State("before Stop");
        _subject.Stop(); Wait(150); Prepare();
        State("after Stop, before native Ctrl down");
        Send(0, 0x1D, 8, ProbeMarker);
        Console.WriteLine("native Ctrl down SendInput returned");
        Wait(150); State("after native Ctrl down");
        Check("native Ctrl is down before Start", GetAsyncKeyState(0xA2) < 0 && GetAsyncKeyState(0xA4) >= 0);
        Check("hook starts with native Ctrl held", _subject.Start());
        State("after Start, before native Ctrl up");
        Send(0, 0x1D, 10, ProbeMarker);
        Console.WriteLine("native Ctrl up SendInput returned");
        Wait(200); State("after native Ctrl up");
        Check("native Ctrl release after Start reaches Windows", GetAsyncKeyState(0xA2) >= 0 && GetAsyncKeyState(0xA4) >= 0);
        ProbeShortcut("mapped shortcut resumes after pre-existing Ctrl is released");
    }
    #endif
    private static void Check(string label, bool success)
    {
        Console.WriteLine((success ? "PASS " : "FAIL ") + label);
        if (!success) _failures++;
    }
    private static void Send(ushort vk, ushort scan, uint flags, IntPtr marker)
    {
        Native.INPUT input = new Native.INPUT(); input.type=1;
        input.ki.wVk=vk; input.ki.wScan=scan; input.ki.dwFlags=flags; input.ki.dwExtraInfo=marker;
        uint inserted = Native.SendInput(1, new Native.INPUT[] { input }, Marshal.SizeOf(typeof(Native.INPUT)));
        if (inserted != 1) Console.WriteLine("SEND FAILURE error=" + Marshal.GetLastWin32Error());
    }
    private static void Release()
    {
        Send(0, 0x0F, 10, Native.MAGIC);
        Send(0, 0x38, 10, Native.MAGIC);
        Send(0, 0x38, 11, Native.MAGIC);
        Send(0, 0x1D, 10, Native.MAGIC);
        Send(0, 0x2A, 10, Native.MAGIC);
    }
    private static void State(string phase)
    {
        IntPtr hwnd=GetForegroundWindow(); StringBuilder title=new StringBuilder(256); GetWindowText(hwnd,title,256);
        Console.WriteLine("{0} foreground={1:X} [{2}] LAlt={3} RAlt={4} LCtrl={5}",phase,hwnd.ToInt64(),title,
            GetAsyncKeyState(0xA4)<0,GetAsyncKeyState(0xA5)<0,GetAsyncKeyState(0xA2)<0);
    }
    private static void Wait(int milliseconds)
    {
        Stopwatch watch=Stopwatch.StartNew();
        while(watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
    }
}
