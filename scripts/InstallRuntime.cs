using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace KeySwap.Installation
{
    public sealed class ProcessIdentity
    {
        public int Id;
        public string Path;
        public uint SessionId;
        public long CreationTime;
        public string UserSid;
        public int IntegrityRid;
    }

    public static class InstallRuntime
    {
        private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
        private const uint QueryAndSynchronize = 0x00101000;

        public static ProcessIdentity Inspect(int processId)
        {
            IntPtr process = OpenProcess(QueryAndSynchronize, false, processId);
            if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return InspectHandle(process, processId); }
            finally { CloseHandle(process); }
        }

        // The held process handle and creation time prevent acting on a reused PID.
        // Only this process's top-level windows receive the registered message.
        public static int RequestQuit(ProcessIdentity expected)
        {
            IntPtr process = OpenProcess(QueryAndSynchronize, false, expected.Id);
            if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                ProcessIdentity actual = InspectHandle(process, expected.Id);
                if (!Same(actual, expected)) throw new InvalidOperationException("Process identity changed before shutdown.");
                uint message = RegisterWindowMessage("KeySwap.Quit");
                if (message == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                int sent = 0;
                WindowCallback callback = delegate(IntPtr window, IntPtr parameter)
                {
                    uint owner;
                    GetWindowThreadProcessId(window, out owner);
                    if (owner == (uint)expected.Id && WaitForSingleObject(process, 0) == 258)
                    {
                        if (PostMessage(window, message, IntPtr.Zero, IntPtr.Zero)) sent++;
                    }
                    return true;
                };
                EnumWindows(callback, IntPtr.Zero);
                GC.KeepAlive(callback);
                return sent;
            }
            finally { CloseHandle(process); }
        }

        public static bool Same(ProcessIdentity left, ProcessIdentity right)
        {
            return left.Id == right.Id && left.CreationTime == right.CreationTime
                && left.SessionId == right.SessionId && left.UserSid == right.UserSid
                && string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
        }

        public static bool HasTrayIcon(ProcessIdentity expected)
        {
            if (!Same(Inspect(expected.Id), expected)) throw new InvalidOperationException("Process identity changed during tray validation.");
            bool present = false;
            WindowCallback callback = delegate(IntPtr window, IntPtr parameter)
            {
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner != (uint)expected.Id) return true;
                // KeySwap creates one NotifyIcon (ID 1). The small additional range
                // allows WinForms to recreate it without probing another process.
                for (uint id = 1; id <= 16; id++)
                {
                    IconIdentifier identity = new IconIdentifier { Size = (uint)Marshal.SizeOf(typeof(IconIdentifier)), Window = window, Id = id };
                    Rectangle rectangle;
                    if (Shell_NotifyIconGetRect(ref identity, out rectangle) == 0) { present = true; return false; }
                }
                return true;
            };
            EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return present;
        }

        private static ProcessIdentity InspectHandle(IntPtr process, int processId)
        {
            if (WaitForSingleObject(process, 0) != 258) throw new InvalidOperationException("Process has exited.");
            StringBuilder path = new StringBuilder(32768);
            int length = path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            uint session;
            if (!ProcessIdToSessionId((uint)processId, out session)) throw new Win32Exception(Marshal.GetLastWin32Error());
            long created, exited, kernel, user;
            if (!GetProcessTimes(process, out created, out exited, out kernel, out user)) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr token;
            if (!OpenProcessToken(process, 8, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                string integrity = TokenSid(token, 25);
                return new ProcessIdentity { Id = processId, Path = path.ToString(), SessionId = session,
                    CreationTime = created, UserSid = TokenSid(token, 1),
                    IntegrityRid = int.Parse(integrity.Substring(integrity.LastIndexOf('-') + 1)) };
            }
            finally { CloseHandle(token); }
        }

        private static string TokenSid(IntPtr token, int informationClass)
        {
            int length;
            GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out length);
            if (length <= 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, informationClass, buffer, length, out length)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        [StructLayout(LayoutKind.Sequential)] private struct IconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
        [StructLayout(LayoutKind.Sequential)] private struct Rectangle { public int Left, Top, Right, Bottom; }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint processId, out uint session);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr buffer, int length, out int required);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier, out Rectangle rectangle);
    }
}
