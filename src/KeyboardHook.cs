using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace KeySwap
{
    // Windows invokes a LL hook on its installing thread. Keep that message
    // pump independent of slow UI, registry, task scheduler and disk operations.
    internal sealed class KeyboardHook : IDisposable
    {
        private Thread thread;
        private Control dispatcher;
        private IntPtr hook;
        private Native.LowLevelKeyboardProc callback;
        private KeyboardState state = new KeyboardState();
        private volatile bool preserve = true;
        private bool faulted;
        public bool KeepAltTab { get { return preserve; } set { preserve = value; } }
        public bool InputSwapped { get; set; }
        public event Action<string> Error;
        public event Action<Native.KBDLLHOOKSTRUCT, bool> KeyObserved;

        public bool Start()
        {
            if (thread != null) return hook != IntPtr.Zero;
            faulted = false;
            state = new KeyboardState();
            var ready = new ManualResetEvent(false);
            thread = new Thread(delegate()
            {
                try
                {
                    using (var control = new Control())
                    {
                        dispatcher = control;
                        var handle = control.Handle;
                        callback = OnKey;
                        using (var process = Process.GetCurrentProcess())
                        using (var module = process.MainModule)
                            hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, callback,
                                Native.GetModuleHandle(module.ModuleName), 0);
                        state = new KeyboardState((GetAsyncKeyState(0xA4) & 0x8000) != 0,
                            (GetAsyncKeyState(0xA2) & 0x8000) != 0, InputSwapped);
                        ready.Set();
                        if (hook != IntPtr.Zero) Application.Run();
                    }
                }
                catch (Exception ex)
                {
                    var handler = Error;
                    if (handler != null) handler("键盘线程启动失败：" + ex.Message);
                }
                finally
                {
                    Release();
                    dispatcher = null;
                    ready.Set();
                }
            });
            thread.IsBackground = true;
            thread.Name = "KeySwap keyboard";
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.WaitOne();
            return hook != IntPtr.Zero;
        }

        private bool Send(ScanEvent[] events)
        {
            var batch = new Native.ScanEvent[events.Length];
            for (int i = 0; i < events.Length; i++) batch[i] = new Native.ScanEvent(events[i].Scan, events[i].Up);
            return Native.SendBatch(batch);
        }

        private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0 || faulted) return Native.CallNextHookEx(hook, code, wParam, lParam);
            var info = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
            if (info.dwExtraInfo == Native.MAGIC) return Native.CallNextHookEx(hook, code, wParam, lParam);
            int message = wParam.ToInt32();
            bool up = message == Native.WM_KEYUP || message == Native.WM_SYSKEYUP;
            var observed = KeyObserved;
            if (observed != null) observed(info, up); // subscriber only enqueues; never performs I/O
            var action = state.Handle(info.vkCode, info.scanCode, info.flags, up, preserve);
            if (action.Handled)
            {
                if (Send(action.Events)) { state.Confirm(); return (IntPtr)1; }
                int error = Marshal.GetLastWin32Error();
                faulted = true;
                Send(state.ReleaseAll().Events);
                var handler = Error;
                if (handler != null) handler("按键发送失败，互换已暂停（Win32 " + error + "）。请重新启用。" );
            }
            return Native.CallNextHookEx(hook, code, wParam, lParam);
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        private void Release()
        {
            // Release owned keys before unhooking, so the MAGIC bypass still applies.
            Send(state.ReleaseAll().Events);
            if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        }

        public void Stop()
        {
            var control = dispatcher;
            if (control != null && !control.IsDisposed)
            {
                try { control.BeginInvoke((Action)delegate { Release(); Application.ExitThread(); }); }
                catch (InvalidOperationException) { }
            }
            if (thread != null && thread != Thread.CurrentThread) thread.Join();
            thread = null;
        }
        public void Dispose() { Stop(); }
    }
}
