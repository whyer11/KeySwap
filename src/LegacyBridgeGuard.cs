using System;
using System.Threading;

namespace KeySwap
{
    // Legacy hosts exit whenever this named object exists. Keep its handle open
    // while the integrated bridge runs; no mutex ownership or waiting is needed.
    internal sealed class LegacyBridgeGuard : IDisposable
    {
        private readonly string mutexName;
        private readonly object gate = new object();
        private Mutex mutex;
        public string LastError { get; private set; }

        public LegacyBridgeGuard(string mutexName = @"Local\WorkBuddyAltTabHost")
        {
            this.mutexName = mutexName;
        }

        public bool TryAcquire()
        {
            lock (gate)
            {
                LastError = null;
                if (mutex != null) return true;
                if (string.IsNullOrWhiteSpace(mutexName))
                {
                    LastError = "WorkBuddy 桥接实例保护名称不能为空。";
                    return false;
                }
                try
                {
                    bool created;
                    Mutex candidate = new Mutex(false, mutexName, out created);
                    if (!created)
                    {
                        candidate.Dispose();
                        LastError = "旧版 WorkBuddy Host 已在运行，请先退出旧版后启用内置桥接。";
                        return false;
                    }
                    mutex = candidate;
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = "无法获取 WorkBuddy 桥接实例保护：" + ex.Message;
                    return false;
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (mutex == null) return;
                mutex.Dispose();
                mutex = null;
            }
        }
    }
}
