using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml;
using KeySwap;

internal static class AppServicesTests
{
    private static int passed;
    private static int failed;

    private static int Main()
    {
        Test("tray launch restores saved disabled state", delegate {
            StartupOptions value = StartupOptions.Parse(new string[] { "--tray" }, false, false);
            Assert(!value.Enabled && !value.KeepAltTab && value.Tray, "saved preferences were ignored");
        });
        Test("first launch uses supplied enabled defaults", delegate {
            StartupOptions value = StartupOptions.Parse(new string[0], true, true);
            Assert(value.Enabled && value.KeepAltTab && !value.Tray && !value.LogKeys, "defaults were ignored");
        });
        Test("explicit flags override saved preferences", delegate {
            StartupOptions value = StartupOptions.Parse(new string[] { "KeySwap.exe", "--ENABLE", "--NO-ALTTAB", "--TRAY", "--LOGKEYS" }, false, true);
            Assert(value.Enabled && !value.KeepAltTab && value.Tray && value.LogKeys, "command-line overrides were lost");
        });
        Test("last enable or disable argument wins", delegate {
            Assert(!StartupOptions.Parse(new string[] { "--enable", "--disable" }, true, true).Enabled, "disable ignored");
            Assert(StartupOptions.Parse(new string[] { "--disable", "--enable" }, false, true).Enabled, "enable ignored");
        });
        Test("autostart task preserves settings and runs indefinitely on battery", delegate {
            string xml = AutoStartManager.BuildTaskXml(@"C:\Apps & Tools\KeySwap.exe", "S-1-5-21-1234");
            XmlDocument doc = new XmlDocument(); doc.LoadXml(xml);
            Assert(Text(doc, "//t:Exec/t:Command") == @"C:\Apps & Tools\KeySwap.exe", "executable path did not round-trip");
            Assert(Text(doc, "//t:Exec/t:Arguments") == "--tray", "task forced runtime preference on");
            Assert(Text(doc, "//t:Settings/t:DisallowStartIfOnBatteries") == "false", "battery launch blocked");
            Assert(Text(doc, "//t:Settings/t:StopIfGoingOnBatteries") == "false", "battery switch stops remapping");
            Assert(Text(doc, "//t:Settings/t:ExecutionTimeLimit") == "PT0S", "72h task expiry retained");
            Assert(Text(doc, "//t:LogonTrigger/t:UserId") == "S-1-5-21-1234", "trigger runs for other users");
            Assert(Text(doc, "//t:Principal/t:LogonType") == "InteractiveToken", "task is not interactive");
            Assert(Text(doc, "//t:Principal/t:RunLevel") == "HighestAvailable", "task cannot remap elevated applications");
            Assert(AutoStartManager.IsTaskConfigured(xml, @"C:\Apps & Tools\KeySwap.exe", "S-1-5-21-1234"), "new task is not recognized");
        });
        Test("outdated task settings and disabled tasks need repair", delegate {
            string xml = AutoStartManager.BuildTaskXml(@"C:\KeySwap.exe", "S-1-5-21-1234");
            Assert(!AutoStartManager.IsTaskConfigured(xml.Replace("--tray", "--enable --tray"), @"C:\KeySwap.exe", "S-1-5-21-1234"), "forced enable not repaired");
            Assert(!AutoStartManager.IsTaskConfigured(xml.Replace("PT0S", "PT72H"), @"C:\KeySwap.exe", "S-1-5-21-1234"), "72h expiry not repaired");
            Assert(!AutoStartManager.IsTaskConfigured(xml.Replace("<Enabled>true</Enabled>", "<Enabled>false</Enabled>"), @"C:\KeySwap.exe", "S-1-5-21-1234"), "disabled task accepted");
            Assert(!AutoStartManager.IsTaskConfigured(xml, @"D:\KeySwap.exe", "S-1-5-21-1234"), "obsolete executable accepted");
            Assert(!AutoStartManager.IsTaskConfigured(xml, @"C:\KeySwap.exe", "S-1-5-21-5678"), "wrong user accepted");
            Assert(!AutoStartManager.IsTaskConfigured("not xml", @"C:\KeySwap.exe", "S-1-5-21-1234"), "malformed task accepted");
        });
        Test("Windows accepts task XML without registering a task", delegate {
            object service = null, definition = null, settings = null;
            try
            {
                service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
                Call(service, "Connect", BindingFlags.InvokeMethod);
                definition = Call(service, "NewTask", BindingFlags.InvokeMethod, 0);
                string sid;
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) sid = identity.User.Value;
                Call(definition, "XmlText", BindingFlags.SetProperty, AutoStartManager.BuildTaskXml(typeof(AppServicesTests).Assembly.Location, sid));
                settings = Call(definition, "Settings", BindingFlags.GetProperty);
                Assert(!(bool)Call(settings, "DisallowStartIfOnBatteries", BindingFlags.GetProperty), "Windows retained battery launch restriction");
                Assert(!(bool)Call(settings, "StopIfGoingOnBatteries", BindingFlags.GetProperty), "Windows retained battery stop restriction");
                Assert((string)Call(settings, "ExecutionTimeLimit", BindingFlags.GetProperty) == "PT0S", "Windows retained time limit");
            }
            finally
            {
                if (settings != null) Marshal.FinalReleaseComObject(settings);
                if (definition != null) Marshal.FinalReleaseComObject(definition);
                if (service != null) Marshal.FinalReleaseComObject(service);
            }
        });
        Console.WriteLine("Services: {0} passed, {1} failed", passed, failed);
        return failed == 0 ? 0 : 1;
    }

    private static string Text(XmlDocument doc, string path)
    {
        XmlNamespaceManager ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
        XmlNode node = doc.SelectSingleNode(path, ns);
        return node == null ? "" : node.InnerText;
    }

    private static void Test(string name, Action body)
    {
        try { body(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static object Call(object target, string name, BindingFlags flags, params object[] args)
    {
        return target.GetType().InvokeMember(name, flags, null, target, args);
    }
}
