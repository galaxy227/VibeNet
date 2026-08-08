using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VibeNet;

internal static class Program
{
    private static async Task<int> Main()
    {
        List<TestCase> tests = new List<TestCase>
        {
            new TestCase("Basic_HandshakeMessagingDisconnect", TestBasicHandshakeMessagingDisconnectAsync),
            new TestCase("Lifecycle_SingleUseInstances", TestSingleUseInstancesAsync),
            new TestCase("Server_CapacityRejectsExtraClient", TestServerCapacityRejectsExtraClientAsync),
            new TestCase("Validation_InvalidEnumsAndTimeoutsThrow", TestInvalidEnumsAndTimeoutsThrowAsync),
            new TestCase("Handshake_UDPRequiredOnServer", TestUdpRequiredOnServerAsync),
            new TestCase("Protocol_ServerRejectsEarlyPong", TestServerRejectsEarlyPongAsync),
            new TestCase("Protocol_ServerRejectsMalformedClientDisconnect", TestServerRejectsMalformedClientDisconnectAsync),
            new TestCase("Protocol_ClientRejectsInvalidServerDisconnectCode", TestClientRejectsInvalidServerDisconnectCodeAsync),
            new TestCase("Queue_UDPPressureDoesNotFaultOtherClientTCP", TestUdpPressureDoesNotFaultOtherClientTcpAsync)
        };

        int passed = 0;
        foreach (TestCase test in tests)
        {
            try
            {
                await test.ExecuteAsync().ConfigureAwait(false);
                passed++;
                Console.WriteLine("[PASS] " + test.Name);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] " + test.Name);
                Console.WriteLine("  " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        Console.WriteLine();
        Console.WriteLine("Passed " + passed + " / " + tests.Count);
        return passed == tests.Count ? 0 : 1;
    }

    private static async Task TestBasicHandshakeMessagingDisconnectAsync()
    {
        int port = GetFreePort();
        VibeNetConfiguration config = CreateDefaultConfig();
        VibeNetServer server = new VibeNetServer(port, null, 8, IPAddress.Loopback, config);
        VibeNetClient client = new VibeNetClient("127.0.0.1", port, null, config);

        try
        {
            Assert((await server.StartAsync().ConfigureAwait(false)).Success, "Server failed to start.");
            Assert((await client.StartAsync().ConfigureAwait(false)).Success, "Client failed to start.");

            await WaitUntilAsync(() => server.ConnectedClientCount == 1, TimeSpan.FromSeconds(2), "server connect count")
                .ConfigureAwait(false);
            await WaitUntilAsync(() => server.TryDequeueConnected(out _), TimeSpan.FromSeconds(2), "connected event")
                .ConfigureAwait(false);

            Assert(client.IsConnected, "Client should be connected.");
            Assert(client.Connection.RemoteAddress != null, "Client remote address should be populated.");
            Assert(client.Connection.ConnectedAtUtc.HasValue, "Client connected timestamp should be set.");

            await client.SendAsync(Encoding.UTF8.GetBytes("c-tcp"), VibeNetTransport.TCP).ConfigureAwait(false);
            await client.SendAsync(Encoding.UTF8.GetBytes("c-udp"), VibeNetTransport.UDP).ConfigureAwait(false);

            VibeNetMessage[] serverMessages = await WaitForMessagesAsync(server, 2).ConfigureAwait(false);
            Assert(serverMessages.Any(message => message.Transport == VibeNetTransport.TCP), "Server missing TCP message.");
            Assert(serverMessages.Any(message => message.Transport == VibeNetTransport.UDP), "Server missing UDP message.");

            await server.SendAsync(client.ConnectionId, Encoding.UTF8.GetBytes("s-tcp"), VibeNetTransport.TCP)
                .ConfigureAwait(false);
            await server.SendAsync(client.ConnectionId, Encoding.UTF8.GetBytes("s-udp"), VibeNetTransport.UDP)
                .ConfigureAwait(false);

            VibeNetMessage[] clientMessages = await WaitForMessagesAsync(client, 2).ConfigureAwait(false);
            Assert(clientMessages.Any(message => message.Transport == VibeNetTransport.TCP), "Client missing TCP message.");
            Assert(clientMessages.Any(message => message.Transport == VibeNetTransport.UDP), "Client missing UDP message.");

            await client.DisconnectAsync().ConfigureAwait(false);
            await WaitUntilAsync(() => server.TryDequeueDisconnected(out _), TimeSpan.FromSeconds(2), "server disconnect event")
                .ConfigureAwait(false);
        }
        finally
        {
            await SafeDisconnectAsync(client).ConfigureAwait(false);
            await SafeStopAsync(server).ConfigureAwait(false);
            client.Dispose();
            server.Dispose();
        }
    }

    private static async Task TestSingleUseInstancesAsync()
    {
        int port = GetFreePort();
        VibeNetServer server = new VibeNetServer(port, null, 8, IPAddress.Loopback, CreateDefaultConfig());
        VibeNetClient client = new VibeNetClient("127.0.0.1", port, null, CreateDefaultConfig());

        try
        {
            Assert((await server.StartAsync().ConfigureAwait(false)).Success, "Server failed to start.");
            Assert((await client.StartAsync().ConfigureAwait(false)).Success, "Client failed to start.");

            bool serverThrew = false;
            bool clientThrew = false;

            try
            {
                await server.StartAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                serverThrew = true;
            }

            try
            {
                await client.StartAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                clientThrew = true;
            }

            Assert(serverThrew, "Server should be single-use.");
            Assert(clientThrew, "Client should be single-use.");
        }
        finally
        {
            await SafeDisconnectAsync(client).ConfigureAwait(false);
            await SafeStopAsync(server).ConfigureAwait(false);
            client.Dispose();
            server.Dispose();
        }
    }

    private static async Task TestServerCapacityRejectsExtraClientAsync()
    {
        int port = GetFreePort();
        VibeNetConfiguration config = CreateDefaultConfig();
        VibeNetServer server = new VibeNetServer(port, null, 1, IPAddress.Loopback, config);
        VibeNetClient first = new VibeNetClient("127.0.0.1", port, null, config);
        VibeNetClient second = new VibeNetClient("127.0.0.1", port, null, config);

        try
        {
            Assert((await server.StartAsync().ConfigureAwait(false)).Success, "Server failed to start.");
            Assert((await first.StartAsync().ConfigureAwait(false)).Success, "First client failed to connect.");

            VibeNetConnectResult secondResult = await second.StartAsync().ConfigureAwait(false);
            Assert(!secondResult.Success, "Second client should have been rejected.");
            Assert(secondResult.Failure == VibeNetConnectFailure.ServerRejected, "Expected ServerRejected.");
        }
        finally
        {
            await SafeDisconnectAsync(first).ConfigureAwait(false);
            await SafeDisconnectAsync(second).ConfigureAwait(false);
            await SafeStopAsync(server).ConfigureAwait(false);
            first.Dispose();
            second.Dispose();
            server.Dispose();
        }
    }

    private static Task TestInvalidEnumsAndTimeoutsThrowAsync()
    {
        bool badAddressMode = false;
        bool badTimeout = false;
        bool badTransport = false;

        try
        {
            _ = new VibeNetConfiguration(addressMode: (VibeNetAddressMode)999);
        }
        catch (ArgumentOutOfRangeException)
        {
            badAddressMode = true;
        }

        try
        {
            _ = new VibeNetConfiguration(clientConnectTimeout: TimeSpan.MaxValue);
        }
        catch (ArgumentOutOfRangeException)
        {
            badTimeout = true;
        }

        try
        {
            VibeNetConfiguration config = CreateDefaultConfig();
            VibeNetClient client = new VibeNetClient("127.0.0.1", 7777, null, config);
            try
            {
                client.SendAsync(Array.Empty<byte>(), (VibeNetTransport)999).GetAwaiter().GetResult();
            }
            finally
            {
                client.Dispose();
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            badTransport = true;
        }
        catch (InvalidOperationException)
        {
            badTransport = true;
        }

        Assert(badAddressMode, "Invalid AddressMode should throw.");
        Assert(badTimeout, "Invalid timeout should throw.");
        Assert(badTransport, "Invalid transport should throw.");

        return Task.CompletedTask;
    }

    private static async Task TestUdpRequiredOnServerAsync()
    {
        int port = GetFreePort();
        VibeNetConfiguration config = CreateShortHandshakeConfig();
        VibeNetServer server = new VibeNetServer(port, null, 8, IPAddress.Loopback, config);
        TcpClient rawClient = new TcpClient(AddressFamily.InterNetwork);

        try
        {
            Assert((await server.StartAsync().ConfigureAwait(false)).Success, "Server failed to start.");
            await rawClient.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);

            NetworkFrame? hello = await VibeNetProtocol.ReadTcpFrameAsync(
                rawClient.GetStream(),
                VibeNetProtocol.MaxControlPayloadBytes,
                CancellationToken.None).ConfigureAwait(false);

            Assert(hello != null && hello.Type == VibeNetPacketType.Hello, "Expected HELLO.");

            await Task.Delay(config.ServerConnectTimeout + TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            Assert(server.PendingClientCount == 0, "Pending client should time out without UDP registration.");
            Assert(server.ConnectedClientCount == 0, "Raw client should never become connected.");
        }
        finally
        {
            rawClient.Close();
            await SafeStopAsync(server).ConfigureAwait(false);
            server.Dispose();
        }
    }

    private static async Task TestServerRejectsEarlyPongAsync()
    {
        int port = GetFreePort();
        VibeNetConfiguration config = CreateShortHandshakeConfig();
        VibeNetServer server = new VibeNetServer(port, null, 8, IPAddress.Loopback, config);
        TcpClient rawClient = new TcpClient(AddressFamily.InterNetwork);

        try
        {
            Assert((await server.StartAsync().ConfigureAwait(false)).Success, "Server failed to start.");
            await rawClient.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);

            NetworkFrame? hello = await VibeNetProtocol.ReadTcpFrameAsync(
                rawClient.GetStream(),
                VibeNetProtocol.MaxControlPayloadBytes,
                CancellationToken.None).ConfigureAwait(false);

            Assert(hello != null && hello.Type == VibeNetPacketType.Hello, "Expected HELLO.");

            await VibeNetProtocol.WriteTcpFrameAsync(
                rawClient.GetStream(),
                VibeNetPacketType.Pong,
                VibeNetProtocol.EmptyPayload,
                CancellationToken.None).ConfigureAwait(false);

            await Task.Delay(300).ConfigureAwait(false);
            Assert(server.ConnectedClientCount == 0, "Early PONG should not complete the session.");

            await WaitUntilAsync(
                () => DrainErrors(server).Any(error => error.Code == VibeNetErrorCode.ProtocolError),
                TimeSpan.FromSeconds(2),
                "protocol error").ConfigureAwait(false);
        }
        finally
        {
            rawClient.Close();
            await SafeStopAsync(server).ConfigureAwait(false);
            server.Dispose();
        }
    }

    private static async Task TestServerRejectsMalformedClientDisconnectAsync()
    {
        int port = GetFreePort();
        VibeNetConfiguration config = CreateDefaultConfig();
        VibeNetServer server = new VibeNetServer(port, null, 8, IPAddress.Loopback, config);
        RawClientSession? raw = null;

        try
        {
            Assert((await server.StartAsync().ConfigureAwait(false)).Success, "Server failed to start.");
            raw = await RawClientSession.ConnectAsync(port).ConfigureAwait(false);

            await WaitUntilAsync(() => server.TryDequeueConnected(out _), TimeSpan.FromSeconds(2), "connected event")
                .ConfigureAwait(false);

            await VibeNetProtocol.WriteTcpFrameAsync(
                raw.Tcp.GetStream(),
                VibeNetPacketType.Disconnect,
                VibeNetProtocol.EmptyPayload,
                CancellationToken.None).ConfigureAwait(false);

            VibeNetDisconnectInfo disconnect = await WaitForDisconnectAsync(server).ConfigureAwait(false);
            Assert(disconnect.Reason == VibeNetDisconnectReason.ProtocolError, "Malformed disconnect should be protocol error.");
        }
        finally
        {
            if (raw != null)
                await raw.DisposeAsync().ConfigureAwait(false);

            await SafeStopAsync(server).ConfigureAwait(false);
            server.Dispose();
        }
    }

    private static async Task TestClientRejectsInvalidServerDisconnectCodeAsync()
    {
        int port = GetFreePort();
        VibeNetConfiguration config = CreateDefaultConfig();
        TcpListener listener = new TcpListener(IPAddress.Loopback, port);
        UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        VibeNetClient client = new VibeNetClient("127.0.0.1", port, null, config);
        Task serverTask = Task.CompletedTask;

        try
        {
            listener.Start();
            serverTask = RunServerWithInvalidDisconnectCodeAsync(listener, udp).AsTask();

            Assert((await client.StartAsync().ConfigureAwait(false)).Success, "Client failed to connect.");

            VibeNetDisconnectInfo disconnect = await WaitForDisconnectAsync(client).ConfigureAwait(false);
            Assert(disconnect.Reason == VibeNetDisconnectReason.ProtocolError, "Invalid server disconnect code should be protocol error.");
        }
        finally
        {
            try
            {
                await serverTask.ConfigureAwait(false);
            }
            catch
            {
            }

            await SafeDisconnectAsync(client).ConfigureAwait(false);
            client.Dispose();
            udp.Close();
            listener.Stop();
        }
    }

    private static async Task TestUdpPressureDoesNotFaultOtherClientTcpAsync()
    {
        int port = GetFreePort();
        VibeNetConfiguration config = new VibeNetConfiguration(
            clientConnectTimeout: TimeSpan.FromSeconds(5),
            udpHandshakeInterval: TimeSpan.FromMilliseconds(50),
            udpHandshakeTimeout: TimeSpan.FromSeconds(2),
            serverConnectTimeout: TimeSpan.FromSeconds(2),
            tcpHeartbeatInterval: TimeSpan.FromMilliseconds(100),
            tcpHeartbeatTimeout: TimeSpan.FromMilliseconds(400),
            maxTcpPayloadBytes: 1024 * 1024,
            maxUdpPayloadBytes: 1200,
            maxQueuedMessages: 1,
            maxQueuedErrors: 64,
            maxQueuedEvents: 64,
            addressMode: VibeNetAddressMode.IPv4);

        VibeNetServer server = new VibeNetServer(port, null, 8, IPAddress.Loopback, config);
        VibeNetClient client1 = new VibeNetClient("127.0.0.1", port, null, config);
        VibeNetClient client2 = new VibeNetClient("127.0.0.1", port, null, config);

        try
        {
            Assert((await server.StartAsync().ConfigureAwait(false)).Success, "Server failed to start.");
            Assert((await client1.StartAsync().ConfigureAwait(false)).Success, "Client1 failed to connect.");
            Assert((await client2.StartAsync().ConfigureAwait(false)).Success, "Client2 failed to connect.");

            await WaitUntilAsync(() => server.ConnectedClientCount == 2, TimeSpan.FromSeconds(2), "two clients")
                .ConfigureAwait(false);

            await client1.SendAsync(Encoding.UTF8.GetBytes("udp-fill"), VibeNetTransport.UDP).ConfigureAwait(false);
            await client2.SendAsync(Encoding.UTF8.GetBytes("tcp-keep"), VibeNetTransport.TCP).ConfigureAwait(false);

            VibeNetMessage message = await WaitForMessageAsync(server).ConfigureAwait(false);
            Assert(
                message.ConnectionId == client2.ConnectionId &&
                message.Transport == VibeNetTransport.TCP,
                "TCP message from client2 should survive UDP queue pressure.");

            await Task.Delay(200).ConfigureAwait(false);
            Assert(!server.TryDequeueDisconnected(out _), "No client should be disconnected by UDP pressure alone.");
        }
        finally
        {
            await SafeDisconnectAsync(client1).ConfigureAwait(false);
            await SafeDisconnectAsync(client2).ConfigureAwait(false);
            await SafeStopAsync(server).ConfigureAwait(false);
            client1.Dispose();
            client2.Dispose();
            server.Dispose();
        }
    }

    private static IEnumerable<VibeNetErrorInfo> DrainErrors(VibeNetNode node)
    {
        List<VibeNetErrorInfo> errors = new List<VibeNetErrorInfo>();
        while (node.TryDequeueError(out VibeNetErrorInfo error))
            errors.Add(error);

        return errors;
    }

    private static async Task<VibeNetDisconnectInfo> WaitForDisconnectAsync(VibeNetServer server)
    {
        VibeNetDisconnectInfo info = default;
        await WaitUntilAsync(
            () => server.TryDequeueDisconnected(out info),
            TimeSpan.FromSeconds(2),
            "server disconnect").ConfigureAwait(false);
        return info;
    }

    private static async Task<VibeNetDisconnectInfo> WaitForDisconnectAsync(VibeNetClient client)
    {
        VibeNetDisconnectInfo info = default;
        await WaitUntilAsync(
            () => client.TryDequeueDisconnected(out info),
            TimeSpan.FromSeconds(2),
            "client disconnect").ConfigureAwait(false);
        return info;
    }

    private static async Task<VibeNetMessage> WaitForMessageAsync(VibeNetNode node)
    {
        VibeNetMessage message = default;
        await WaitUntilAsync(
            () => node.TryDequeueMessage(out message),
            TimeSpan.FromSeconds(2),
            "message").ConfigureAwait(false);
        return message;
    }

    private static async Task<VibeNetMessage[]> WaitForMessagesAsync(VibeNetNode node, int count)
    {
        List<VibeNetMessage> messages = new List<VibeNetMessage>(count);
        await WaitUntilAsync(
            () =>
            {
                while (node.TryDequeueMessage(out VibeNetMessage message))
                {
                    messages.Add(message);
                    if (messages.Count >= count)
                        return true;
                }

                return messages.Count >= count;
            },
            TimeSpan.FromSeconds(2),
            "messages").ConfigureAwait(false);

        return messages.ToArray();
    }

    private static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string description)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for " + description + ".");
    }

