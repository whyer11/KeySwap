using System;
using System.Management;
using Microsoft.Win32;

namespace KeySwap
{
    internal static class InputCalibration
    {
        private const string RegistryPath = @"Software\KeySwap";
        private const string EnabledName = "InputSwapped";
        private const string BootName = "InputSwapBoot";
        private static readonly object StateLock = new object();
        private static readonly Lazy<string> BootToken = new Lazy<string>(ReadBootToken);

        // WMI is consulted once per process. An unavailable token stays unavailable,
        // so a stale registry correction is never restored on an unidentified boot.
        internal static string CurrentBootToken { get { return BootToken.Value; } }

        public static bool IsSwapped()
        {
            string current = CurrentBootToken;
            lock (StateLock)
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath, false))
                    {
                        if (key == null) return false;
                        object enabled = key.GetValue(EnabledName);
                        string saved = key.GetValue(BootName) as string;
                        return ApplyForBoot(enabled is int && (int)enabled == 1, saved, current);
                    }
                }
                catch { return false; }
            }
        }

        public static bool SetSwapped(bool enabled)
        {
            string current = CurrentBootToken;
            if (enabled && string.IsNullOrWhiteSpace(current)) return false;
            lock (StateLock)
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath))
                    {
                        if (key == null) return false;
                        // Disable first so a partial write cannot enable stale calibration.
                        key.SetValue(EnabledName, 0, RegistryValueKind.DWord);
                        key.SetValue(BootName, current, RegistryValueKind.String);
                        if (enabled) key.SetValue(EnabledName, 1, RegistryValueKind.DWord);
                        return true;
                    }
                }
                catch { return false; }
            }
        }

        internal static bool ApplyForBoot(bool enabled, string saved, string current)
        {
            return enabled && !string.IsNullOrWhiteSpace(saved) && !string.IsNullOrWhiteSpace(current)
                && string.Equals(saved, current, StringComparison.Ordinal);
        }

        private static string ReadBootToken()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    @"root\CIMV2", "SELECT LastBootUpTime FROM Win32_OperatingSystem"))
                {
                    searcher.Options.Timeout = TimeSpan.FromSeconds(3);
                    using (ManagementObjectCollection values = searcher.Get())
                    {
                        foreach (ManagementBaseObject value in values)
                        {
                            using (value)
                            {
                                string token = value["LastBootUpTime"] as string;
                                if (string.IsNullOrWhiteSpace(token)) continue;
                                ManagementDateTimeConverter.ToDateTime(token);
                                return token;
                            }
                        }
                    }
                }
            }
            catch { }
            return string.Empty;
        }
    }
}
