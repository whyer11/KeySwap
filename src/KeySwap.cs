using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeySwap
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal static class Native
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;
        public const uint VK_LMENU = 0xA4;
        public const uint VK_LCONTROL = 0xA2;
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

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public KEYBDINPUT ki;
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

        public static void SendScan(ushort scan, bool up)
        {
            INPUT inp = new INPUT();
            inp.type = INPUT_KEYBOARD;
            inp.ki.wVk = 0;
            inp.ki.wScan = scan;
            inp.ki.dwFlags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0);
            inp.ki.time = 0;
            inp.ki.dwExtraInfo = MAGIC;
            INPUT[] arr = new INPUT[1];
            arr[0] = inp;
            SendInput(1, arr, Marshal.SizeOf(typeof(INPUT)));
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
        private readonly Label lblSysState;
        private readonly Label lblWarn;
        private readonly Button btnWrite;
        private readonly Button btnRemove;
        private IntPtr _hookId = IntPtr.Zero;
        private Native.LowLevelKeyboardProc _proc;

        public MainForm()
        {
            Text = "Alt / Ctrl 互换控制";
            ClientSize = new Size(470, 300);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);

            GroupBox gbRuntime = new GroupBox();
            gbRuntime.Text = "运行时互换（立即生效，仅本程序运行期间）";
            gbRuntime.Location = new Point(14, 12);
            gbRuntime.Size = new Size(442, 82);
            Controls.Add(gbRuntime);

            chkRuntime = new CheckBox();
            chkRuntime.Text = "交换左侧 Alt 与左侧 Ctrl";
            chkRuntime.Location = new Point(16, 24);
            chkRuntime.Size = new Size(400, 24);
            chkRuntime.CheckedChanged += OnRuntimeToggled;
            gbRuntime.Controls.Add(chkRuntime);

            Label lblRtHint = new Label();
            lblRtHint.Text = "右侧 Ctrl / Alt（AltGr）不受影响";
            lblRtHint.ForeColor = Color.Gray;
            lblRtHint.Location = new Point(36, 50);
            lblRtHint.Size = new Size(390, 20);
            gbRuntime.Controls.Add(lblRtHint);

            GroupBox gbSystem = new GroupBox();
            gbSystem.Text = "系统级映射（驱动层，重启后生效，游戏/登录界面也生效）";
            gbSystem.Location = new Point(14, 104);
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
            lblWarn.Location = new Point(14, 226);
            lblWarn.Size = new Size(442, 40);
            Controls.Add(lblWarn);

            Label lblFoot = new Label();
            lblFoot.Text = "提示：两种方式不要同时开启，否则会二次交换而恢复原状。";
            lblFoot.ForeColor = Color.DimGray;
            lblFoot.Location = new Point(14, 268);
            lblFoot.Size = new Size(442, 20);
            Controls.Add(lblFoot);

            _proc = HookProc;
            RefreshSystemState();
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
                if (!InstallHook())
                {
                    chkRuntime.Checked = false;
                    MessageBox.Show(this, "键盘钩子安装失败，无法启用运行时互换。", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            else
            {
                UninstallHook();
            }
            UpdateWarning();
        }

        private bool InstallHook()
        {
            if (_hookId != IntPtr.Zero) return true;
            using (Process cur = Process.GetCurrentProcess())
            using (ProcessModule mod = cur.MainModule)
            {
                _hookId = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc,
                    Native.GetModuleHandle(mod.ModuleName), 0);
            }
            return _hookId != IntPtr.Zero;
        }

        private void UninstallHook()
        {
            if (_hookId == IntPtr.Zero) return;
            Native.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _hookId != IntPtr.Zero)
            {
                Native.KBDLLHOOKSTRUCT info = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(
                    lParam, typeof(Native.KBDLLHOOKSTRUCT));

                if (info.dwExtraInfo != Native.MAGIC)
                {
                    int msg = wParam.ToInt32();
                    bool isUp = (msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP);

                    if (info.vkCode == Native.VK_LMENU)
                    {
                        Native.SendScan(Native.SC_LEFTCTRL, isUp);
                        return (IntPtr)1;
                    }
                    if (info.vkCode == Native.VK_LCONTROL)
                    {
                        Native.SendScan(Native.SC_LEFTALT, isUp);
                        return (IntPtr)1;
                    }
                }
            }
            return Native.CallNextHookEx(_hookId, nCode, wParam, lParam);
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
                MessageBox.Show(this, "已写入系统级映射。重启后生效。", "完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            UninstallHook();
            base.OnFormClosing(e);
        }
    }
}
