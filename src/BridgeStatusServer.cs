using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace KeySwap
{
    internal sealed class BridgeStatusServer : IDisposable
    {
        private const int MaxDatagramBytes = 512;
        private const int NonceLength = 32;
        private const string RequestPrefix = "WB_STATUS_V2 nonce=";
        private readonly object sync = new object();
        private readonly int port;
        private readonly string responseSuffix;
        private readonly Func<bool> isReady;
        private Socket socket;
        private Thread worker;
        private string lastError;
        private bool disposed;

        public BridgeStatusServer(int port = 57420, string hostVersion = "1.1.0", Func<bool> isReady = null)
        {
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException("port");
            if (string.IsNullOrEmpty(hostVersion)) throw new ArgumentException("Host version is required.", "hostVersion");
            foreach (char value in hostVersion)
                if (value < 33 || value > 126) throw new ArgumentException("Host version must be one ASCII token.", "hostVersion");
            responseSuffix = " host=" + hostVersion + " protocol=2 core=ready rules=alt_tab,windows_key";
            if (RequestPrefix.Length + NonceLength + responseSuffix.Length > MaxDatagramBytes)
                throw new ArgumentException("Host version exceeds the status datagram limit.", "hostVersion");
            this.port = port;
            this.isReady = isReady;
        }

        public bool IsRunning { get { lock (sync) return socket != null; } }
        public string LastError { get { lock (sync) return lastError; } }

        // Binding is local and immediate; all receive/send work stays off the UI
        // and keyboard threads. Failure leaves the caller free to retry later.
        public bool Start()
        {
            lock (sync)
            {
                if (disposed) { lastError = "The bridge status server is disposed."; return false; }
                if (socket != null) return true;
                Socket candidate = null;
                try
                {
                    candidate = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    candidate.ExclusiveAddressUse = true;
                    candidate.ReceiveTimeout = 100;
                    candidate.Bind(new IPEndPoint(IPAddress.Any, port));
                    Socket startedSocket = candidate;
                    Thread startedWorker = new Thread(delegate() { ReceiveRequests(startedSocket); });
                    startedWorker.IsBackground = true;
                    startedWorker.Name = "KeySwap bridge status";
                    socket = candidate;
                    worker = startedWorker;
                    startedWorker.Start();
                    lastError = null;
                    return true;
                }
                catch (Exception ex)
                {
                    if (candidate != null) candidate.Close();
                    socket = null;
                    worker = null;
                    lastError = ex.Message;
                    return false;
                }
            }
        }

        public void Stop()
        {
            Socket stoppingSocket;
            Thread stoppingWorker;
            lock (sync)
            {
                stoppingSocket = socket;
                stoppingWorker = worker;
                socket = null;
                worker = null;
            }
            if (stoppingSocket != null) stoppingSocket.Close();
            if (stoppingWorker != null && stoppingWorker != Thread.CurrentThread)
                stoppingWorker.Join(1000);
        }

        public void Dispose()
        {
            lock (sync) disposed = true;
            Stop();
        }

        private bool IsCurrent(Socket activeSocket)
        {
            lock (sync) return socket == activeSocket;
        }

        private void ReceiveRequests(Socket activeSocket)
        {
            byte[] request = new byte[MaxDatagramBytes + 1];
            try
            {
                while (IsCurrent(activeSocket))
                {
                    EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                    int received;
                    try { received = activeSocket.ReceiveFrom(request, ref sender); }
                    catch (ObjectDisposedException) { break; }
                    catch (SocketException ex)
                    {
                        if (!IsCurrent(activeSocket)) break;
                        if (ex.SocketErrorCode == SocketError.TimedOut
                            || ex.SocketErrorCode == SocketError.WouldBlock
                            || ex.SocketErrorCode == SocketError.MessageSize
                            || ex.SocketErrorCode == SocketError.ConnectionReset) continue;
                        lock (sync) { if (socket == activeSocket) lastError = ex.Message; }
                        break;
                    }
                    string response = FormatResponse(request, received);
                    if (response == null) continue;
                    try
                    {
                        byte[] bytes = Encoding.ASCII.GetBytes(response);
                        // Read live hook health immediately before the reply;
                        // UI cleanup may be delayed after a keyboard fault.
                        if (isReady != null && !isReady()) continue;
                        activeSocket.SendTo(bytes, sender);
                    }
                    catch (ObjectDisposedException) { break; }
                    // One unreachable sender must not take down status for others.
                    catch (SocketException) { }
                }
            }
            finally
            {
                activeSocket.Close();
                lock (sync)
                {
                    if (socket == activeSocket) { socket = null; worker = null; }
                }
            }
        }

        private string FormatResponse(byte[] request, int length)
        {
            // Preserve the unchanged Pad's exact six-field V2 protocol. This is
            // a read-only capability reply; no datagram can perform an action.
            if (length != RequestPrefix.Length + NonceLength) return null;
            for (int index = 0; index < RequestPrefix.Length; index++)
                if (request[index] != RequestPrefix[index]) return null;
            for (int index = RequestPrefix.Length; index < length; index++)
            {
                byte value = request[index];
                if (!((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f')
                    || (value >= 'A' && value <= 'F'))) return null;
            }
            return Encoding.ASCII.GetString(request, 0, length) + responseSuffix;
        }
    }
}
