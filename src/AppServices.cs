using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

namespace KeySwap
{
    internal sealed class StartupOptions
    {
        public bool Enabled { get; private set; }
        public bool KeepAltTab { get; private set; }
        public bool BridgeEnabled { get; private set; }
        public bool Tray { get; private set; }
        public bool LogKeys { get; private set; }
        public static StartupOptions Parse(string[] args, bool savedEnabled, bool savedKeepAltTab, bool savedBridge = false)
        {
            StartupOptions options = new StartupOptions();
            options.Enabled = savedEnabled;
            options.KeepAltTab = savedKeepAltTab;
            options.BridgeEnabled = savedBridge;
            if (args == null) return options;
            foreach (string arg in args)
            {
                if (string.Equals(arg, "--enable", StringComparison.OrdinalIgnoreCase)) options.Enabled = true;
                else if (string.Equals(arg, "--disable", StringComparison.OrdinalIgnoreCase)) options.Enabled = false;
                else if (string.Equals(arg, "--no-alttab", StringComparison.OrdinalIgnoreCase)) options.KeepAltTab = false;
                else if (string.Equals(arg, "--bridge", StringComparison.OrdinalIgnoreCase)) options.BridgeEnabled = true;
                else if (string.Equals(arg, "--no-bridge", StringComparison.OrdinalIgnoreCase)) options.BridgeEnabled = false;
                else if (string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase)) options.Tray = true;
                else if (string.Equals(arg, "--logkeys", StringComparison.OrdinalIgnoreCase)) options.LogKeys = true;
            }
            return options;
        }
    }

    internal static class UserPreferences
    {
        private const string KeyPath = @"Software\KeySwap";

        public static bool Get(string name, bool fallback)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                {
                    object value = key == null ? null : key.GetValue(name);
                    return value == null ? fallback : Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
                }
            }
            catch { return fallback; }
        }

        public static bool Set(string name, bool value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (key == null) return false;
                    key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
                    return true;
                }
            }
            catch { return false; }
        }
    }

    internal static class AutoStartManager
    {
        private const string TaskName = "KeySwap";
        private const string TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        public static string LastError { get; private set; }

        // Call these methods on a worker; Task Scheduler RPC must never block the hook/UI loop.
        public static bool IsEnabled() { return ReadTask(false); }
        public static bool IsConfigured() { return ReadTask(true); }

        private static bool ReadTask(bool validate)
        {
            object service = null;
            object folder = null;
            object task = null;
            try
            {
                service = Connect();
                folder = Invoke(service, "GetFolder", BindingFlags.InvokeMethod, "\\");
                task = Invoke(folder, "GetTask", BindingFlags.InvokeMethod, TaskName);
                if (!(bool)Invoke(task, "Enabled", BindingFlags.GetProperty)) return false;
                return !validate || IsTaskConfigured((string)Invoke(task, "Xml", BindingFlags.GetProperty),
                    Application.ExecutablePath, CurrentSid());
            }
            catch { return false; }
            finally { Release(task); Release(folder); Release(service); }
        }

        public static bool SetEnabled(bool enabled)
        {
            object service = null;
            object folder = null;
            object task = null;
            LastError = null;
            try
            {
                service = Connect();
                folder = Invoke(service, "GetFolder", BindingFlags.InvokeMethod, "\\");
                if (enabled)
                {
                    string sid = CurrentSid();
                    task = Invoke(folder, "RegisterTask", BindingFlags.InvokeMethod, TaskName,
                        BuildTaskXml(Application.ExecutablePath, sid), 6, sid, null, 3, null);
                }
                else
                {
                    try { Invoke(folder, "DeleteTask", BindingFlags.InvokeMethod, TaskName, 0); }
                    catch (Exception ex)
                    {
                        Exception cause = Unwrap(ex);
                        // Removing an already absent task is success.
                        if (Marshal.GetHRForException(cause) != unchecked((int)0x80070002)) throw;
                    }
                }
                return true;
            }
            catch (Exception ex) { LastError = Unwrap(ex).Message; return false; }
            finally { Release(task); Release(folder); Release(service); }
        }

        internal static string BuildTaskXml(string executable, string sid)
        {
            StringBuilder text = new StringBuilder();
            XmlWriterSettings settings = new XmlWriterSettings();
            settings.OmitXmlDeclaration = true;
            using (XmlWriter writer = XmlWriter.Create(text, settings))
            {
                writer.WriteStartElement("Task", TaskNamespace);
                writer.WriteAttributeString("version", "1.2");
                writer.WriteStartElement("Triggers");
                writer.WriteStartElement("LogonTrigger");
                writer.WriteElementString("Enabled", "true");
                writer.WriteElementString("UserId", sid);
                writer.WriteEndElement(); writer.WriteEndElement();
                writer.WriteStartElement("Principals");
                writer.WriteStartElement("Principal"); writer.WriteAttributeString("id", "Author");
                writer.WriteElementString("UserId", sid);
                writer.WriteElementString("LogonType", "InteractiveToken");
                writer.WriteElementString("RunLevel", "HighestAvailable");
                writer.WriteEndElement(); writer.WriteEndElement();
                writer.WriteStartElement("Settings");
                writer.WriteElementString("MultipleInstancesPolicy", "IgnoreNew");
                writer.WriteElementString("DisallowStartIfOnBatteries", "false");
                writer.WriteElementString("StopIfGoingOnBatteries", "false");
                writer.WriteElementString("AllowHardTerminate", "false");
                writer.WriteElementString("StartWhenAvailable", "true");
                writer.WriteElementString("Enabled", "true");
                writer.WriteElementString("ExecutionTimeLimit", "PT0S");
                writer.WriteEndElement();
                writer.WriteStartElement("Actions"); writer.WriteAttributeString("Context", "Author");
                writer.WriteStartElement("Exec");
                writer.WriteElementString("Command", executable);
                writer.WriteElementString("Arguments", "--tray");
                writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
            }
            return text.ToString();
        }

        internal static bool IsTaskConfigured(string xml, string executable, string sid)
        {
            try
            {
                XmlDocument doc = new XmlDocument(); doc.XmlResolver = null; doc.LoadXml(xml);
                XmlNamespaceManager ns = new XmlNamespaceManager(doc.NameTable); ns.AddNamespace("t", TaskNamespace);
                return string.Equals(Value(doc, ns, "//t:Exec/t:Command").Trim('"'), executable, StringComparison.OrdinalIgnoreCase)
                    && Value(doc, ns, "//t:Exec/t:Arguments") == "--tray"
                    && Value(doc, ns, "//t:Principal/t:UserId") == sid
                    && Value(doc, ns, "//t:Principal/t:LogonType") == "InteractiveToken"
                    && Value(doc, ns, "//t:Principal/t:RunLevel") == "HighestAvailable"
                    && Value(doc, ns, "//t:LogonTrigger/t:UserId") == sid
                    && Value(doc, ns, "//t:LogonTrigger/t:Enabled") != "false"
                    && Value(doc, ns, "//t:Settings/t:Enabled") != "false"
                    && Value(doc, ns, "//t:Settings/t:DisallowStartIfOnBatteries") == "false"
                    && Value(doc, ns, "//t:Settings/t:StopIfGoingOnBatteries") == "false"
                    && Value(doc, ns, "//t:Settings/t:ExecutionTimeLimit") == "PT0S";
            }
            catch { return false; }
        }

        private static string Value(XmlDocument doc, XmlNamespaceManager ns, string path)
        {
            XmlNode node = doc.SelectSingleNode(path, ns);
            return node == null ? "" : node.InnerText;
        }

        private static string CurrentSid()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) return identity.User.Value;
        }

        private static object Connect()
        {
            object service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
            try { Invoke(service, "Connect", BindingFlags.InvokeMethod); return service; }
            catch { Release(service); throw; }
        }

        private static object Invoke(object target, string member, BindingFlags flags, params object[] args)
        {
            return target.GetType().InvokeMember(member, flags, null, target, args, CultureInfo.InvariantCulture);
        }

        private static Exception Unwrap(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
    }
}
