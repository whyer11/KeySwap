using System;
using System.Collections.Generic;
using KeySwap;

internal static class CleanupQueueTests
{
    private static int failed;
    private static int passed;

    private static int Main()
    {
        Test("failed and partial cleanup releases survive until a successful retry", delegate {
            CleanupQueue queue = new CleanupQueue();
            ScanEvent[] releases = new ScanEvent[] { new ScanEvent(0x5B, true, true), new ScanEvent(0x38, true) };
            HashSet<int> down = new HashSet<int> { 0x1005B, 0x38 };
            int attempts = 0;
            Func<ScanEvent[], bool> send = delegate(ScanEvent[] batch) {
                attempts++;
                Same(batch, releases);
                // The first send fails completely, the second inserts a prefix.
                if (attempts == 1) return false;
                down.Remove(Id(batch[0]));
                if (attempts == 2) return false;
                foreach (ScanEvent key in batch) down.Remove(Id(key));
                return true;
            };
            Assert(!queue.TryRelease(releases, send) && attempts == 1, "initial cleanup failure was not bounded");
            Assert(!queue.TryRelease(new ScanEvent[0], send) && attempts == 2, "failed cleanup was lost or retried in a loop");
            Assert(queue.TryRelease(new ScanEvent[0], send) && attempts == 3, "stop/finally could not retry the retained release");
            Assert(down.Count == 0, "owned Windows key or Alt remained down");
            Assert(queue.TryRelease(new ScanEvent[0], send) && attempts == 3, "successful cleanup was unnecessarily resent");
        });
        Test("new owned releases merge without duplicates and preserve extended identity", delegate {
            CleanupQueue queue = new CleanupQueue();
            ScanEvent leftAlt = new ScanEvent(0x38, true);
            ScanEvent rightAlt = new ScanEvent(0x38, true, true);
            ScanEvent win = new ScanEvent(0x5B, true, true);
            Assert(!queue.TryRelease(new ScanEvent[] { leftAlt, win }, delegate { return false; }), "fixture should retain releases");
            Assert(queue.TryRelease(new ScanEvent[] { leftAlt, rightAlt }, delegate(ScanEvent[] batch) {
                Same(batch, new ScanEvent[] { leftAlt, win, rightAlt });
                return true;
            }), "merged cleanup failed");
        });
        Test("empty cleanup never releases unrelated keys", delegate {
            CleanupQueue queue = new CleanupQueue();
            bool called = false;
            Assert(queue.TryRelease(new ScanEvent[0], delegate { called = true; return true; }), "empty cleanup failed");
            Assert(!called, "cleanup emitted releases without any owned keys");
        });
        Console.WriteLine("Cleanup: {0} passed, {1} failed", passed, failed);
        return failed == 0 ? 0 : 1;
    }

    private static int Id(ScanEvent key) { return key.Scan | (key.Extended ? 0x10000 : 0); }
    private static void Same(ScanEvent[] actual, ScanEvent[] expected)
    {
        Assert(actual.Length == expected.Length, "cleanup lost or invented release events");
        for (int index = 0; index < actual.Length; index++)
            Assert(actual[index].Up && Id(actual[index]) == Id(expected[index]), "cleanup changed a release identity");
    }
    private static void Test(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
