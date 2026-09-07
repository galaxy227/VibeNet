using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using VibeNet;
internal static partial class Program
{
    internal static Task<int> RunTests() => Main(Array.Empty<string>());
    static int checks;
    static void Assert(bool condition, string message)
    {
        checks++;
        if (!condition)
            throw new Exception(message);
    }
    static async Task Until(Func<bool> condition, int milliseconds = 5000)
    {
        var s = Stopwatch.StartNew();
        while (!condition())
        {
            if (s.ElapsedMilliseconds > milliseconds)
                throw new TimeoutException("Condition timed out.");
            await Task.Delay(5).ConfigureAwait(false);
        }
    }
    static int Port()
    {
        var t = new TcpListener(IPAddress.Loopback, 0);
        t.Start();
        int p = ((IPEndPoint)t.LocalEndpoint).Port;
        t.Stop();
        return p;
    }
    static async Task<int> Main(string[] args)
    {
        if (args.Length != 0)
            return await RunCommand(args);
        var tests = new (string, Func<Task>)[] { ("configuration", ValidationTests), ("crypto-integrity", CryptoTests), ("record-sequencing", SequenceTests), ("queues-and-admission", QueueTests), ("cancellation", CancellationTests), ("end-to-end", NetworkTests), ("multi-client-relay-and-rekey", MultiClientTests), ("hostile-and-overload", HostileTests), ("admission-and-rate-isolation", AdmissionTests) };
        int failures = 0;
        foreach (var t in tests)
        {
            try
            {
                await t.Item2();
                Console.WriteLine("PASS " + t.Item1);
            }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL " + t.Item1 + " " + ex); }
        }
        Console.WriteLine(checks + " assertions; " + failures + " failed groups");
        return failures == 0 ? 0 : 1;
    }
    static Task CryptoTests()
    {
        var k = Enumerable.Repeat((byte)0x0b, 22).ToArray();
        byte[] result = Crypto.Hkdf(Enumerable.Range(0, 13).Select(i => (byte)i).ToArray(), k, Enumerable.Range(0xf0, 10).Select(i => (byte)i).ToArray(), 42);
        Assert(BitConverter.ToString(result).Replace("-", "").ToLowerInvariant() == "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865", "HKDF RFC5869 vector");
        var id = Guid.NewGuid();
        var secret = Crypto.Random(32);
        using (var tx = new Lane((byte[])secret.Clone()))
        using (var rx = new Lane((byte[])secret.Clone()))
        {
            var p = tx.Seal(id, Kind.Data, VibeNetTransport.UDP, new byte[] { 1, 2, 3 });
            Assert(rx.Open(p, id, VibeNetTransport.UDP, 1024)!.SequenceEqual(new byte[] { 1, 2, 3 }), "round trip");
            Assert(rx.Open(p, id, VibeNetTransport.UDP, 1024) == null, "duplicate");
            for (int i = 0; i < p.Length; i++)
            {
                var bad = (byte[])p.Clone();
                bad[i] ^= 1;
                using (var fresh = new Lane((byte[])secret.Clone()))
                    Assert(fresh.Open(bad, id, VibeNetTransport.UDP, 1024) == null, "tamper " + i);
            }
            var second = tx.Seal(id, Kind.Data, VibeNetTransport.UDP, Array.Empty<byte>());
            var forged = (byte[])second.Clone();
            Wire.U64(forged, 28, 60000);
            Assert(rx.Open(forged, id, VibeNetTransport.UDP, 1024) == null, "forged high");
            Assert(rx.Open(second, id, VibeNetTransport.UDP, 1024) != null, "no replay poisoning");
        }
        return Task.CompletedTask;
    }
    static void Throws<T>(Action f) where T : Exception
    {
        try
        {
            f();
        }
        catch (T) { checks++; return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    static Task ValidationTests()
    {
        Throws<ArgumentOutOfRangeException>(() => new VibeNetLimits(maxClients: 0));
        Throws<ArgumentOutOfRangeException>(() => new VibeNetLimits(maxTcpPayloadBytes: int.MaxValue));
        Throws<ArgumentOutOfRangeException>(() => new VibeNetLimits(maxUdpPayloadBytes: 65408));
        Throws<ArgumentOutOfRangeException>(() => new VibeNetLimits(maxPendingSends: -1));
        Throws<ArgumentException>(() => new VibeNetLimits(maxQueuedBytes: 100, maxBufferedBytes: 10));
        Throws<ArgumentException>(() => new VibeNetConfiguration(idleTimeout: TimeSpan.FromMilliseconds(1)));
        Throws<ArgumentOutOfRangeException>(() => new VibeNetClient("localhost", 0));
        Throws<ArgumentException>(() => new VibeNetClient(""));
        using (var c = new VibeNetClient("localhost"))
        {
            Throws<ArgumentNullException>(() => c.SendAsync(null!, VibeNetTransport.TCP));
            Throws<ArgumentOutOfRangeException>(() => c.SendAsync(Array.Empty<byte>(), (VibeNetTransport)2));
        }
        return Task.CompletedTask;
    }
    static Task SequenceTests()
    {
        byte[] secret = Crypto.Random(32);
        Guid id = Guid.NewGuid();
        using (var tx = new Lane((byte[])secret.Clone()))
        using (var rx = new Lane((byte[])secret.Clone()))
        {
            var a = tx.Seal(id, Kind.Data, VibeNetTransport.TCP, Array.Empty<byte>());
            var b = tx.Seal(id, Kind.Data, VibeNetTransport.TCP, new byte[17]);
            Assert(rx.Open(b, id, VibeNetTransport.TCP, 32) == null, "TCP out of order");
            Assert(rx.Open(a, id, VibeNetTransport.TCP, 32) != null, "TCP expected");
            Assert(rx.Open(b, id, VibeNetTransport.TCP, 32) != null, "TCP next");
            Assert(rx.Open(b, id, VibeNetTransport.TCP, 32) == null, "TCP replay");
        }
        using (var tx = new Lane((byte[])secret.Clone()))
        using (var rx = new Lane((byte[])secret.Clone()))
        {
            var records = new List<byte[]>();
            for (int i = 0; i < 300; i++)
                records.Add(tx.Seal(id, Kind.Data, VibeNetTransport.UDP, Array.Empty<byte>()));
            Assert(rx.Open(records[299], id, VibeNetTransport.UDP, 32) != null, "high valid");
            Assert(rx.Open(records[44], id, VibeNetTransport.UDP, 32) != null, "window lower inclusive");
            Assert(rx.Open(records[43], id, VibeNetTransport.UDP, 32) == null, "outside window");
            Assert(rx.Open(records[298], id, VibeNetTransport.UDP, 32) != null, "reordering");
            Assert(rx.Open(records[297], Guid.NewGuid(), VibeNetTransport.UDP, 32) == null, "cross session");
            Assert(rx.Open(records[297], id, VibeNetTransport.TCP, 32) == null, "cross transport");
            using (var nextTx = tx.Next())
            using (var nextRx = rx.Next())
            {
                var next = nextTx.Seal(id, Kind.Data, VibeNetTransport.UDP, new byte[1]);
                Assert(rx.Open(next, id, VibeNetTransport.UDP, 32) == null, "future epoch rejected");
                Assert(nextRx.Open(next, id, VibeNetTransport.UDP, 32) != null, "new epoch valid");
            }
            using (var otherDirection = new Lane(Crypto.Expand(secret, "different-direction")))
                Assert(otherDirection.Open(records[0], id, VibeNetTransport.UDP, 32) == null, "direction separation");
            var invalidControl = tx.Seal(id, Kind.Ping, VibeNetTransport.TCP, new byte[1]);
            Assert(!Wire.HeaderValid(invalidControl, VibeNetTransport.TCP, 100), "control exact length");
            for (int i = 0; i < 64; i++)
                Assert(!Wire.HeaderValid(new byte[i], VibeNetTransport.UDP, 32), "short header");
            var random = new Random(1729);
            for (int i = 0; i < 2000; i++)
            {
                var garbage = new byte[random.Next(64, 512)];
                random.NextBytes(garbage);
                Assert(!Wire.HeaderValid(garbage, VibeNetTransport.UDP, 1024), "garbage parser");
            }
        }
        return Task.CompletedTask;
    }
    static async Task QueueTests()
    {
        var q = new Ring<int>(2);
        q.Add(1);
        q.Add(2);
        q.Add(3);
        Assert(q.Lost == 1 && q.Take(out int x) && x == 2, "ring drop oldest");
        var b = new Budget(10);
        Assert(b.Acquire(10) && !b.Acquire(1), "budget bound");
        b.Release(10);
        Assert(b.Used == 0, "budget release");
        var global = new Budget(1000);
        var local = new Budget(100);
        var lease = Lease.Try(global, local, 100)!;
        Assert(Lease.Try(global, local, 1) == null, "local limit");
        lease.Dispose();
        lease.Dispose();
        Assert(global.Used == 0 && local.Used == 0, "idempotent lease");
        Parallel.For(0, 10000, _ => { var l = Lease.Try(global, local, 5); l?.Dispose(); });
        Assert(global.Used == 0 && local.Used == 0, "parallel accounting");
        var config = new VibeNetConfiguration(new VibeNetLimits(maxQueuedMessages: 2, maxMessagesPerConnection: 2, maxQueuedBytes: 4));
        using (var node = new VibeNetServer(configuration: config))
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var peers = new List<TcpClient>();
            var links = new List<Link>();
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    var tcp = new TcpClient();
                    var connect = tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                    var peer = await listener.AcceptTcpClientAsync();
                    await connect;
                    peers.Add(peer);
                    var link = new Link(node, tcp, Guid.NewGuid(), true);
                    link.Connected();
                    links.Add(link);
                }
                Assert(node.Messages.Add(links[0], new byte[] { 1, 1 }, VibeNetTransport.UDP), "UDP fill");
                Assert(node.Messages.Add(links[1], new byte[] { 2, 2 }, VibeNetTransport.TCP), "TCP fill");
                Assert(node.Messages.Add(links[0], new byte[] { 3, 3 }, VibeNetTransport.TCP), "evict oldest UDP");
                Assert(node.Messages.Take(out var first) && first.Data[0] == 2, "FIFO survivor");
                Assert(node.Messages.Take(out var second) && second.Data[0] == 3, "FIFO newcomer");
                Assert(node.Messages.Add(links[0], new byte[] { 4 }, VibeNetTransport.TCP), "owner enqueue");
                links[0].Close(DisconnectReason.LocalRequested);
                Assert(!node.Messages.Add(links[0], new byte[1], VibeNetTransport.TCP), "no enqueue after close");
                Assert(node.Statistics.BufferedBytes == 0 && node.Statistics.QueuedMessages == 0, "owner cleanup releases");
            }
            finally { foreach (var c in links) c.Close(DisconnectReason.LocalRequested); foreach (var p in peers) p.Dispose(); listener.Stop(); }
        }
    }
    static async Task CancellationTests()
    {
        using (var source = new CancellationTokenSource())
        {
            for (int i = 0; i < 10000; i++)
                await Wire.Wait(Task.CompletedTask, source.Token);
            source.Cancel();
            bool canceled = false;
            try
            {
                await Wire.Wait(new TaskCompletionSource<bool>().Task, source.Token);
            }
            catch (OperationCanceledException) { canceled = true; }
            Assert(canceled, "wait cancellation");
        }
        using (var c = new VibeNetClient("127.0.0.1", Port()))
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel();
            bool canceled = false;
            try
            {
                await c.StartAsync(stop.Token);
            }
            catch (OperationCanceledException) { canceled = true; }
            Assert(canceled, "startup cancellation");
            Assert(c.State == ConnectionState.Closed, "canceled state");
            await c.DisconnectAsync();
        }
    }
    static Link ClientLink(VibeNetClient c) => (Link)typeof(VibeNetClient).GetField("link", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(c)!;
    static Lane GetLane(Link c, string name) => (Lane)typeof(Link).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(c)!;
    static async Task MultiClientTests()
    {
        int p = Port();
        var config = new VibeNetConfiguration(new VibeNetLimits(maxClients: 3, maxPendingSendsPerConnection: 1), heartbeatInterval: TimeSpan.FromMilliseconds(500), idleTimeout: TimeSpan.FromSeconds(10));
        using (var server = new VibeNetServer(p, bindAddress: IPAddress.Loopback, configuration: config))
        using (var host = new VibeNetClient("127.0.0.1", p, configuration: config))
        using (var guest = new VibeNetClient("127.0.0.1", p, configuration: config))
        {
            Assert((await server.StartAsync()).Success, "relay start");
            Assert((await host.StartAsync()).Success, "host outbound");
            Assert((await guest.StartAsync()).Success, "guest outbound");
            await Until(() => server.ConnectedClientCount == 2);
            var sendGate = (SemaphoreSlim)typeof(Link).GetField("tcpSend", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(ClientLink(host))!;
            await sendGate.WaitAsync();
            using (var cancel = new CancellationTokenSource())
            {
                try
                {
                    var pending = host.SendAsync(new byte[1], VibeNetTransport.TCP, cancel.Token);
                    Assert(await host.SendAsync(new byte[1], VibeNetTransport.TCP) == SendResult.Backpressured, "bounded pending sends");
                    cancel.Cancel();
                    bool canceled = false;
                    try
                    {
                        await pending;
                    }
                    catch (OperationCanceledException) { canceled = true; }
                    Assert(canceled && host.Statistics.PendingSends == 0, "queued cancellation releases admission");
                    Assert(host.State == ConnectionState.Connected, "cancel before write keeps connection");
                }
                finally { sendGate.Release(); }
            }
            var result = await server.BroadcastAsync(new byte[] { 9 }, VibeNetTransport.UDP);
            Assert(result.Sent == 2, "UDP broadcast");
            await Until(() => host.TryDequeueMessage(out _));
            await Until(() => guest.TryDequeueMessage(out _));
            Assert(await host.SendAsync(new byte[] { 7 }, VibeNetTransport.TCP) == SendResult.Sent, "authoritative host to relay");
            VibeNetMessage m = default;
            await Until(() => server.TryDequeueMessage(out m));
            Assert(m.ConnectionId == host.ConnectionId, "relay source identity");
            await server.SendAsync(guest.ConnectionId, m.Data, m.Transport);
            await Until(() => guest.TryDequeueMessage(out m));
            Assert(m.Data[0] == 7, "relay forwards app state");
            var tx = GetLane(ClientLink(host), "tcpTx");
            tx.Bytes = Lane.ByteLimit;
            Assert(await host.SendAsync(new byte[] { 11 }, VibeNetTransport.TCP) == SendResult.Sent, "TCP key update send");
            await Until(() => server.TryDequeueMessage(out m));
            Assert(m.Data[0] == 11 && GetLane(ClientLink(host), "tcpTx").Epoch == 1, "TCP epoch transition");
            var udpTx = GetLane(ClientLink(host), "udpTx");
            udpTx.Bytes = Lane.ByteLimit;
            Assert(await host.SendAsync(new byte[] { 12 }, VibeNetTransport.UDP) == SendResult.Sent, "UDP key update send");
            await Until(() => server.TryDequeueMessage(out m));
            Assert(m.Data[0] == 12 && GetLane(ClientLink(host), "udpTx").Epoch == 1, "UDP ack and epoch transition");
            // Ordered TCP calls plus an awaited batch; concurrent wall-clock call ordering is not assumed.
            for (int i = 0; i < 20; i++)
                Assert(await host.SendAsync(new[] { (byte)i }, VibeNetTransport.TCP) == SendResult.Sent, "ordered write");
            for (int i = 0; i < 20; i++)
            {
                await Until(() => server.TryDequeueMessage(out m));
                Assert(m.Data[0] == i, "TCP ordered delivery");
            }
            await Task.Delay(1100);
            Assert(host.State == ConnectionState.Connected && guest.State == ConnectionState.Connected, "heartbeat liveness");
            await server.StopAsync();
            DisconnectInfo d = default;
            await Until(() => guest.TryDequeueDisconnected(out d));
            Assert(d.Reason == DisconnectReason.ServerStopped, "server stop reason");
            await host.DisconnectAsync();
            await guest.DisconnectAsync();
            Assert(server.Statistics.BufferedBytes == 0, "server stopped budget");
            bool singleUse = false;
            try
            {
                await server.StartAsync();
            }
            catch (InvalidOperationException) { singleUse = true; }
            Assert(singleUse, "server single use");
        }
    }
    static async Task HostileTests()
    {
        int p = Port();
        var config = new VibeNetConfiguration(new VibeNetLimits(maxQueuedMessages: 1, maxMessagesPerConnection: 1, maxClients: 2, maxPendingHandshakes: 1));
        using (var server = new VibeNetServer(p, bindAddress: IPAddress.Loopback, configuration: config))
        using (var client = new VibeNetClient("127.0.0.1", p))
        {
            Assert((await server.StartAsync()).Success && (await client.StartAsync()).Success, "hostile fixture start");
            await Until(() => server.ConnectedClientCount == 1);
            using (var udp = new UdpClient())
            {
                for (int i = 0; i < 100; i++)
                {
                    byte[] garbage = new byte[64];
                    await udp.SendAsync(garbage, garbage.Length, new IPEndPoint(IPAddress.Loopback, p));
                }
            }
            await Until(() => server.Statistics.DroppedUdp > 0);
            Assert(client.State == ConnectionState.Connected, "garbage does not disconnect");
            await client.SendAsync(new byte[1], VibeNetTransport.TCP);
            await Until(() => server.Statistics.QueuedMessages == 1);
            await client.SendAsync(new byte[1], VibeNetTransport.TCP);
            DisconnectInfo d = default;
            await Until(() => server.TryDequeueDisconnected(out d));
            Assert(d.Reason == DisconnectReason.ResourceLimit, "reliable overflow");
            Assert(server.Statistics.QueuedMessages == 0, "overflow removes owner queue");
            await client.DisconnectAsync();
            await server.StopAsync();
        }
        p = Port();
        using (var server = new VibeNetServer(p, bindAddress: IPAddress.Loopback))
        using (var client = new VibeNetClient("127.0.0.1", p))
        {
            await server.StartAsync();
            Assert((await client.StartAsync()).Success, "integrity fixture start");
            await Until(() => server.ConnectedClientCount == 1);
            var link = ClientLink(client);
            var bad = GetLane(link, "tcpTx").Seal(link.Id, Kind.Data, VibeNetTransport.TCP, new byte[] { 99 });
            bad[bad.Length - 1] ^= 1;
            await link.Stream.WriteAsync(bad, 0, bad.Length);
            DisconnectInfo d = default;
            await Until(() => server.TryDequeueDisconnected(out d));
            Assert(d.Reason == DisconnectReason.ProtocolError && !server.TryDequeueMessage(out _), "bad MAC rejected before delivery");
            await client.DisconnectAsync();
            await server.StopAsync();
        }
    }
    static async Task AdmissionTests()
    {
        int p = Port();
        var config = new VibeNetConfiguration(new VibeNetLimits(maxPendingHandshakes: 1), connectTimeout: TimeSpan.FromMilliseconds(500));
        using (var server = new VibeNetServer(p, bindAddress: IPAddress.Loopback, configuration: config))
        using (var stalled = new TcpClient())
        {
            Assert((await server.StartAsync()).Success, "admission server");
            await stalled.ConnectAsync(IPAddress.Loopback, p);
            await Until(() => server.PendingClientCount == 1);
            using (var rejected = new VibeNetClient("127.0.0.1", p))
                Assert(!(await rejected.StartAsync()).Success, "pending handshake cap");
            await Until(() => server.PendingClientCount == 0, 60000);
            Assert(server.ConnectedClientCount == 0 && server.Statistics.BufferedBytes == 0, "expired handshake cleaned");
            await server.StopAsync();
        }
        p = Port();
        config = new VibeNetConfiguration(new VibeNetLimits(maxPacketsPerSecond: 8));
        using (var server = new VibeNetServer(p, bindAddress: IPAddress.Loopback, configuration: config))
        using (var client = new VibeNetClient("127.0.0.1", p))
        {
            await server.StartAsync();
            Assert((await client.StartAsync()).Success, "rate fixture");
            // Exhaust the entire UDP bucket deterministically: unauthenticated traffic must not consume TCP quota.
            while (server.InputRate.Take(1))
            {
            }
            Assert(await client.SendAsync(new byte[] { 23 }, VibeNetTransport.TCP) == SendResult.Sent, "TCP survives UDP quota exhaustion");
            VibeNetMessage m = default;
            await Until(() => server.TryDequeueMessage(out m));
            Assert(m.Data[0] == 23 && client.State == ConnectionState.Connected, "isolated quota delivery");
            await client.DisconnectAsync();
            await server.StopAsync();
        }
        if (Socket.OSSupportsIPv6)
        {
            p = Port();
            using (var server = new VibeNetServer(p, bindAddress: IPAddress.IPv6Loopback))
            using (var client = new VibeNetClient("::1", p))
            {
                Assert((await server.StartAsync()).Success && (await client.StartAsync()).Success, "IPv6 mandatory handshake");
                Assert(await client.SendAsync(Array.Empty<byte>(), VibeNetTransport.UDP) == SendResult.Sent, "empty IPv6 UDP");
                await Until(() => server.TryDequeueMessage(out _));
                await client.DisconnectAsync();
                await server.StopAsync();
            }
        }
    }
    static async Task NetworkTests()
    {
        int p = Port();
        using (var server = new VibeNetServer(p, bindAddress: IPAddress.Loopback))
        using (var client = new VibeNetClient("127.0.0.1", p))
        {
            Assert((await server.StartAsync()).Success, "server start");
            var start = await client.StartAsync();
            if (!start.Success)
            {
                while (server.TryDequeueFailure(out var failure))
                    Console.WriteLine("SERVER " + failure.Detail);
            }
            Assert(start.Success, "client start: " + start.Failure?.Detail);
            await Until(() => server.ConnectedClientCount == 1);
            Assert(await client.SendAsync(new byte[] { 1, 2 }, VibeNetTransport.TCP) == SendResult.Sent, "tcp send");
            VibeNetMessage m = default;
            await Until(() => server.TryDequeueMessage(out m));
            Assert(m.Data.SequenceEqual(new byte[] { 1, 2 }) && m.ConnectionId == client.ConnectionId, "tcp receive");
            Assert(await client.SendAsync(new byte[] { 3 }, VibeNetTransport.UDP) == SendResult.Sent, "udp send");
            await Until(() => server.TryDequeueMessage(out m));
            Assert(m.Data[0] == 3 && m.Transport == VibeNetTransport.UDP, "udp receive");
            Assert(await server.SendAsync(client.ConnectionId, new byte[] { 4 }, VibeNetTransport.TCP) == SendResult.Sent, "server send");
            await Until(() => client.TryDequeueMessage(out m));
            Assert(m.Data[0] == 4, "server receive");
            await server.DisconnectClientAsync(client.ConnectionId);
            DisconnectInfo d = default;
            await Until(() => client.TryDequeueDisconnected(out d));
            if (d.Reason != DisconnectReason.RemoteRequested)
            {
                while (client.TryDequeueFailure(out var f))
                    Console.WriteLine("CLIENT CLOSE: " + f.Detail);
                while (server.TryDequeueFailure(out var f))
                    Console.WriteLine("SERVER CLOSE: " + f.Detail);
            }
            Assert(d.Reason == DisconnectReason.RemoteRequested, "graceful reason " + d.Reason);
            await client.DisconnectAsync();
            await server.StopAsync();
            Assert(server.Statistics.BufferedBytes == 0 && client.Statistics.BufferedBytes == 0, "leases released");
        }
    }
}