    private static async ValueTask RunServerWithInvalidDisconnectCodeAsync(TcpListener listener, UdpClient udp)
    {
        Guid id = Guid.NewGuid();
        byte[] token = new byte[VibeNetProtocol.SessionTokenSize];
        for (int index = 0; index < token.Length; index++)
            token[index] = (byte)index;

        TcpClient tcp = await listener.AcceptTcpClientAsync().ConfigureAwait(false);

        try
        {
            await VibeNetProtocol.WriteTcpFrameAsync(
                tcp.GetStream(),
                VibeNetPacketType.Hello,
                VibeNetProtocol.CreateHelloPayload(id, token),
                CancellationToken.None).ConfigureAwait(false);

            UdpReceiveResult registration = await udp.ReceiveAsync().ConfigureAwait(false);
            bool validRegistrationFrame = VibeNetProtocol.TryParseUdpFrame(
                registration.Buffer,
                VibeNetProtocol.MaxControlPayloadBytes,
                out NetworkFrame? registrationFrame);
            bool validRegistrationPayload =
                validRegistrationFrame &&
                registrationFrame != null &&
                registrationFrame.Type == VibeNetPacketType.UdpRegister &&
                VibeNetProtocol.TryParseRegistrationPayload(
                    registrationFrame.Payload,
                    out Guid registeredId,
                    out byte[]? registeredToken) &&
                registeredId == id &&
                registeredToken != null &&
                registeredToken.SequenceEqual(token);

            Assert(validRegistrationPayload, "Invalid UDP registration.");

            byte[] ack = VibeNetProtocol.CreateUdpFrame(
                VibeNetPacketType.UdpAck,
                VibeNetProtocol.GuidToNetworkBytes(id));
            await udp.SendAsync(ack, ack.Length, registration.RemoteEndPoint).ConfigureAwait(false);

            NetworkFrame? ready = await VibeNetProtocol.ReadTcpFrameAsync(
                tcp.GetStream(),
                VibeNetProtocol.MaxControlPayloadBytes,
                CancellationToken.None).ConfigureAwait(false);
            Assert(ready != null && ready.Type == VibeNetPacketType.Ready, "Expected READY.");

            await VibeNetProtocol.WriteTcpFrameAsync(
                tcp.GetStream(),
                VibeNetPacketType.ReadyAck,
                VibeNetProtocol.EmptyPayload,
                CancellationToken.None).ConfigureAwait(false);

            await Task.Delay(100).ConfigureAwait(false);

            await VibeNetProtocol.WriteTcpFrameAsync(
                tcp.GetStream(),
                VibeNetPacketType.Disconnect,
                VibeNetProtocol.CreateDisconnectPayload(VibeNetDisconnectCode.ClientRequested),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            tcp.Close();
        }
    }

    private static async Task SafeDisconnectAsync(VibeNetClient client)
    {
        try
        {
            await client.DisconnectAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task SafeStopAsync(VibeNetServer server)
    {
        try
        {
            await server.StopAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static VibeNetConfiguration CreateDefaultConfig()
    {
        return new VibeNetConfiguration(
            clientConnectTimeout: TimeSpan.FromSeconds(5),
            udpHandshakeInterval: TimeSpan.FromMilliseconds(50),
            udpHandshakeTimeout: TimeSpan.FromSeconds(2),
            serverConnectTimeout: TimeSpan.FromSeconds(2),
            tcpHeartbeatInterval: TimeSpan.FromMilliseconds(100),
            tcpHeartbeatTimeout: TimeSpan.FromMilliseconds(400),
            maxTcpPayloadBytes: 1024 * 1024,
            maxUdpPayloadBytes: 1200,
            maxQueuedMessages: 256,
            maxQueuedErrors: 64,
            maxQueuedEvents: 64,
            addressMode: VibeNetAddressMode.IPv4);
    }

    private static VibeNetConfiguration CreateShortHandshakeConfig()
    {
        return new VibeNetConfiguration(
            clientConnectTimeout: TimeSpan.FromSeconds(5),
            udpHandshakeInterval: TimeSpan.FromMilliseconds(50),
            udpHandshakeTimeout: TimeSpan.FromMilliseconds(300),
            serverConnectTimeout: TimeSpan.FromMilliseconds(300),
            tcpHeartbeatInterval: TimeSpan.FromMilliseconds(100),
            tcpHeartbeatTimeout: TimeSpan.FromMilliseconds(400),
            maxTcpPayloadBytes: 1024 * 1024,
            maxUdpPayloadBytes: 1200,
            maxQueuedMessages: 64,
            maxQueuedErrors: 64,
            maxQueuedEvents: 64,
            addressMode: VibeNetAddressMode.IPv4);
    }

    private static int GetFreePort()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private readonly struct TestCase
    {
        public string Name { get; }
        public Func<Task> ExecuteAsync { get; }

        public TestCase(string name, Func<Task> executeAsync)
        {
            Name = name;
            ExecuteAsync = executeAsync;
        }
    }

    private sealed class RawClientSession : IAsyncDisposable
    {
        public TcpClient Tcp { get; }
        public UdpClient Udp { get; }

        private RawClientSession(TcpClient tcp, UdpClient udp)
        {
            Tcp = tcp;
            Udp = udp;
        }

        public static async Task<RawClientSession> ConnectAsync(int port)
        {
            TcpClient tcp = new TcpClient(AddressFamily.InterNetwork);
            UdpClient udp = new UdpClient(AddressFamily.InterNetwork);

            try
            {
                await tcp.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);

                NetworkFrame? hello = await VibeNetProtocol.ReadTcpFrameAsync(
                    tcp.GetStream(),
                    VibeNetProtocol.MaxControlPayloadBytes,
                    CancellationToken.None).ConfigureAwait(false);

                Guid id = Guid.Empty;
                byte[]? token = null;
                bool validHello =
                    hello != null &&
                    hello.Type == VibeNetPacketType.Hello &&
                    VibeNetProtocol.TryParseHelloPayload(hello.Payload, out id, out token) &&
                    token != null;

                Assert(validHello, "Failed to read HELLO.");

                udp.Connect(IPAddress.Loopback, port);
                byte[] registration = VibeNetProtocol.CreateRegistrationPayload(id, token!);
                byte[] datagram = VibeNetProtocol.CreateUdpFrame(VibeNetPacketType.UdpRegister, registration);
                await udp.SendAsync(datagram, datagram.Length).ConfigureAwait(false);

                UdpReceiveResult ack = await udp.ReceiveAsync().ConfigureAwait(false);
                bool validAck =
                    VibeNetProtocol.TryParseUdpFrame(
                        ack.Buffer,
                        VibeNetProtocol.MaxControlPayloadBytes,
                        out NetworkFrame? ackFrame) &&
                    ackFrame != null &&
                    ackFrame.Type == VibeNetPacketType.UdpAck &&
                    VibeNetProtocol.RegistrationAckMatches(ackFrame.Payload, id);

                Assert(validAck, "Invalid UDP ACK.");

                await VibeNetProtocol.WriteTcpFrameAsync(
                    tcp.GetStream(),
                    VibeNetPacketType.Ready,
                    VibeNetProtocol.EmptyPayload,
                    CancellationToken.None).ConfigureAwait(false);

                NetworkFrame? readyAck = await VibeNetProtocol.ReadTcpFrameAsync(
                    tcp.GetStream(),
                    VibeNetProtocol.MaxControlPayloadBytes,
                    CancellationToken.None).ConfigureAwait(false);

                Assert(readyAck != null && readyAck.Type == VibeNetPacketType.ReadyAck, "Expected READY_ACK.");

                return new RawClientSession(tcp, udp);
            }
            catch
            {
                udp.Close();
                tcp.Close();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            Udp.Close();
            Tcp.Close();
            return ValueTask.CompletedTask;
        }
    }
}
