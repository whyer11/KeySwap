using System;
using System.Diagnostics;
using System.IO;
using KeySwap.Installation;

internal static class InstallSecurityTests
{
    private static int failures;

    private static int Main()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "KeySwap-install-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            string low = Path.Combine(fixture, "low");
            string normal = Path.Combine(fixture, "normal");
            Directory.CreateDirectory(low);
            Directory.CreateDirectory(normal);
            ProcessStartInfo acl = new ProcessStartInfo("icacls.exe", "\"" + low + "\" /setintegritylevel (OI)(CI)L");
            acl.UseShellExecute = false;
            acl.CreateNoWindow = true;
            acl.RedirectStandardOutput = true;
            acl.RedirectStandardError = true;
            using (Process process = Process.Start(acl))
            {
                string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new Exception("Could not create isolated Low integrity fixture: " + output);
            }
            string source = Path.Combine(low, "source.exe");
            byte[] payload = new byte[100000];
            for (int index = 0; index < payload.Length; index++) payload[index] = (byte)(index * 37);
            File.WriteAllBytes(source, payload);

            Test("low integrity destination is refused without creating a child", delegate {
                string child = Path.Combine(low, "must-not-exist");
                Reject(delegate { InstallSecurity.ValidateDirectory(child); });
                Check(!Directory.Exists(child), "rejected destination was nevertheless created");
            });
            Test("a Low source is staged into a fresh Medium file with identical bytes", delegate {
                Check(InstallSecurity.GetFileIntegrityRid(source) == 4096, "source fixture did not inherit its Low label");
                string staged = InstallSecurity.StageExecutable(source, normal);
                Check(File.Exists(staged), "staged file missing");
                Check(InstallSecurity.GetFileIntegrityRid(staged) >= 8192, "source Low label leaked into staged file");
                Check(InstallSecurity.Sha256(source) == InstallSecurity.Sha256(staged), "staging changed executable bytes");
                Check(File.ReadAllBytes(staged).Length == payload.Length, "staging truncated the source");
            });
            Test("staging refuses a Low destination before copying executable bytes", delegate {
                int count = Directory.GetFiles(low).Length;
                Reject(delegate { InstallSecurity.StageExecutable(source, low); });
                Check(Directory.GetFiles(low).Length == count, "rejected staging left another executable in Low directory");
            });
            Test("fresh staging never overwrites an existing deployment", delegate {
                string deployed = Path.Combine(normal, "KeySwap.exe");
                File.WriteAllText(deployed, "existing deployment");
                string first = InstallSecurity.StageExecutable(source, normal);
                string second = InstallSecurity.StageExecutable(source, normal);
                Check(first != second && first != deployed && second != deployed, "staging reused an existing name");
                Check(File.ReadAllText(deployed) == "existing deployment", "staging changed existing deployment");
            });
            Test("relative, UNC, device, and alternate-stream paths are refused", delegate {
                foreach (string path in new string[] { "relative\\KeySwap", "C:relative", @"\\server\share\KeySwap", @"\\?\C:\KeySwap", normal + ":stream" })
                    Reject(delegate { InstallSecurity.ValidatePath(path, false); });
            });
            Test("missing required source is refused", delegate {
                Reject(delegate { InstallSecurity.ValidatePath(Path.Combine(normal, "missing.exe"), true); });
            });
            Test("directory junction is refused rather than followed", delegate {
                string junction = Path.Combine(fixture, "redirect");
                // Junction creation affects only this isolated test directory.
                ProcessStartInfo create = new ProcessStartInfo("cmd.exe", "/d /c mklink /J \"" + junction + "\" \"" + normal + "\"");
                create.UseShellExecute = false;
                create.CreateNoWindow = true;
                create.RedirectStandardOutput = true;
                create.RedirectStandardError = true;
                using (Process process = Process.Start(create))
                {
                    string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode != 0) throw new Exception("Could not create isolated junction fixture: " + output);
                }
                try { Reject(delegate { InstallSecurity.ValidateDirectory(Path.Combine(junction, "new")); }); }
                finally { Directory.Delete(junction); }
                Check(!Directory.Exists(Path.Combine(normal, "new")), "validation followed the junction");
            });
        }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL fixture: " + ex); }
        finally
        {
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullFixture = Path.GetFullPath(fixture);
            if (fullFixture.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullFixture)) Directory.Delete(fullFixture, true);
        }
        Console.WriteLine("Install security failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    private static void Test(string name, Action action)
    {
        try { action(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Reject(Action action)
    {
        bool rejected = false;
        try { action(); }
        catch (ArgumentException) { rejected = true; }
        catch (IOException) { rejected = true; }
        catch (UnauthorizedAccessException) { rejected = true; }
        if (!rejected) throw new Exception("unsafe path was accepted");
    }
}
