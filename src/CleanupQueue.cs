using System;
using System.Collections.Generic;

namespace KeySwap
{
    // Worker-thread-only cleanup. Inputs are the owned key-ups returned by
    // KeyboardState.ReleaseAll, never guesses about global keyboard state.
    internal sealed class CleanupQueue
    {
        private readonly List<ScanEvent> pending = new List<ScanEvent>();
        public CleanupQueue() { }

        public bool TryRelease(ScanEvent[] releases, Func<ScanEvent[], bool> send)
        {
            foreach (ScanEvent release in releases)
            {
                bool alreadyPending = false;
                foreach (ScanEvent existing in pending)
                    if (existing.Scan == release.Scan && existing.Extended == release.Extended)
                    {
                        alreadyPending = true;
                        break;
                    }
                if (!alreadyPending) pending.Add(release);
            }
            if (pending.Count == 0) return true;
            // A failed batch may have inserted only a prefix. Keep every owned
            // key-up for the next lifecycle attempt; repeating key-up is safe.
            if (!send(pending.ToArray())) return false;
            pending.Clear();
            return true;
        }
    }
}
