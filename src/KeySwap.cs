using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeySwap
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Native.EnablePerMonitorDpi();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool created;
            string name = @"Local\KeySwap-" + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            using (var instance = new System.Threading.Mutex(true, name, out created))
            {
                if (!created)
                {
                    Native.PostMessage(new IntPtr(0xffff), Native.ShowMessage, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                try { Application.Run(new MainForm()); }
                finally { instance.ReleaseMutex(); }
            }
        }
    }

    internal static class Native
    {
        public static readonly uint ShowMessage = RegisterWindowMessage("KeySwap.ShowMainWindow");
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string message);
        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;
        public const uint VK_TAB = 0x09;
        public const uint VK_LMENU = 0xA4;
        public const uint VK_LCONTROL = 0xA2;
        public const ushort SC_TAB = 0x0F;
        // 注意：扩展位是 0x01；0x20 是 LLKHF_ALTDOWN，别混用
        public const uint LLKHF_EXTENDED = 0x01;
        public const ushort SC_LEFTCTRL = 0x1D;
        public const ushort SC_LEFTALT = 0x38;
        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_SCANCODE = 0x0008;
        public static readonly IntPtr MAGIC = new IntPtr(0x4B53574D);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // x64 下 INPUT 实际大小为 40 字节（联合体里 MOUSEINPUT 最大），
        // cbSize 不匹配会让 SendInput 整个失败，必须显式指定
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        public struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public KEYBDINPUT ki;
        }

        public struct ScanEvent
        {
            public ushort scan;
            public bool up;
            public ScanEvent(ushort scan, bool up)
            {
                this.scan = scan;
                this.up = up;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int value);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        // 必须在创建任何窗口之前声明，否则 Windows 会按 96 DPI 渲染再位图放大
        public static void EnablePerMonitorDpi()
        {
            try
            {
                // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
            }
            catch (EntryPointNotFoundException) { }

            try
            {
                if (SetProcessDpiAwareness(2) == 0) return; // PROCESS_PER_MONITOR_DPI_AWARE
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }

            try { SetProcessDPIAware(); }
            catch (EntryPointNotFoundException) { }
        }

        public static void SendScan(ushort scan, bool up)
        {
            SendBatch(new ScanEvent[] { new ScanEvent(scan, up) });
        }

        // 一次 SendInput 提交，保证系统按数组顺序处理
        public static bool SendBatch(ScanEvent[] events)
        {
            if (events == null || events.Length == 0) return true;

            INPUT[] arr = new INPUT[events.Length];
            for (int i = 0; i < events.Length; i++)
            {
                INPUT inp = new INPUT();
                inp.type = INPUT_KEYBOARD;
                inp.ki.wVk = 0;
                inp.ki.wScan = events[i].scan;
                inp.ki.dwFlags = KEYEVENTF_SCANCODE | (events[i].up ? KEYEVENTF_KEYUP : 0);
                inp.ki.time = 0;
                inp.ki.dwExtraInfo = MAGIC;
                arr[i] = inp;
            }
            return SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(INPUT))) == arr.Length;
        }
    }

    internal sealed class MainForm : Form
    {
        private const string RegPath = @"SYSTEM\CurrentControlSet\Control\Keyboard Layout";
        private const string RegName = "Scancode Map";

        private static readonly byte[] SwapValue = new byte[]
        {
            0,0,0,0, 0,0,0,0, 3,0,0,0,
            0x1D,0, 0x38,0,
            0x38,0, 0x1D,0,
            0,0,0,0
        };

        private readonly CheckBox chkRuntime;
        private readonly CheckBox chkKeepAltTab;
        private readonly CheckBox chkInputSwapped;
        private readonly Label lblSysState;
        private readonly Label lblWarn;
        private readonly Button btnWrite;
        private readonly Button btnRemove;

        private KeyboardHook _keyboard;
        private bool _wantEnabled;
        private bool _settingsLoaded;
        private bool _autoStartEnabled;
        private bool _autoStartBusy;

        private readonly ConcurrentQueue<string> _keyLog = new ConcurrentQueue<string>();
        private bool _logKeys;
        private string _logPath;
        private Timer _logTimer;

        private NotifyIcon _tray;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _miShow;
        private ToolStripMenuItem _miSwap;
        private ToolStripMenuItem _miKeepAltTab;
        private ToolStripMenuItem _miAutoStart;
        private bool _reallyExit;
        private bool _startHidden;
        private bool _hideTipShown;

        public MainForm()
        {
            Text = "Alt / Ctrl 互换控制";
            // 关掉自动缩放，改为在 OnLoad 里按实际 DPI 显式缩放整个窗体
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(470, 372);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);

            GroupBox gbRuntime = new GroupBox();
            gbRuntime.Text = "运行时互换（立即生效，仅本程序运行期间）";
            gbRuntime.Location = new Point(14, 12);
            gbRuntime.Size = new Size(442, 144);
            Controls.Add(gbRuntime);

            chkRuntime = new CheckBox();
            chkRuntime.Text = "交换左侧 Alt 与左侧 Ctrl";
            chkRuntime.Location = new Point(16, 24);
            chkRuntime.Size = new Size(400, 24);
            chkRuntime.CheckedChanged += OnRuntimeToggled;
            gbRuntime.Controls.Add(chkRuntime);

            chkKeepAltTab = new CheckBox();
            chkKeepAltTab.Text = "物理 Alt + Tab 仍触发原生 Alt + Tab";
            chkKeepAltTab.Location = new Point(36, 50);
            chkKeepAltTab.Size = new Size(390, 24);
            chkKeepAltTab.Checked = true;
            gbRuntime.Controls.Add(chkKeepAltTab);

            chkInputSwapped = new CheckBox();
            chkInputSwapped.Text = "输入端已互换 Alt/Ctrl（仅本次开机校准）";
            chkInputSwapped.Location = new Point(36, 76);
            chkInputSwapped.Size = new Size(390, 24);
            chkInputSwapped.Checked = InputCalibration.IsSwapped();
            chkInputSwapped.CheckedChanged += delegate
            {
                bool enabled = chkRuntime.Checked;
                if (enabled) chkRuntime.Checked = false;
                bool saved = InputCalibration.SetSwapped(chkInputSwapped.Checked);
                if (enabled) chkRuntime.Checked = true;
                if (!saved) lblWarn.Text = "校准已用于当前程序，但保存失败；重新打开程序后需再次勾选。";
            };
            gbRuntime.Controls.Add(chkInputSwapped);

            Label lblRtHint = new Label();
            lblRtHint.Text = "右侧 Ctrl / Alt（AltGr）不受影响";
            lblRtHint.ForeColor = Color.Gray;
            lblRtHint.Location = new Point(36, 108);
            lblRtHint.Size = new Size(390, 20);
            gbRuntime.Controls.Add(lblRtHint);

            GroupBox gbSystem = new GroupBox();
            gbSystem.Text = "系统级映射（驱动层，重启后生效，游戏/登录界面也生效）";
            gbSystem.Location = new Point(14, 166);
            gbSystem.Size = new Size(442, 112);
            Controls.Add(gbSystem);

            lblSysState = new Label();
            lblSysState.Text = "状态：检测中…";
            lblSysState.Location = new Point(16, 26);
            lblSysState.Size = new Size(410, 22);
            gbSystem.Controls.Add(lblSysState);

            btnWrite = new Button();
            btnWrite.Text = "写入映射";
            btnWrite.Location = new Point(16, 60);
            btnWrite.Size = new Size(110, 32);
            btnWrite.Click += OnWrite;
            gbSystem.Controls.Add(btnWrite);

            btnRemove = new Button();
            btnRemove.Text = "移除映射";
            btnRemove.Location = new Point(136, 60);
            btnRemove.Size = new Size(110, 32);
            btnRemove.Click += OnRemove;
            gbSystem.Controls.Add(btnRemove);

            lblWarn = new Label();
            lblWarn.Text = "";
            lblWarn.ForeColor = Color.Firebrick;
            lblWarn.Location = new Point(14, 286);
            lblWarn.Size = new Size(442, 42);
            Controls.Add(lblWarn);

            Label lblFoot = new Label();
            lblFoot.Text = "提示：两种方式不要同时开启，否则会二次交换而恢复原状。";
            lblFoot.ForeColor = Color.DimGray;
            lblFoot.Location = new Point(14, 338);
            lblFoot.Size = new Size(442, 20);
            Controls.Add(lblFoot);

            RefreshSystemState();
            var options = StartupOptions.Parse(Environment.GetCommandLineArgs(),
                UserPreferences.Get("Enabled", true), UserPreferences.Get("KeepAltTab", true));
            _wantEnabled = options.Enabled;
            chkKeepAltTab.Checked = options.KeepAltTab;
            _startHidden = options.Tray;
            if (_startHidden) { ShowInTaskbar = false; WindowState = FormWindowState.Minimized; }
            _logKeys = options.LogKeys;
            if (_logKeys)
            {
                _logPath = Path.Combine(Path.GetTempPath(), "keyswap-keys.log");
                _logTimer = new Timer();
                _logTimer.Interval = 500;
                _logTimer.Tick += FlushKeyLog;
                _logTimer.Start();
                _keyLog.Enqueue("=== KeySwap modifier diagnostics " + DateTime.Now + " calibrated=" + chkInputSwapped.Checked + " ===");
            }
            BuildTray();
        }

        private static bool IsSystemMapSet()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(RegPath, false))
                {
                    if (k == null) return false;
                    return k.GetValue(RegName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        private void RefreshSystemState()
        {
            bool set = IsSystemMapSet();
            lblSysState.Text = set
                ? "状态：已设置 — 重启后由驱动层生效"
                : "状态：未设置";
            lblSysState.ForeColor = set ? Color.SeaGreen : Color.Black;
            btnWrite.Enabled = !set;
            btnRemove.Enabled = set;
            UpdateWarning();
        }

        private void UpdateWarning()
        {
            if (chkRuntime.Checked && IsSystemMapSet())
            {
                lblWarn.Text = "警告：系统级映射和运行时互换同时开启，会交换两次等于没交换。请关闭其中一种。";
            }
            else
            {
                lblWarn.Text = "";
            }
        }

        private void OnRuntimeToggled(object sender, EventArgs e)
        {
            if (chkRuntime.Checked)
            {
                _keyboard = new KeyboardHook();
                _keyboard.KeepAltTab = chkKeepAltTab.Checked;
                _keyboard.InputSwapped = chkInputSwapped.Checked;
                if (_logKeys) _keyboard.KeyObserved += LogKey;
                _keyboard.Error += delegate(string error)
                {
                    PostToUi(delegate { chkRuntime.Checked = false; lblWarn.Text = error; });
                };
                if (!_keyboard.Start())
                {
                    chkRuntime.Checked = false;
                    lblWarn.Text = "键盘钩子安装失败，请重新启用。";
                    return;
                }
            }
            else if (_keyboard != null)
            {
                _keyboard.Dispose();
                _keyboard = null;
            }
            if (_settingsLoaded) UserPreferences.Set("Enabled", chkRuntime.Checked);
            UpdateWarning();
            RefreshMenuItemStates();
        }

        private void PostToUi(Action action)
        {
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke(action); } catch (InvalidOperationException) { }
        }

        private void LogKey(Native.KBDLLHOOKSTRUCT info, bool isUp)
        {
            // Diagnostics deliberately exclude letters and other typed content.
            if (info.scanCode != 0x38 && info.scanCode != 0x1D && info.scanCode != 15) return;
            _keyLog.Enqueue(string.Format("{0:HH:mm:ss.fff} vk=0x{1:X2} scan=0x{2:X2} flags=0x{3:X2} {4} {5}",
                DateTime.Now, info.vkCode, info.scanCode, info.flags, isUp ? "UP" : "DOWN",
                (info.flags & 0x10) != 0 ? "injected" : "non-injected"));
        }

        private void FlushKeyLog(object sender, EventArgs e)
        {
            if (_keyLog.Count == 0) return;
            StringBuilder sb = new StringBuilder();
            string line;
            while (_keyLog.TryDequeue(out line)) sb.AppendLine(line);
            try { File.AppendAllText(_logPath, sb.ToString(), Encoding.UTF8); }
            catch { }
        }

        private void OnWrite(object sender, EventArgs e)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(RegPath))
                {
                    k.SetValue(RegName, SwapValue, RegistryValueKind.Binary);
                }
                RefreshSystemState();
                MessageBox.Show(this,
                    "已写入系统级映射。重启后生效。\r\n\r\n注意：系统级映射在驱动层完成，"
                    + "无法保留原生 Alt+Tab 行为。",
                    "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "写入失败（需要管理员权限）：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnRemove(object sender, EventArgs e)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(RegPath, true))
                {
                    if (k != null) k.DeleteValue(RegName, false);
                }
                RefreshSystemState();
                MessageBox.Show(this, "已移除系统级映射。同样需要重启才恢复原始按键。", "完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "移除失败（需要管理员权限）：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            if (_keyboard != null) { _keyboard.Dispose(); _keyboard = null; }
            if (_logTimer != null) { _logTimer.Stop(); _logTimer.Dispose(); FlushKeyLog(null, EventArgs.Empty); }
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            base.OnFormClosing(e);
        }

        private void BuildTray()
        {
            Icon appIcon = null;
            try
            {
                using (Stream s = typeof(MainForm).Assembly.GetManifestResourceStream("KeySwap.ico"))
                {
                    if (s != null) appIcon = new Icon(s);
                }
            }
            catch { }
            if (appIcon == null)
            {
                try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                catch { }
            }
            if (appIcon != null) Icon = appIcon;

            _menu = new ContextMenuStrip();
            _menu.Font = new Font("Microsoft YaHei UI", 9F);

            _miShow = new ToolStripMenuItem("显示主窗口");
            _miShow.Click += delegate { ShowMainWindow(); };
            _menu.Items.Add(_miShow);
            _menu.Items.Add(new ToolStripSeparator());

            _miSwap = new ToolStripMenuItem("交换左侧 Alt / Ctrl");
            _miSwap.Click += delegate { chkRuntime.Checked = !chkRuntime.Checked; };
            _menu.Items.Add(_miSwap);

            _miKeepAltTab = new ToolStripMenuItem("物理 Alt + Tab 保持原生");
            _miKeepAltTab.Click += delegate { chkKeepAltTab.Checked = !chkKeepAltTab.Checked; };
            _menu.Items.Add(_miKeepAltTab);

            _menu.Items.Add(new ToolStripSeparator());

            _miAutoStart = new ToolStripMenuItem("开机自动启动");
            _miAutoStart.Click += delegate { OnToggleAutoStart(); };
            _menu.Items.Add(_miAutoStart);
            _menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { _reallyExit = true; Close(); };
            _menu.Items.Add(miExit);

            _menu.Opening += delegate { RefreshMenuItemStates(); RefreshAutoStart(false); };

            _tray = new NotifyIcon();
            if (appIcon != null) _tray.Icon = appIcon;
            _tray.Text = "KeySwap — Alt / Ctrl 互换";
            _tray.ContextMenuStrip = _menu;
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ShowMainWindow(); };

            chkKeepAltTab.CheckedChanged += delegate
            {
                if (_keyboard != null) _keyboard.KeepAltTab = chkKeepAltTab.Checked;
                if (_settingsLoaded) UserPreferences.Set("KeepAltTab", chkKeepAltTab.Checked);
                RefreshMenuItemStates();
            };
            RefreshMenuItemStates();
        }

        protected override void WndProc(ref Message message)
        {
            if ((uint)message.Msg == Native.ShowMessage) ShowMainWindow();
            base.WndProc(ref message);
        }

        private void ShowMainWindow()
        {
            ShowInTaskbar = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void HideToTray()
        {
            ShowInTaskbar = false;
            Hide();
            if (!_hideTipShown && _tray != null)
            {
                _hideTipShown = true;
                _tray.BalloonTipTitle = "KeySwap 仍在运行";
                _tray.BalloonTipText = "程序已最小化到托盘，右键图标可以开关互换或退出。";
                _tray.ShowBalloonTip(4000);
            }
        }

        private void RefreshMenuItemStates()
        {
            if (_miSwap == null) return;
            _miSwap.Checked = chkRuntime.Checked;
            _miKeepAltTab.Checked = chkKeepAltTab.Checked;
            _miKeepAltTab.Enabled = chkRuntime.Checked;
            _miAutoStart.Checked = _autoStartEnabled;
            _miAutoStart.Enabled = !_autoStartBusy;
            if (_tray != null) _tray.Text = chkRuntime.Checked ? "KeySwap — 互换已启用" : "KeySwap — 互换已暂停";
        }

        private void RefreshAutoStart(bool ensure)
        {
            if (_autoStartBusy) return;
            _autoStartBusy = true;
            RefreshMenuItemStates();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                bool enabled = AutoStartManager.IsEnabled();
                string error = null;
                if (ensure && UserPreferences.Get("AutoStart", true) && !AutoStartManager.IsConfigured())
                {
                    if (AutoStartManager.SetEnabled(true)) { enabled = true; UserPreferences.Set("AutoStart", true); }
                    else error = AutoStartManager.LastError;
                }
                PostToUi(delegate
                {
                    _autoStartEnabled = enabled;
                    _autoStartBusy = false;
                    if (error != null) lblWarn.Text = "开机启动设置失败：" + error;
                    RefreshMenuItemStates();
                });
            });
        }

        private void OnToggleAutoStart()
        {
            if (_autoStartBusy) return;
            bool wanted = !_autoStartEnabled;
            _autoStartBusy = true;
            RefreshMenuItemStates();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                bool success = AutoStartManager.SetEnabled(wanted);
                string error = AutoStartManager.LastError;
                if (success) UserPreferences.Set("AutoStart", wanted);
                PostToUi(delegate
                {
                    _autoStartBusy = false;
                    if (success) _autoStartEnabled = wanted;
                    else lblWarn.Text = "开机启动设置失败：" + error;
                    RefreshMenuItemStates();
                });
            });
        }

        private float _appliedScale = 1f;

        // 用系统 API 取真实 DPI，WinForms 的 DeviceDpi 在部分配置下会返回 96
        private void ApplyCurrentDpi()
        {
            uint dpi = Native.GetDpiForWindow(Handle);
            if (dpi == 0) return;

            float target = dpi / 96f;
            float factor = target / _appliedScale;
            if (Math.Abs(factor - 1f) < 0.01f) return;

            // 只缩放控件几何尺寸；字体点数本身已随 DPI 生效，再乘会双重放大
            Scale(new SizeF(factor, factor));
            _appliedScale = target;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplyCurrentDpi();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _settingsLoaded = true;
            chkRuntime.Checked = _wantEnabled;
            if (_startHidden) Hide();
            RefreshAutoStart(true);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            ApplyCurrentDpi();
        }
    }
}
