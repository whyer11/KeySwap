using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using KeySwap;

internal static class BridgeStatusTests
{
    private const string Nonce = "00112233445566778899aAbBcCdDeEfF";
    private static int passed;
    private static int failed;

    private static int Main()
    {
        Test("loopback response preserves nonce, source port and exact V2 fields", delegate {
            int port = AvailablePort();
            using (BridgeStatusServer server = new BridgeStatusServer(port))
            {
                Assert(server.Start(), "server failed to start: " + server.LastError);
                Assert(server.IsRunning, "running state missing");
                Assert(server.Start(), "repeated start must be idempotent");
                ExpectResponse(port, "1.1.0");
            }
        });
        Test("live readiness gate suppresses replies before readiness and after revocation", delegate {
            int port = AvailablePort();
            using (ManualResetEvent ready = new ManualResetEvent(false))
            {
                using (BridgeStatusServer server = new BridgeStatusServer(port,
                    isReady: delegate { return ready.WaitOne(0); }))
                {
                    Assert(server.Start(), "server failed to start");
                    string request = "WB_STATUS_V2 nonce=" + Nonce;
                    ExpectNoResponse(port, request);
                    ready.Set();
                    ExpectResponse(port, "1.1.0");
                    ready.Reset();
                    ExpectNoResponse(port, request);
                    Assert(server.IsRunning, "revoking readiness should leave the listener available for recovery");
                }
            }
        });
        Test("malformed, command, V1, trailing and oversized requests are ignored", delegate {
            int port = AvailablePort();
            using (BridgeStatusServer server = new BridgeStatusServer(port))
            {
                Assert(server.Start(), "server failed to start");
                string valid = "WB_STATUS_V2 nonce=" + Nonce;
                string[] invalid = new string[] {
                    "", "WB_STATUS_V1 " + Nonce, "RUN_KEY " + Nonce,
                    "wb_status_v2 nonce=" + Nonce, "WB_STATUS_V2 nonce=" + Nonce.Substring(1),
                    valid + "0", "WB_STATUS_V2 nonce=" + Nonce.Substring(0, 31) + "g",
                    "WB_STATUS_V2 nonce=" + Nonce.Substring(0, 31) + "\u00e9",
                    " " + valid, valid + " ", valid + "\n", valid + "\r\n",
                    valid + "\0", valid + " extra=x", new string('x', 512),
                    valid + new string('x', 513 - valid.Length), new string('x', 4096)
                };
                foreach (string request in invalid) ExpectNoResponse(port, request);
                // A bad or truncated datagram must not kill the receiver.
                ExpectResponse(port, "1.1.0");
            }
        });
        Test("occupied port fails without throwing and succeeds after retry", delegate {
            using (Socket occupied = BindExclusive(0))
            using (BridgeStatusServer server = new BridgeStatusServer(Port(occupied)))
            {
                int port = Port(occupied);
                Assert(!server.Start(), "second owner bound the occupied UDP port");
                Assert(!server.IsRunning, "failed bind reported running");
                Assert(!string.IsNullOrEmpty(server.LastError), "failed bind needs a diagnostic");
                occupied.Close();
                Assert(server.Start(), "retry failed after port release: " + server.LastError);
                Assert(string.IsNullOrEmpty(server.LastError), "successful retry retained old error");
                ExpectResponse(port, "1.1.0");
            }
        });
        Test("idle stop is bounded, releases port and permits restart", delegate {
            int port = AvailablePort();
            using (BridgeStatusServer server = new BridgeStatusServer(port))
            {
                Assert(server.Start(), "server failed to start");
                ExpectResponse(port, "1.1.0");
                Stopwatch elapsed = Stopwatch.StartNew();
                server.Stop();
                Assert(elapsed.ElapsedMilliseconds < 2000, "stop blocked beyond its bound");
                Assert(!server.IsRunning, "stopped server still reports running");
                server.Stop();
                using (Socket replacement = BindExclusive(port)) { }
                Assert(server.Start(), "same server could not restart");
                ExpectResponse(port, "1.1.0");
            }
            using (Socket replacement = BindExclusive(port)) { }
        });
        Test("dispose closes port and prevents accidental restart", delegate {
            int port = AvailablePort();
            BridgeStatusServer server = new BridgeStatusServer(port);
            try
            {
                Assert(server.Start(), "server failed to start");
                server.Dispose();
                Assert(!server.IsRunning, "disposed server still reports running");
                Assert(!server.Start(), "disposed server restarted");
                Assert(!string.IsNullOrEmpty(server.LastError), "disposed start needs a diagnostic");
                server.Dispose();
                using (Socket replacement = BindExclusive(port)) { }
            }
            finally { server.Dispose(); }
        });
        Test("custom host version remains a single valid V2 field", delegate {
            int port = AvailablePort();
            using (BridgeStatusServer server = new BridgeStatusServer(port, "1.2.3+test"))
            {
                Assert(server.Start(), "server failed to start");
                ExpectResponse(port, "1.2.3+test");
            }
        });
        Test("invalid port and host version cannot create malformed responses", delegate {
            ExpectArgumentException(delegate { new BridgeStatusServer(-1); });
            ExpectArgumentException(delegate { new BridgeStatusServer(65536); });
            ExpectArgumentException(delegate { new BridgeStatusServer(0, null); });
            ExpectArgumentException(delegate { new BridgeStatusServer(0, ""); });
            ExpectArgumentException(delegate { new BridgeStatusServer(0, "1.0 injected=x"); });
            ExpectArgumentException(delegate { new BridgeStatusServer(0, "1.0\n"); });
            ExpectArgumentException(delegate { new BridgeStatusServer(0, new string('v', 512)); });
        });
        Console.WriteLine("Bridge status: {0} passed, {1} failed", passed, failed);
        return failed == 0 ? 0 : 1;
    }

    private static void ExpectResponse(int port, string version)
    {
        using (Socket client = Client(1000))
        {
            byte[] request = Encoding.ASCII.GetBytes("WB_STATUS_V2 nonce=" + Nonce);
            client.SendTo(request, new IPEndPoint(IPAddress.Loopback, port));
            byte[] response = new byte[513];
            EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
            int length = client.ReceiveFrom(response, ref sender);
            IPEndPoint actualSource = (IPEndPoint)sender;
            Assert(actualSource.Address.Equals(IPAddress.Loopback), "wrong source IP");
            Assert(actualSource.Port == port, "response came from a different source port");
            string actual = Encoding.ASCII.GetString(response, 0, length);
            Assert(actual == "WB_STATUS_V2 nonce=" + Nonce + " host=" + version
                + " protocol=2 core=ready rules=alt_tab,windows_key", "unexpected response: " + actual);
        }
    }

    private static void ExpectNoResponse(int port, string request)
    {
        using (Socket client = Client(100))
        {
            client.SendTo(Encoding.UTF8.GetBytes(request), new IPEndPoint(IPAddress.Loopback, port));
            try
            {
                byte[] response = new byte[513];
                EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                client.ReceiveFrom(response, ref sender);
                throw new Exception("request expected to be ignored produced a response");
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode != SocketError.TimedOut) throw;
            }
        }
    }

    private static Socket Client(int timeout)
    {
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveTimeout = timeout;
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private static Socket BindExclusive(int port)
    {
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return socket;
        }
        catch { socket.Close(); throw; }
    }

    private static int Port(Socket socket) { return ((IPEndPoint)socket.LocalEndPoint).Port; }
    private static int AvailablePort() { using (Socket socket = BindExclusive(0)) return Port(socket); }
    private static void ExpectArgumentException(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new Exception("invalid configuration was accepted");
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
