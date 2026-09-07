using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using VibeNet;
internal static partial class Program
{
    static async Task Echo(VibeNetServer server, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            int drained = 0;
            while (drained++ < 1024 && server.TryDequeueMessage(out var m))
                await server.SendAsync(m.ConnectionId, m.Data, m.Transport);
            await Task.Delay(1);
        }
    }
    static async Task<int> RunCommand(string[] args)
    {
        if (args[0] == "--serve")
        {
            using (var server = new VibeNetServer(int.Parse(args[1]), bindAddress: IPAddress.Loopback))
            using (var stop = new CancellationTokenSource())
            {
                var result = await server.StartAsync();
                if (!result.Success)
                    throw new Exception(result.Failure?.Detail);
                Console.WriteLine("READY");
                var pump = Echo(server, stop.Token);
                await Task.Delay(TimeSpan.FromSeconds(int.Parse(args[2])));
                stop.Cancel();
                await pump;
                await server.StopAsync();
                return 0;
            }
        }
        if (args[0] == "--client")
        {
            using (var client = new VibeNetClient(args[1], int.Parse(args[2])))
            {
                var start = await client.StartAsync();
                if (!start.Success)
                    throw new Exception(start.Failure?.Detail);
                foreach (var transport in new[] { VibeNetTransport.TCP, VibeNetTransport.UDP })
                {
                    Assert(await client.SendAsync(new byte[] { 42, 1, 99 }, transport) == SendResult.Sent, "interop send");
                    VibeNetMessage m = default;
                    await Until(() => client.TryDequeueMessage(out m));
                    Assert(m.Data.Length == 3 && m.Data[0] == 42 && m.Transport == transport, "interop echo");
                }
                await client.DisconnectAsync();
                Console.WriteLine("PASS cross-runtime TCP/UDP");
                return 0;
            }
        }
        if (args[0] != "--load" || args.Length != 3)
            throw new ArgumentException("Use --load CLIENTS SECONDS, --serve PORT SECONDS, or --client HOST PORT.");
        int count = int.Parse(args[1]), seconds = int.Parse(args[2]);
        if (count < 1 || count > 10000 || seconds < 1 || seconds > 86400)
            throw new ArgumentOutOfRangeException("load bounds");
        int port = Port();
        var clients = new List<VibeNetClient>();
        var config = new VibeNetConfiguration(new VibeNetLimits(maxQueuedMessages: 65536, maxConnectionAttemptsPerSecond: 256, maxAttemptsPerAddressPerSecond: 256));
        using (var server = new VibeNetServer(port, bindAddress: IPAddress.Loopback, configuration: config))
        using (var stop = new CancellationTokenSource())
        {
            await server.StartAsync();
            var setup = Stopwatch.StartNew();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var c = new VibeNetClient("127.0.0.1", port);
                    clients.Add(c);
                    var result = await c.StartAsync();
                    if (!result.Success)
                        throw new Exception("Client " + i + ": " + result.Failure?.Detail);
                }
                await Until(() => server.ConnectedClientCount == count);
                setup.Stop();
                var pump = Echo(server, stop.Token);
                long sent = 0, received = 0, failed = 0, peak = 0;
                long[] histogram = new long[1001];
                int[] gc = { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                long heapStart = GC.GetTotalMemory(true);
                var timer = Stopwatch.StartNew();
                double nextSend = 0;
                while (timer.Elapsed.TotalSeconds < seconds)
                {
                    if (timer.Elapsed.TotalMilliseconds >= nextSend)
                    {
                        nextSend += 50;
                        foreach (var c in clients)
                        {
                            var payload = new byte[64];
                            Buffer.BlockCopy(BitConverter.GetBytes(Stopwatch.GetTimestamp()), 0, payload, 0, 8);
                            if (await c.SendAsync(payload, VibeNetTransport.UDP) == SendResult.Sent)
                                sent++;
                            else
                                failed++;
                        }
                    }
                    foreach (var c in clients)
                    while (c.TryDequeueMessage(out var m))
                    {
                        double ms = (Stopwatch.GetTimestamp() - BitConverter.ToInt64(m.Data, 0)) * 1000.0 / Stopwatch.Frequency;
                        histogram[Math.Min(1000, Math.Max(0, (int)Math.Ceiling(ms)))]++;
                        received++;
                    }
                    peak = Math.Max(peak, server.Statistics.BufferedBytes);
                    await Task.Delay(1);
                }
                stop.Cancel();
                await pump;
                Console.WriteLine("runtime=" + Environment.Version + " clients=" + count + " seconds=" + seconds + " payload=64 rate=20Hz setupSeconds=" + setup.Elapsed.TotalSeconds.ToString("F2"));
                Console.WriteLine("sent=" + sent + " received=" + received + " failedSends=" + failed + " serverUdpDrops=" + server.Statistics.DroppedUdp + " peakServerPayloadBudget=" + peak);
                Console.WriteLine("echoLatencyCeilingMs p50=" + Quantile(histogram, received, .50) + " p95=" + Quantile(histogram, received, .95) + " p99=" + Quantile(histogram, received, .99) + " (1000 bucket includes all >=1000ms)");
                Console.WriteLine("managedHeapBefore=" + HeapValue(heapStart) + " managedHeapAfterFullCollection=" + HeapValue(GC.GetTotalMemory(true)) + " GC=" + (GC.CollectionCount(0) - gc[0]) + "/" + (GC.CollectionCount(1) - gc[1]) + "/" + (GC.CollectionCount(2) - gc[2]));
                foreach (var c in clients)
                    await c.DisconnectAsync();
                await server.StopAsync();
                Assert(server.Statistics.BufferedBytes == 0, "load teardown budget");
                return failed == 0 && received != 0 ? 0 : 1;
            }
            finally { stop.Cancel(); foreach (var c in clients) c.Dispose(); await server.StopAsync(); }
        }
    }
    static string HeapValue(long value) => value < 0 ? "unavailable(runtime returned negative)" : value.ToString();
    static int Quantile(long[] buckets, long count, double p)
    {
        if (count == 0)
            return -1;
        long target = (long)Math.Ceiling(count * p), total = 0;
        for (int i = 0; i < buckets.Length; i++)
        {
            total += buckets[i];
            if (total >= target)
                return i;
        }
        return 1000;
    }
}


