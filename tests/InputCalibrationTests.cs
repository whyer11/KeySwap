using System;
using System.Management;
using KeySwap;

internal static class InputCalibrationTests
{
    private static int passed;
    private static int failed;

    private static int Main()
    {
        Test("enabled correction is restored during the same boot", delegate {
            Assert(InputCalibration.ApplyForBoot(true, "20261030090000.000000+480", "20261030090000.000000+480"), "same-boot correction was lost");
        });
        Test("disabled correction remains disabled during the same boot", delegate {
            Assert(!InputCalibration.ApplyForBoot(false, "20261030090000.000000+480", "20261030090000.000000+480"), "disabled correction was restored");
        });
        Test("a new boot invalidates the old correction", delegate {
            Assert(!InputCalibration.ApplyForBoot(true, "20261030090000.000000+480", "20261031090000.000000+480"), "old correction survived a reboot");
        });
        Test("missing saved boot never restores correction", delegate {
            Assert(!InputCalibration.ApplyForBoot(true, null, "20261030090000.000000+480"), "null saved boot was accepted");
            Assert(!InputCalibration.ApplyForBoot(true, "", "20261030090000.000000+480"), "empty saved boot was accepted");
            Assert(!InputCalibration.ApplyForBoot(true, "   ", "20261030090000.000000+480"), "blank saved boot was accepted");
        });
        Test("unknown current boot never restores correction", delegate {
            Assert(!InputCalibration.ApplyForBoot(true, "20261030090000.000000+480", null), "null current boot was accepted");
            Assert(!InputCalibration.ApplyForBoot(true, "20261030090000.000000+480", ""), "empty current boot was accepted");
            Assert(!InputCalibration.ApplyForBoot(true, "", ""), "two missing boot tokens were treated as a match");
            Assert(!InputCalibration.ApplyForBoot(true, "   ", "   "), "two blank boot tokens were treated as a match");
        });
        Test("real Windows boot token is readable and stable", delegate {
            string first = InputCalibration.CurrentBootToken;
            Assert(!string.IsNullOrEmpty(first), "Windows boot time could not be read");
            DateTime boot = ManagementDateTimeConverter.ToDateTime(first);
            Assert(boot <= DateTime.Now && boot > new DateTime(2000, 1, 1), "boot time is outside the expected range");
            Assert(first == InputCalibration.CurrentBootToken, "cached boot token changed");
        });
        Console.WriteLine("Calibration: {0} passed, {1} failed", passed, failed);
        return failed == 0 ? 0 : 1;
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
}
