// VibeNet 1. No peer identity authentication: an active handshake MITM is possible.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#nullable enable
namespace VibeNet
{
    public enum VibeNetTransport
    {
        TCP, UDP
    }
    public enum SendResult
    {
        Sent, NotConnected, Backpressured, Failed
    }
    public enum ConnectionState
    {
        Created, Connecting, Connected, Closing, Closed
    }
    public enum FailureCode
    {
        Socket, Protocol, Integrity, Handshake, Timeout, ResourceLimit, UnsupportedRuntime, Internal
    }
    public enum DisconnectReason
    {
        LocalRequested, RemoteRequested, ServerStopped, ConnectionLost, Timeout, ProtocolError, ResourceLimit
    }
    public sealed class VibeNetLimits
    {
        public int MaxClients
        {
            get;
        }
        public int MaxPendingHandshakes
        {
            get;
        }
        public int MaxTcpPayloadBytes
        {
            get;
        }
        public int MaxUdpPayloadBytes
        {
            get;
        }
        public int MaxQueuedMessages
        {
            get;
        }
        public int MaxMessagesPerConnection
        {
            get;
        }
        public long MaxQueuedBytes
        {
            get;
        }
        public long MaxBufferedBytes
        {
            get;
        }
        public long MaxBufferedBytesPerConnection
        {
            get;
        }
        public int MaxPendingSends
        {
            get;
        }
        public int MaxPendingSendsPerConnection
        {
            get;
        }
        public int MaxEvents
        {
            get;
        }
        public int MaxPacketsPerSecond
        {
            get;
        }
        public int MaxPacketsPerSecondPerConnection
        {
            get;
        }
        public long MaxBytesPerSecond
        {
            get;
        }
        public int MaxConnectionAttemptsPerSecond
        {
            get;
        }
        public int MaxAttemptsPerAddressPerSecond
        {
            get;
        }
        public int MaxTrackedAddresses
        {
            get;
        }
        public VibeNetLimits(int maxClients = 10000, int maxPendingHandshakes = 8,
            int maxTcpPayloadBytes = 1048576, int maxUdpPayloadBytes = 1024,
            int maxQueuedMessages = 8192, int maxMessagesPerConnection = 256,
            long maxQueuedBytes = 67108864, long maxBufferedBytes = 134217728,
            long maxBufferedBytesPerConnection = 8388608, int maxPendingSends = 1024,
            int maxPendingSendsPerConnection = 64, int maxEvents = 2048,
            int maxPacketsPerSecond = 250000, int maxPacketsPerSecondPerConnection = 1000,
            long maxBytesPerSecond = 134217728, int maxConnectionAttemptsPerSecond = 64,
            int maxAttemptsPerAddressPerSecond = 16, int maxTrackedAddresses = 4096)
        {
            MaxClients = Positive(maxClients);
            MaxPendingHandshakes = Positive(maxPendingHandshakes);
            MaxTcpPayloadBytes = Positive(maxTcpPayloadBytes);
            MaxUdpPayloadBytes = Positive(maxUdpPayloadBytes);
            if (maxTcpPayloadBytes > 16777216 || maxUdpPayloadBytes > 65407)
                throw new ArgumentOutOfRangeException("payload size");
            MaxQueuedMessages = Positive(maxQueuedMessages);
            MaxMessagesPerConnection = Positive(maxMessagesPerConnection);
            MaxQueuedBytes = Positive(maxQueuedBytes);
            MaxBufferedBytes = Positive(maxBufferedBytes);
            MaxBufferedBytesPerConnection = Positive(maxBufferedBytesPerConnection);
            if (maxQueuedBytes > maxBufferedBytes || maxBufferedBytesPerConnection > maxBufferedBytes)
                throw new ArgumentException("Inconsistent byte budgets.");
            MaxPendingSends = Positive(maxPendingSends);
            MaxPendingSendsPerConnection = Positive(maxPendingSendsPerConnection);
            MaxEvents = Positive(maxEvents);
            MaxPacketsPerSecond = Positive(maxPacketsPerSecond);
            MaxPacketsPerSecondPerConnection = Positive(maxPacketsPerSecondPerConnection);
            MaxBytesPerSecond = Positive(maxBytesPerSecond);
            MaxConnectionAttemptsPerSecond = Positive(maxConnectionAttemptsPerSecond);
            MaxAttemptsPerAddressPerSecond = Positive(maxAttemptsPerAddressPerSecond);
            MaxTrackedAddresses = Positive(maxTrackedAddresses);
        }
        private static int Positive(int n) => n > 0 ? n : throw new ArgumentOutOfRangeException(nameof(n));
        private static long Positive(long n) => n > 0 ? n : throw new ArgumentOutOfRangeException(nameof(n));
    }
    public sealed class VibeNetConfiguration
    {
        public VibeNetLimits Limits
        {
            get;
        }
        public TimeSpan ConnectTimeout
        {
            get;
        }
        public TimeSpan HeartbeatInterval
        {
            get;
        }
        public TimeSpan IdleTimeout
        {
            get;
        }
        public TimeSpan SendTimeout
        {
            get;
        }
        public TimeSpan UdpRetryInterval
        {
            get;
        }
        public VibeNetConfiguration(VibeNetLimits? limits = null, TimeSpan? connectTimeout = null,
            TimeSpan? heartbeatInterval = null, TimeSpan? idleTimeout = null,
            TimeSpan? sendTimeout = null, TimeSpan? udpRetryInterval = null)
        {
            Limits = limits ?? new VibeNetLimits();
            ConnectTimeout = Duration(connectTimeout ?? TimeSpan.FromSeconds(60));
            HeartbeatInterval = Duration(heartbeatInterval ?? TimeSpan.FromSeconds(2));
            IdleTimeout = Duration(idleTimeout ?? TimeSpan.FromSeconds(15));
            SendTimeout = Duration(sendTimeout ?? TimeSpan.FromSeconds(5));
            UdpRetryInterval = Duration(udpRetryInterval ?? TimeSpan.FromMilliseconds(250));
            if (IdleTimeout <= HeartbeatInterval)
                throw new ArgumentException("IdleTimeout must exceed HeartbeatInterval.");
        }
        private static TimeSpan Duration(TimeSpan t) => t.TotalMilliseconds >= 1 && t.TotalMilliseconds <= int.MaxValue - 1 ? t : throw new ArgumentOutOfRangeException(nameof(t));
        internal int MaxClients => Limits.MaxClients;
        internal int MaxPendingHandshakes => Limits.MaxPendingHandshakes;
        internal int MaxTcpPayloadBytes => Limits.MaxTcpPayloadBytes;
        internal int MaxUdpPayloadBytes => Limits.MaxUdpPayloadBytes;
        internal int MaxQueuedMessages => Limits.MaxQueuedMessages;
        internal int MaxMessagesPerConnection => Limits.MaxMessagesPerConnection;
        internal long MaxQueuedBytes => Limits.MaxQueuedBytes;
        internal long MaxBufferedBytes => Limits.MaxBufferedBytes;
        internal long MaxBufferedBytesPerConnection => Limits.MaxBufferedBytesPerConnection;
        internal int MaxPendingSends => Limits.MaxPendingSends;
        internal int MaxPendingSendsPerConnection => Limits.MaxPendingSendsPerConnection;
        internal int MaxEvents => Limits.MaxEvents;
        internal int MaxPacketsPerSecond => Limits.MaxPacketsPerSecond;
        internal int MaxPacketsPerSecondPerConnection => Limits.MaxPacketsPerSecondPerConnection;
        internal long MaxBytesPerSecond => Limits.MaxBytesPerSecond;
        internal int MaxConnectionAttemptsPerSecond => Limits.MaxConnectionAttemptsPerSecond;
        internal int MaxAttemptsPerAddressPerSecond => Limits.MaxAttemptsPerAddressPerSecond;
        internal int MaxTrackedAddresses => Limits.MaxTrackedAddresses;
    }
    public readonly struct VibeNetMessage
    {
        public Guid ConnectionId
        {
            get;
        }
        public VibeNetTransport Transport
        {
            get;
        }
        public byte[] Data
        {
            get;
        }
        internal VibeNetMessage(Guid id, VibeNetTransport transport, byte[] data)
        {
            ConnectionId = id;
            Transport = transport;
            Data = data;
        }
    }
    public readonly struct VibeNetFailure
    {
        public Guid? ConnectionId
        {
            get;
        }
        public FailureCode Code
        {
            get;
        }
        public string Detail
        {
            get;
        }
        internal VibeNetFailure(Guid? id, FailureCode code, string detail)
        {
            ConnectionId = id;
            Code = code;
            Detail = detail;
        }
    }
    public readonly struct StartResult
    {
        public bool Success
        {
            get;
        }
        public VibeNetFailure? Failure
        {
            get;
        }
        internal StartResult(VibeNetFailure? failure)
        {
            Success = !failure.HasValue;
            Failure = failure;
        }
    }
    public readonly struct ConnectionInfo
    {
        public Guid Id
        {
            get;
        }
        public IPEndPoint TcpEndpoint
        {
            get;
        }
        public IPEndPoint? UdpEndpoint
        {
            get;
        }
        public ConnectionState State
        {
            get;
        }
        internal ConnectionInfo(Link c)
        {
            Id = c.Id;
            TcpEndpoint = new IPEndPoint(c.TcpEndpoint.Address, c.TcpEndpoint.Port);
            var u = c.UdpEndpoint;
            UdpEndpoint = u == null ? null : new IPEndPoint(u.Address, u.Port);
            State = c.State;
        }
    }
    public readonly struct DisconnectInfo
    {
        public Guid ConnectionId
        {
            get;
        }
        public DisconnectReason Reason
        {
            get;
        }
        internal DisconnectInfo(Guid id, DisconnectReason reason)
        {
            ConnectionId = id;
            Reason = reason;
        }
    }
    public readonly struct BroadcastResult
    {
        public int Sent
        {
            get;
        }
        public int NotConnected
        {
            get;
        }
        public int Backpressured
        {
            get;
        }
        public int Failed
        {
            get;
        }
        internal BroadcastResult(int[] n)
        {
            Sent = n[0];
            NotConnected = n[1];
            Backpressured = n[2];
            Failed = n[3];
        }
    }
    public readonly struct NetworkStatistics
    {
        public long BufferedBytes
        {
            get;
        }
        public int QueuedMessages
        {
            get;
        }
        public long QueuedBytes
        {
            get;
        }
        public long DroppedUdp
        {
            get;
        }
        public long LostEvents
        {
            get;
        }
        public int PendingSends
        {
            get;
        }
        internal NetworkStatistics(VibeNetNode n)
        {
            BufferedBytes = n.Budget.Used;
            QueuedMessages = n.Messages.Count;
            QueuedBytes = n.Messages.Bytes;
            DroppedUdp = Interlocked.Read(ref n.Dropped);
            LostEvents = n.EventLoss;
            PendingSends = Volatile.Read(ref n.PendingSends);
        }
    }
    public abstract class VibeNetNode : IDisposable
    {
        public VibeNetConfiguration Configuration
        {
            get;
        }
        public NetworkStatistics Statistics => new NetworkStatistics(this);
        internal readonly Budget Budget;
        internal readonly MessageQueue Messages;
        internal readonly Ring<VibeNetFailure> Failures;
        internal readonly Ring<DisconnectInfo> Disconnects;
        internal readonly CancellationTokenSource Lifetime = new CancellationTokenSource();
        internal readonly RateGate InputRate;
        internal readonly RateGate TcpInputRate;
        internal readonly SemaphoreSlim DatagramGate = new SemaphoreSlim(1, 1);
        internal int PendingSends;
        internal long Dropped;
        internal abstract long EventLoss
        {
            get;
        }
        private protected VibeNetNode(VibeNetConfiguration? config)
        {
            Configuration = config ?? new VibeNetConfiguration();
            Budget = new Budget(Configuration.MaxBufferedBytes);
            Messages = new MessageQueue(Configuration);
            Failures = new Ring<VibeNetFailure>(Configuration.MaxEvents);
            Disconnects = new Ring<DisconnectInfo>(Configuration.MaxEvents);
            InputRate = new RateGate(Configuration.MaxPacketsPerSecond, Configuration.MaxBytesPerSecond);
            TcpInputRate = new RateGate(Configuration.MaxPacketsPerSecond, Configuration.MaxBytesPerSecond);
        }
        public bool TryDequeueMessage(out VibeNetMessage message) => Messages.Take(out message);
        public bool TryDequeueFailure(out VibeNetFailure failure) => Failures.Take(out failure);
        public bool TryDequeueDisconnected(out DisconnectInfo info) => Disconnects.Take(out info);
        public abstract void Dispose();
        internal void Report(Guid? id, FailureCode code, string detail) => Failures.Add(new VibeNetFailure(id, code, detail));
        internal abstract Task<int> SendDatagram(byte[] packet, IPEndPoint endpoint);
        internal abstract void Closed(Link link, DisconnectReason reason, bool wasConnected);
        internal void Validate(byte[] data, VibeNetTransport transport)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (transport != VibeNetTransport.TCP && transport != VibeNetTransport.UDP)
                throw new ArgumentOutOfRangeException(nameof(transport));
            if (data.Length > (transport == VibeNetTransport.TCP ? Configuration.MaxTcpPayloadBytes : Configuration.MaxUdpPayloadBytes))
                throw new ArgumentOutOfRangeException(nameof(data));
        }
        internal static void Port(int port)
        {
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port));
        }
    }
    internal sealed class Budget
    {
        private long used; private readonly long maximum;
        internal long Used => Interlocked.Read(ref used);
        internal Budget(long max)
        {
            maximum = max;
        }
        internal bool Acquire(long size)
        {
            while (true)
            {
                long n = Used;
                if (size < 0 || size > maximum - n)
                    return false;
                if (Interlocked.CompareExchange(ref used, n + size, n) == n)
                    return true;
            }
        }
        internal void Release(long size)
        {
            Interlocked.Add(ref used, -size);
        }
    }
    internal sealed class Lease : IDisposable
    {
        private Budget? global; private readonly Budget local; private readonly long size;
        private Lease(Budget g, Budget l, long n)
        {
            global = g;
            local = l;
            size = n;
        }
        internal static Lease? Try(Budget g, Budget l, long n)
        {
            if (!l.Acquire(n))
                return null;
            if (!g.Acquire(n))
            {
                l.Release(n);
                return null;
            }
            return new Lease(g, l, n);
        }
        public void Dispose()
        {
            var g = Interlocked.Exchange(ref global, null);
            if (g != null)
            {
                local.Release(size);
                g.Release(size);
            }
        }
    }
    internal sealed class Ring<T>
    {
        private readonly object gate = new object(); private readonly Queue<T> q = new Queue<T>(); private readonly int max; internal long Lost;
        internal Ring(int n)
        {
            max = n;
        }
        internal void Add(T item)
        {
            lock (gate)
            {
                if (q.Count == max)
                {
                    q.Dequeue();
                    Interlocked.Increment(ref Lost);
                }
                q.Enqueue(item);
            }
        }
        internal bool Take(out T item)
        {
            lock (gate)
            {
                if (q.Count == 0)
                {
                    item = default!;
                    return false;
                }
                item = q.Dequeue();
                return true;
            }
        }
    }
    internal sealed class RateGate
    {
        private readonly object gate = new object(); private readonly double pmax, bmax; private double packets, bytes; private long stamp;
        internal RateGate(double p, double b)
        {
            pmax = packets = p;
            bmax = bytes = b;
            stamp = Clock.Now;
        }
        internal bool Take(int n)
        {
            lock (gate)
            {
                long now = Clock.Now;
                double dt = (now - stamp) / (double)Stopwatch.Frequency;
                stamp = now;
                packets = Math.Min(pmax, packets + dt * pmax);
                bytes = Math.Min(bmax, bytes + dt * bmax);
                if (packets < 1 || bytes < n)
                    return false;
                packets--;
                bytes -= n;
                return true;
            }
        }
    }
    internal static class Clock
    {
        internal static long Now => Stopwatch.GetTimestamp();
        internal static long Ticks(TimeSpan d) => (long)(d.TotalSeconds * Stopwatch.Frequency);
        internal static bool Elapsed(long at, TimeSpan d) => Now - at >= Ticks(d);
    }
    internal sealed class MessageQueue
    {
        private sealed class Entry
        {
            internal VibeNetMessage Message; internal Lease Lease = null!; internal Entry? Prev, Next, OwnerPrev, OwnerNext, UPrev, UNext; internal Link Owner = null!;
        }
        private sealed class OwnerEntries
        {
            internal Entry? Head, Tail; internal int Count;
        }
        private readonly object gate = new object(); private readonly Dictionary<Link, OwnerEntries> owners = new Dictionary<Link, OwnerEntries>();
        private Entry? head, tail, udpHead, udpTail; private int count; private long bytes; private readonly VibeNetConfiguration config;
        internal int Count
        {
            get
            {
                lock (gate)
                    return count;
            }
        }
        internal long Bytes
        {
            get
            {
                lock (gate)
                    return bytes;
            }
        }
        internal MessageQueue(VibeNetConfiguration c)
        {
            config = c;
        }
        internal bool Add(Link owner, byte[] data, VibeNetTransport transport)
        {
            lock (gate)
            {
                if (owner.State != ConnectionState.Connected)
                    return false;
                if (!owners.TryGetValue(owner, out var o))
                {
                    o = new OwnerEntries();
                    owners.Add(owner, o);
                }
                if (o.Count >= config.MaxMessagesPerConnection)
                    return false;
                while (count >= config.MaxQueuedMessages || data.Length > config.MaxQueuedBytes - bytes)
                {
                    if (transport == VibeNetTransport.UDP || udpHead == null)
                        return false;
                    Remove(udpHead);
                    Interlocked.Increment(ref owner.Node.Dropped);
                }
                var lease = Lease.Try(owner.Node.Budget, owner.Memory, data.Length);
                if (lease == null)
                    return false;
                // Remove() can unlink an empty owner entry during UDP eviction.
                if (!owners.TryGetValue(owner, out o))
                {
                    o = new OwnerEntries();
                    owners.Add(owner, o);
                }
                var e = new Entry { Message = new VibeNetMessage(owner.Id, transport, data), Lease = lease, Owner = owner, Prev = tail, OwnerPrev = o.Tail };
                if (tail != null)
                    tail.Next = e;
                else
                    head = e;
                tail = e;
                if (o.Tail != null)
                    o.Tail.OwnerNext = e;
                else
                    o.Head = e;
                o.Tail = e;
                o.Count++;
                if (transport == VibeNetTransport.UDP)
                {
                    e.UPrev = udpTail;
                    if (udpTail != null)
                        udpTail.UNext = e;
                    else
                        udpHead = e;
                    udpTail = e;
                }
                count++;
                bytes += data.Length;
                return true;
            }
        }
        internal bool Take(out VibeNetMessage message)
        {
            lock (gate)
            {
                if (head == null)
                {
                    message = default;
                    return false;
                }
                message = head.Message;
                Remove(head);
                return true;
            }
        }
        internal void Close(Link owner)
        {
            lock (gate)
            {
                if (owners.TryGetValue(owner, out var o))
                {
                    while (o.Head != null)
                        Remove(o.Head);
                    owners.Remove(owner);
                }
            }
        }
        private void Remove(Entry e)
        {
            if (e.Prev != null)
                e.Prev.Next = e.Next;
            else
                head = e.Next;
            if (e.Next != null)
                e.Next.Prev = e.Prev;
            else
                tail = e.Prev;
            var o = owners[e.Owner];
            if (e.OwnerPrev != null)
                e.OwnerPrev.OwnerNext = e.OwnerNext;
            else
                o.Head = e.OwnerNext;
            if (e.OwnerNext != null)
                e.OwnerNext.OwnerPrev = e.OwnerPrev;
            else
                o.Tail = e.OwnerPrev;
            if (e.Message.Transport == VibeNetTransport.UDP)
            {
                if (e.UPrev != null)
                    e.UPrev.UNext = e.UNext;
                else
                    udpHead = e.UNext;
                if (e.UNext != null)
                    e.UNext.UPrev = e.UPrev;
                else
                    udpTail = e.UPrev;
            }
            o.Count--;
            if (o.Count == 0)
                owners.Remove(e.Owner);
            count--;
            bytes -= e.Message.Data.Length;
            e.Lease.Dispose();
        }
    }
}
namespace VibeNet
{
    internal enum Kind : byte
    {
        Data = 1, Register = 2, Registered = 3, Ready = 4, ReadyAck = 5, Ping = 6, Pong = 7, Bye = 8, TcpUpdate = 9, UdpUpdate = 10, UdpUpdated = 11, ByeAck = 12
    }
    internal sealed class ProtocolException : IOException
    {
        internal ProtocolException(string s) : base(s) { }
    }
    internal static class Crypto
    {
        internal const int RsaBytes = 256;
        internal static byte[] Random(int n)
        {
            var b = new byte[n];
            RandomNumberGenerator.Fill(b);
            return b;
        }
        internal static void Clear(byte[]? b)
        {
            if (b != null)
                CryptographicOperations.ZeroMemory(b);
        }
        internal static byte[] Hash(byte[] b)
        {
            using (var h = SHA256.Create())
                return h.ComputeHash(b);
        }
        internal static byte[] Mac(byte[] key, byte[] b)
        {
            using (var h = new HMACSHA256(key))
                return h.ComputeHash(b);
        }
        internal static byte[] Join(params byte[][] parts)
        {
            int n = 0;
            foreach (var p in parts)
                n = checked(n + p.Length);
            var b = new byte[n];
            n = 0;
            foreach (var p in parts)
            {
                Buffer.BlockCopy(p, 0, b, n, p.Length);
                n += p.Length;
            }
            return b;
        }
        internal static byte[] Hkdf(byte[] salt, byte[] ikm, byte[] info, int count)
        {
            if (count < 1 || count > 255 * 32)
                throw new ArgumentOutOfRangeException(nameof(count));
            byte[] prk = Mac(salt, ikm), previous = Array.Empty<byte>(), result = new byte[count];
            try
            {
                int offset = 0;
                byte index = 1;
                while (offset < count)
                {
                    byte[] input = Join(previous, info, new[] { index++ });
                    Clear(previous);
                    previous = Mac(prk, input);
                    Clear(input);
                    int n = Math.Min(32, count - offset);
                    Buffer.BlockCopy(previous, 0, result, offset, n);
                    offset += n;
                }
                return result;
            }
            finally { Clear(prk); Clear(previous); }
        }
        internal static byte[] Expand(byte[] root, string label) => Hkdf(Array.Empty<byte>(), root, Encoding.ASCII.GetBytes("VNS1/" + label), 32);
        internal static bool Equal(byte[] a, int offset, byte[] b) => offset >= 0 && a.Length - offset >= b.Length && CryptographicOperations.FixedTimeEquals(new ReadOnlySpan<byte>(a, offset, b.Length), b);
        internal static RSA NewRsa()
        {
            RSA r = RSA.Create(RsaBytes * 8);
            if (r.ExportParameters(false).Modulus!.Length == RsaBytes)
                return r;
            // Unity Mono's generic factory can ignore the requested size. Never accept that key.
            r.Dispose();
            var csp = new RSACryptoServiceProvider(RsaBytes * 8);
            csp.PersistKeyInCsp = false;
            if (csp.ExportParameters(false).Modulus!.Length != RsaBytes)
            {
                csp.Dispose();
                throw new PlatformNotSupportedException("RSA-2048 provider unavailable.");
            }
            return csp;
        }
    }
    internal static class Wire
    {
        internal const uint Magic = 0x564e5331; // VNS1: intentionally different from the plaintext prototype.
        internal const int Header = 64, Tag = 32;
        internal static int Size(int n) => checked(Header + (n / 16 + 1) * 16 + Tag);
        internal static void U32(byte[] b, int o, uint n)
        {
            for (int i = 3; i >= 0; i--)
            {
                b[o + i] = (byte)n;
                n >>= 8;
            }
        }
        internal static uint U32(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        internal static void U64(byte[] b, int o, ulong n)
        {
            for (int i = 7; i >= 0; i--)
            {
                b[o + i] = (byte)n;
                n >>= 8;
            }
        }
        internal static ulong U64(byte[] b, int o)
        {
            ulong n = 0;
            for (int i = 0; i < 8; i++)
                n = (n << 8) | b[o + i];
            return n;
        }
        internal static Guid Id(byte[] b) => new Guid(new ReadOnlySpan<byte>(b, 8, 16));
        internal static bool HeaderValid(byte[] b, VibeNetTransport lane, int max)
        {
            if (b.Length < Header || U32(b, 0) != Magic || b[4] != 1 || b[6] != (byte)lane || b[7] != 0 || U32(b, 44) != 0)
                return false;
            int plain = unchecked((int)U32(b, 36));
            if (plain < 0 || plain > max || U32(b, 40) != (uint)((plain / 16 + 1) * 16))
                return false;
            var k = (Kind)b[5];
            if (k == Kind.Data)
                return true;
            if (lane == VibeNetTransport.UDP)
                return (k == Kind.Register || k == Kind.Registered) && plain == 0;
            return ((k == Kind.Ready || k == Kind.ReadyAck || k == Kind.Ping || k == Kind.Pong || k == Kind.TcpUpdate || k == Kind.ByeAck) && plain == 0)
                || (k == Kind.Bye && plain == 1) || ((k == Kind.UdpUpdate || k == Kind.UdpUpdated) && plain == 4);
        }
        internal static async Task ReadExact(Stream s, byte[] b, int offset, int count, CancellationToken token)
        {
            while (count > 0)
            {
                int n = await s.ReadAsync(b, offset, count, token).ConfigureAwait(false);
                if (n == 0)
                    throw new EndOfStreamException();
                offset += n;
                count -= n;
            }
        }
        internal static async Task PlainWrite(Stream s, byte type, byte[] p, CancellationToken ct)
        {
            var b = new byte[12 + p.Length];
            U32(b, 0, Magic);
            b[4] = 1;
            b[5] = type;
            U32(b, 8, (uint)p.Length);
            Buffer.BlockCopy(p, 0, b, 12, p.Length);
            await s.WriteAsync(b, 0, b.Length, ct).ConfigureAwait(false);
        }
        internal static async Task<byte[]> PlainRead(Stream s, byte type, int length, CancellationToken ct)
        {
            var h = new byte[12];
            await ReadExact(s, h, 0, 12, ct).ConfigureAwait(false);
            if (U32(h, 0) != Magic || h[4] != 1 || h[5] != type || h[6] != 0 || h[7] != 0 || U32(h, 8) != length)
                throw new ProtocolException("Invalid handshake frame.");
            var p = new byte[length];
            await ReadExact(s, p, 0, length, ct).ConfigureAwait(false);
            return p;
        }
        internal static async Task Wait(Task task, CancellationToken ct)
        {
            if (!ct.CanBeCanceled)
            {
                await task.ConfigureAwait(false);
                return;
            }
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => canceled.TrySetCanceled()))
            {
                if (await Task.WhenAny(task, canceled.Task).ConfigureAwait(false) != task)
                {
                    _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    ct.ThrowIfCancellationRequested();
                }
                await task.ConfigureAwait(false);
            }
        }
    }
    internal sealed class Lane : IDisposable
    {
        internal const ulong RecordLimit = 65536;
        internal const long ByteLimit = 67108864;
        internal readonly uint Epoch; private byte[] secret; private readonly byte[] enc, mac;
        private readonly Aes aes; private readonly HMACSHA256 hmac;
        internal ulong Sequence; internal long Bytes;
        private ulong high; private bool any; private readonly ulong[] seen = new ulong[256];
        internal bool NeedsUpdate => Sequence >= RecordLimit || Bytes >= ByteLimit;
        internal Lane(byte[] root, uint epoch = 0)
        {
            secret = root;
            Epoch = epoch;
            enc = Crypto.Expand(root, "encryption");
            mac = Crypto.Expand(root, "authentication");
            aes = Aes.Create();
            aes.Key = enc;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            hmac = new HMACSHA256(mac);
            for (int i = 0; i < seen.Length; i++)
                seen[i] = ulong.MaxValue;
        }
        internal Lane Next()
        {
            if (Epoch == uint.MaxValue)
                throw new ProtocolException("Key epoch exhausted.");
            return new Lane(Crypto.Expand(secret, "next"), Epoch + 1);
        }
        internal byte[] Seal(Guid id, Kind type, VibeNetTransport transport, byte[] plaintext)
        {
            if (Sequence >= RecordLimit + 64 || Bytes > ByteLimit + 16777216L + 4096)
                throw new ProtocolException("Key usage exhausted.");
            byte[] b = new byte[Wire.Size(plaintext.Length)];
            Wire.U32(b, 0, Wire.Magic);
            b[4] = 1;
            b[5] = (byte)type;
            b[6] = (byte)transport;
            id.TryWriteBytes(new Span<byte>(b, 8, 16));
            Wire.U32(b, 24, Epoch);
            Wire.U64(b, 28, Sequence++);
            Wire.U32(b, 36, (uint)plaintext.Length);
            Wire.U32(b, 40, (uint)(b.Length - Wire.Header - Wire.Tag));
            var iv = Crypto.Random(16);
            Buffer.BlockCopy(iv, 0, b, 48, 16);
            using (var transform = aes.CreateEncryptor(enc, iv))
            {
                var c = transform.TransformFinalBlock(plaintext, 0, plaintext.Length);
                Buffer.BlockCopy(c, 0, b, 64, c.Length);
            }
            byte[] tag = hmac.ComputeHash(b, 0, b.Length - 32);
            Buffer.BlockCopy(tag, 0, b, b.Length - 32, 32);
            Bytes += plaintext.Length;
            return b;
        }
        internal byte[]? Open(byte[] b, Guid id, VibeNetTransport transport, int max)
        {
            if (!Wire.HeaderValid(b, transport, max) || Wire.Id(b) != id || Wire.U32(b, 24) != Epoch || b.Length != Wire.Size((int)Wire.U32(b, 36)))
                return null;
            ulong seq = Wire.U64(b, 28);
            if (seq >= RecordLimit + 64 || Bytes + Wire.U32(b, 36) > ByteLimit + max + 4096L)
                return null;
            if (transport == VibeNetTransport.TCP)
            {
                if (seq != Sequence)
                    return null;
            }
            else if ((any && high >= 256 && seq <= high - 256) || seen[seq % 256] == seq)
                return null;
            byte[] tag = hmac.ComputeHash(b, 0, b.Length - 32);
            if (!Crypto.Equal(b, b.Length - 32, tag))
                return null;
            // Encrypt-then-MAC: no padding processing or decryption before MAC verification.
            var iv = new byte[16];
            Buffer.BlockCopy(b, 48, iv, 0, 16);
            byte[] p;
            using (var t = aes.CreateDecryptor(enc, iv))
                p = t.TransformFinalBlock(b, 64, b.Length - 96);
            if (p.Length != Wire.U32(b, 36))
            {
                Crypto.Clear(p);
                return null;
            }
            if (transport == VibeNetTransport.TCP)
                Sequence++;
            else
            {
                seen[seq % 256] = seq;
                if (!any || seq > high)
                    high = seq;
                any = true;
            }
            Bytes += p.Length;
            return p;
        }
        public void Dispose()
        {
            aes.Dispose();
            hmac.Dispose();
            Crypto.Clear(secret);
            Crypto.Clear(enc);
            Crypto.Clear(mac);
        }
    }
    internal sealed class Link
    {
        internal readonly VibeNetNode Node; internal readonly TcpClient Tcp; internal readonly NetworkStream Stream;
        internal readonly IPEndPoint TcpEndpoint; internal volatile IPEndPoint? UdpEndpoint;
        internal readonly Budget Memory; internal Guid Id;
        private int state = (int)ConnectionState.Connecting;
        internal ConnectionState State => (ConnectionState)Volatile.Read(ref state);
        internal readonly CancellationTokenSource Stop = new CancellationTokenSource();
        internal Task Work = Task.CompletedTask;
        private readonly SemaphoreSlim tcpSend = new SemaphoreSlim(1, 1), udpSend = new SemaphoreSlim(1, 1);
        private readonly object receiveGate = new object(); private readonly object lifeGate = new object();
        private Lane? tcpTx, tcpRx, udpTx, udpRx, previousUdp;
        private long previousUntil, lastTcp = Clock.Now; internal long NextHeartbeat; internal int HeapIndex = -1;
        private int sends, closed; private bool server; private int users; private bool disposed;
        private DisconnectReason? requestedClose;
        private readonly TaskCompletionSource<bool> byeAck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> registered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool>? updateAck; private uint expectedAck;
        private readonly RateGate packets, udpPackets, controls = new RateGate(32, 65536);
        internal Link(VibeNetNode node, TcpClient tcp, Guid id, bool isServer)
        {
            Node = node;
            Tcp = tcp;
            Stream = tcp.GetStream();
            Tcp.NoDelay = true;
            TcpEndpoint = (IPEndPoint)tcp.Client.RemoteEndPoint!;
            Id = id;
            server = isServer;
            Memory = new Budget(node.Configuration.MaxBufferedBytesPerConnection);
            packets = new RateGate(node.Configuration.MaxPacketsPerSecondPerConnection, node.Configuration.MaxBytesPerSecond);
            udpPackets = new RateGate(node.Configuration.MaxPacketsPerSecondPerConnection, node.Configuration.MaxBytesPerSecond);
            NextHeartbeat = Clock.Now + Clock.Ticks(node.Configuration.HeartbeatInterval) + (long)(Math.Abs(id.GetHashCode() % 1000) / 1000.0 * Clock.Ticks(node.Configuration.HeartbeatInterval));
        }
        internal bool Enter()
        {
            lock (lifeGate)
            {
                if (closed != 0)
                    return false;
                users++;
                return true;
            }
        }
        internal void Exit()
        {
            lock (lifeGate)
            {
                users--;
                if (closed != 0 && users == 0)
                    Cleanup();
            }
        }
        private void Cleanup()
        {
            if (disposed)
                return;
            disposed = true;
            tcpTx?.Dispose();
            tcpRx?.Dispose();
            udpTx?.Dispose();
            udpRx?.Dispose();
            previousUdp?.Dispose();
            tcpSend.Dispose();
            udpSend.Dispose();
            Stop.Dispose();
            Completion.TrySetResult(true);
        }
        internal void Close(DisconnectReason reason)
        {
            lock (lifeGate)
            {
                if (closed != 0)
                    return;
                closed = 1;
                if (requestedClose.HasValue && reason == DisconnectReason.ConnectionLost)
                    reason = requestedClose.Value;
                bool connected = State == ConnectionState.Connected || State == ConnectionState.Closing;
                Volatile.Write(ref state, (int)ConnectionState.Closed);
                Stop.Cancel();
                Tcp.Close();
                registered.TrySetCanceled();
                updateAck?.TrySetCanceled();
                byeAck.TrySetCanceled();
                Node.Messages.Close(this);
                Node.Closed(this, reason, connected);
                if (users == 0)
                    Cleanup();
            }
        }
        private void Setup(byte[] root)
        {
            string tx = server ? "s2c" : "c2s", rx = server ? "c2s" : "s2c";
            lock (receiveGate)
            {
                tcpTx = new Lane(Crypto.Expand(root, tx + "/tcp"));
                tcpRx = new Lane(Crypto.Expand(root, rx + "/tcp"));
                udpTx = new Lane(Crypto.Expand(root, tx + "/udp"));
                udpRx = new Lane(Crypto.Expand(root, rx + "/udp"));
            }
        }
        internal void Connected()
        {
            lock (lifeGate)
            {
                if (closed != 0)
                    throw new OperationCanceledException();
                Volatile.Write(ref state, (int)ConnectionState.Connected);
                Interlocked.Exchange(ref lastTcp, Clock.Now);
            }
        }
        internal async Task Handshake(CancellationToken token)
        {
            byte[] hello, reply, seed, root;
            byte[] context = Encoding.ASCII.GetBytes("VNS1/RSA2048-OAEP-SHA1/AES256-CBC-HMACSHA256/handshake");
            if (server)
            {
                using (RSA rsa = Crypto.NewRsa())
                {
                    var pub = rsa.ExportParameters(false);
                    if (pub.Modulus!.Length != Crypto.RsaBytes || pub.Exponent!.Length != 3)
                        throw new CryptographicException("Unsupported RSA encoding.");
                    hello = Crypto.Join(Id.ToByteArray(), Crypto.Random(32), pub.Modulus, pub.Exponent);
                    await Wire.PlainWrite(Stream, 100, hello, token).ConfigureAwait(false);
                    reply = await Wire.PlainRead(Stream, 101, 32 + Crypto.RsaBytes, token).ConfigureAwait(false);
                    var encrypted = new byte[Crypto.RsaBytes];
                    Buffer.BlockCopy(reply, 32, encrypted, 0, Crypto.RsaBytes);
                    seed = rsa.Decrypt(encrypted, RSAEncryptionPadding.OaepSHA1);
                    if (seed.Length != 32)
                    {
                        Crypto.Clear(seed);
                        throw new ProtocolException("Invalid key transport.");
                    }
                }
            }
            else
            {
                hello = await Wire.PlainRead(Stream, 100, 51 + Crypto.RsaBytes, token).ConfigureAwait(false);
                var id = new byte[16];
                Buffer.BlockCopy(hello, 0, id, 0, 16);
                Id = new Guid(id);
                var modulus = new byte[Crypto.RsaBytes];
                Buffer.BlockCopy(hello, 48, modulus, 0, Crypto.RsaBytes);
                if ((modulus[0] & 128) == 0 || (modulus[Crypto.RsaBytes - 1] & 1) == 0 || hello[48 + Crypto.RsaBytes] != 1 || hello[49 + Crypto.RsaBytes] != 0 || hello[50 + Crypto.RsaBytes] != 1)
                    throw new ProtocolException("Invalid RSA key.");
                seed = Crypto.Random(32);
                try
                {
                    using (RSA rsa = RSA.Create())
                    {
                        rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = new byte[] { 1, 0, 1 } });
                        reply = Crypto.Join(Crypto.Random(32), rsa.Encrypt(seed, RSAEncryptionPadding.OaepSHA1));
                    }
                }
                catch { Crypto.Clear(seed); throw; }
                try
                {
                    await Wire.PlainWrite(Stream, 101, reply, token).ConfigureAwait(false);
                }
                catch { Crypto.Clear(seed); throw; }
            }
            try
            {
                byte[] transcript = Crypto.Hash(Crypto.Join(context, hello, reply));
                root = Crypto.Hkdf(transcript, seed, context, 32);
            }
            finally { Crypto.Clear(seed); }
            try
            {
                byte[] clientFinished = Crypto.Expand(root, "client-finished"), serverFinished = Crypto.Expand(root, "server-finished");
                try
                {
                    if (server)
                    {
                        var proof = await Wire.PlainRead(Stream, 102, 32, token).ConfigureAwait(false);
                        if (!Crypto.Equal(proof, 0, clientFinished))
                            throw new ProtocolException("Key confirmation failed.");
                        Setup(root);
                        await Wire.PlainWrite(Stream, 103, serverFinished, token).ConfigureAwait(false);
                    }
                    else
                    {
                        await Wire.PlainWrite(Stream, 102, clientFinished, token).ConfigureAwait(false);
                        var proof = await Wire.PlainRead(Stream, 103, 32, token).ConfigureAwait(false);
                        if (!Crypto.Equal(proof, 0, serverFinished))
                            throw new ProtocolException("Key confirmation failed.");
                        Setup(root);
                    }
                }
                finally { Crypto.Clear(clientFinished); Crypto.Clear(serverFinished); }
            }
            finally { Crypto.Clear(root); }
            if (server)
            {
                using (var frame = await ReadTcp(token, Kind.Ready).ConfigureAwait(false))
                {
                    if (UdpEndpoint == null)
                        throw new ProtocolException("UDP registration required.");
                }
                await Control(Kind.ReadyAck, Array.Empty<byte>(), token).ConfigureAwait(false);
                Connected();
            }
            else
            {
                while (!registered.Task.IsCompleted)
                {
                    await Control(Kind.Register, Array.Empty<byte>(), token, true).ConfigureAwait(false);
                    using (var retry = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        Task delay = Task.Delay(Node.Configuration.UdpRetryInterval, retry.Token);
                        await Task.WhenAny(registered.Task, delay).ConfigureAwait(false);
                        retry.Cancel();
                        token.ThrowIfCancellationRequested();
                    }
                }
                await registered.Task.ConfigureAwait(false);
                await Control(Kind.Ready, Array.Empty<byte>(), token).ConfigureAwait(false);
                using (var frame = await ReadTcp(token, Kind.ReadyAck).ConfigureAwait(false))
                {
                }
                Connected();
            }
        }
        private sealed class Received : IDisposable
        {
            internal Kind Type; internal byte[] Data; private Lease lease; internal Received(Kind type, byte[] data, Lease l)
            {
                Type = type;
                Data = data;
                lease = l;
            }
            public void Dispose() => lease.Dispose();
        }
        private async Task<Received> ReadTcp(CancellationToken token, Kind? expected = null)
        {
            byte[] header = new byte[64];
            await Wire.ReadExact(Stream, header, 0, 64, token).ConfigureAwait(false);
            if (!Wire.HeaderValid(header, VibeNetTransport.TCP, Node.Configuration.MaxTcpPayloadBytes) || (expected.HasValue && (Kind)header[5] != expected.Value))
                throw new ProtocolException("Invalid TCP header/state.");
            int size = Wire.Size((int)Wire.U32(header, 36));
            if (!Node.TcpInputRate.Take(size) || !packets.Take(size))
                throw new ResourceException();
            var lease = Lease.Try(Node.Budget, Memory, checked(size * 4L));
            if (lease == null)
                throw new ResourceException();
            try
            {
                byte[] packet = new byte[size];
                Buffer.BlockCopy(header, 0, packet, 0, 64);
                await Wire.ReadExact(Stream, packet, 64, size - 64, token).ConfigureAwait(false);
                byte[]? data = tcpRx!.Open(packet, Id, VibeNetTransport.TCP, Node.Configuration.MaxTcpPayloadBytes);
                if (data == null)
                    throw new CryptographicException("Invalid protected TCP record.");
                Interlocked.Exchange(ref lastTcp, Clock.Now);
                return new Received((Kind)packet[5], data, lease);
            }
            catch { lease.Dispose(); throw; }
        }
        internal async Task Run()
        {
            if (!Enter())
                return;
            try
            {
                while (State == ConnectionState.Connected || State == ConnectionState.Closing)
                {
                    using (var frame = await ReadTcp(Stop.Token).ConfigureAwait(false))
                    {
                        if (frame.Type != Kind.Data && !controls.Take(frame.Data.Length))
                            throw new ResourceException();
                        switch (frame.Type)
                        {
                            case Kind.Data:
                                if (State == ConnectionState.Connected && !Node.Messages.Add(this, frame.Data, VibeNetTransport.TCP))
                                    throw new ResourceException();
                                break;
                            case Kind.Ping:
                                if (server)
                                    throw new ProtocolException("Unexpected ping.");
                                await Control(Kind.Pong, Array.Empty<byte>(), Stop.Token).ConfigureAwait(false);
                                break;
                            case Kind.Pong:
                                if (!server)
                                    throw new ProtocolException("Unexpected pong.");
                                break;
                            case Kind.Bye:
                                if (frame.Data[0] > 1 || (server && frame.Data[0] != 0))
                                    throw new ProtocolException("Invalid close code.");
                                try
                                {
                                    await Control(Kind.ByeAck, Array.Empty<byte>(), Stop.Token).ConfigureAwait(false);
                                }
                                finally { Close(frame.Data[0] == 1 ? DisconnectReason.ServerStopped : DisconnectReason.RemoteRequested); }
                                return;
                            case Kind.ByeAck:
                                if (State != ConnectionState.Closing)
                                    throw new ProtocolException("Unexpected close acknowledgment.");
                                byeAck.TrySetResult(true);
                                return;
                            case Kind.TcpUpdate:
                                var next = tcpRx!.Next();
                                tcpRx.Dispose();
                                tcpRx = next;
                                break;
                            case Kind.UdpUpdate:
                                uint epoch = Wire.U32(frame.Data, 0);
                                lock (receiveGate)
                                {
                                    if (epoch != udpRx!.Epoch + 1)
                                        throw new ProtocolException("Invalid key update.");
                                    previousUdp?.Dispose();
                                    previousUdp = udpRx;
                                    udpRx = previousUdp.Next();
                                    previousUntil = Clock.Now + Clock.Ticks(TimeSpan.FromSeconds(2));
                                }
                                await Control(Kind.UdpUpdated, frame.Data, Stop.Token).ConfigureAwait(false);
                                break;
                            case Kind.UdpUpdated:
                                lock (receiveGate)
                                {
                                    if (updateAck == null || Wire.U32(frame.Data, 0) != expectedAck)
                                        throw new ProtocolException("Unexpected key acknowledgment.");
                                    updateAck.TrySetResult(true);
                                }
                                break;
                            default:
                                throw new ProtocolException("Unexpected TCP control.");
                        }
                    }
                }
            }
            catch (Exception ex) { Fail(ex); }
            finally { Close(DisconnectReason.ConnectionLost); Exit(); }
        }
        internal async Task ReceiveUdp(byte[] packet, IPEndPoint endpoint)
        {
            if (!Enter())
                return;
            try
            {
                // Unauthenticated datagrams must never spend the TCP budget and disconnect a victim.
                if (!udpPackets.Take(packet.Length))
                {
                    Interlocked.Increment(ref Node.Dropped);
                    return;
                }
                using (var lease = Lease.Try(Node.Budget, Memory, packet.Length * 4L))
                {
                    if (lease == null)
                    {
                        Interlocked.Increment(ref Node.Dropped);
                        return;
                    }
                    Kind type = (Kind)packet[5];
                    byte[]? data;
                    lock (receiveGate)
                    {
                        if (udpRx == null || (UdpEndpoint != null && !EndpointsEqual(UdpEndpoint, endpoint)))
                        {
                            Interlocked.Increment(ref Node.Dropped);
                            return;
                        }
                        if (type != Kind.Data && type != (server ? Kind.Register : Kind.Registered))
                        {
                            Interlocked.Increment(ref Node.Dropped);
                            return;
                        }
                        uint epoch = Wire.U32(packet, 24);
                        Lane? lane = epoch == udpRx.Epoch ? udpRx : previousUdp != null && Clock.Now < previousUntil && epoch == previousUdp.Epoch ? previousUdp : null;
                        data = lane?.Open(packet, Id, VibeNetTransport.UDP, Node.Configuration.MaxUdpPayloadBytes);
                        if (data == null)
                        {
                            Interlocked.Increment(ref Node.Dropped);
                            return;
                        }
                        if (type == Kind.Register && server)
                        {
                            if (UdpEndpoint == null)
                                UdpEndpoint = new IPEndPoint(endpoint.Address, endpoint.Port);
                        }
                    }
                    if (type == Kind.Register && controls.Take(0))
                        await Control(Kind.Registered, Array.Empty<byte>(), Stop.Token, true).ConfigureAwait(false);
                    else if (type == Kind.Registered)
                        registered.TrySetResult(true);
                    else if (type == Kind.Data && !Node.Messages.Add(this, data, VibeNetTransport.UDP))
                        Interlocked.Increment(ref Node.Dropped);
                }
            }
            catch (CryptographicException) { Interlocked.Increment(ref Node.Dropped); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { Interlocked.Increment(ref Node.Dropped); }
            finally { Exit(); }
        }
        internal static bool EndpointsEqual(IPEndPoint a, IPEndPoint b) => a.Port == b.Port && (a.Address.Equals(b.Address) || a.Address.MapToIPv6().Equals(b.Address.MapToIPv6()));
        internal Task<SendResult> Send(byte[] data, VibeNetTransport lane, CancellationToken ct)
        {
            Node.Validate(data, lane);
            ct.ThrowIfCancellationRequested();
            if (State != ConnectionState.Connected || !Enter())
                return Task.FromResult(SendResult.NotConnected);
            if (Interlocked.Increment(ref Node.PendingSends) > Node.Configuration.MaxPendingSends)
            {
                Interlocked.Decrement(ref Node.PendingSends);
                Exit();
                return Task.FromResult(SendResult.Backpressured);
            }
            if (Interlocked.Increment(ref sends) > Node.Configuration.MaxPendingSendsPerConnection)
            {
                Interlocked.Decrement(ref sends);
                Interlocked.Decrement(ref Node.PendingSends);
                Exit();
                return Task.FromResult(SendResult.Backpressured);
            }
            Lease? lease = Lease.Try(Node.Budget, Memory, Wire.Size(data.Length) * 4L);
            if (lease == null)
            {
                Interlocked.Decrement(ref sends);
                Interlocked.Decrement(ref Node.PendingSends);
                Exit();
                return Task.FromResult(SendResult.Backpressured);
            }
            return SendCore(data, lane, ct, lease);
        }
        private async Task<SendResult> SendCore(byte[] data, VibeNetTransport lane, CancellationToken ct, Lease lease)
        {
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, Stop.Token))
                {
                    timeout.CancelAfter(Node.Configuration.SendTimeout);
                    await Write(Kind.Data, data, lane, timeout.Token).ConfigureAwait(false);
                    return SendResult.Sent;
                }
            }
            catch (OperationCanceledException) { if (ct.IsCancellationRequested) throw; if (State == ConnectionState.Closed) return SendResult.NotConnected; Node.Report(Id, FailureCode.Timeout, "Send deadline exceeded."); if (lane == VibeNetTransport.TCP) Close(DisconnectReason.Timeout); return SendResult.Failed; }
            catch (Exception ex) { if (lane == VibeNetTransport.TCP) Fail(ex); else Node.Report(Id, FailureCode.Socket, "UDP send failed."); return SendResult.Failed; }
            finally { lease.Dispose(); Interlocked.Decrement(ref sends); Interlocked.Decrement(ref Node.PendingSends); Exit(); }
        }
        private async Task Control(Kind type, byte[] data, CancellationToken ct, bool udp = false)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, Stop.Token))
            {
                deadline.CancelAfter(Node.Configuration.SendTimeout);
                await Write(type, data, udp ? VibeNetTransport.UDP : VibeNetTransport.TCP, deadline.Token).ConfigureAwait(false);
            }
        }
        private async Task Write(Kind type, byte[] data, VibeNetTransport lane, CancellationToken token)
        {
            var gate = lane == VibeNetTransport.TCP ? tcpSend : udpSend;
            await gate.WaitAsync(token).ConfigureAwait(false);
            bool wrote = false;
            try
            {
                if (lane == VibeNetTransport.TCP)
                {
                    if (tcpTx!.NeedsUpdate)
                    {
                        wrote = true;
                        await LowTcp(Kind.TcpUpdate, Array.Empty<byte>(), token).ConfigureAwait(false);
                        var n = tcpTx.Next();
                        tcpTx.Dispose();
                        tcpTx = n;
                    }
                    wrote = true;
                    await LowTcp(type, data, token).ConfigureAwait(false);
                }
                else
                {
                    if (udpTx!.NeedsUpdate)
                    {
                        byte[] e = new byte[4];
                        TaskCompletionSource<bool> ack;
                        lock (receiveGate)
                        {
                            expectedAck = checked(udpTx.Epoch + 1);
                            Wire.U32(e, 0, expectedAck);
                            ack = updateAck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        }
                        try
                        {
                            await Control(Kind.UdpUpdate, e, token).ConfigureAwait(false);
                            await Wire.Wait(ack.Task, token).ConfigureAwait(false);
                            var n = udpTx.Next();
                            udpTx.Dispose();
                            udpTx = n;
                        }
                        catch { Close(DisconnectReason.ConnectionLost); throw; }
                        finally { lock (receiveGate) updateAck = null; }
                    }
                    token.ThrowIfCancellationRequested();
                    var endpoint = UdpEndpoint ?? throw new ProtocolException("No registered UDP endpoint.");
                    byte[] b = udpTx.Seal(Id, type, lane, data);
                    // Old Unity UdpClient has no cancellation overload. Await completion; only private ciphertext is retained.
                    await Node.SendDatagram(b, endpoint).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException) { if (wrote) Close(DisconnectReason.ConnectionLost); throw; }
            finally { gate.Release(); }
        }
        private async Task LowTcp(Kind kind, byte[] data, CancellationToken ct)
        {
            byte[] b = tcpTx!.Seal(Id, kind, VibeNetTransport.TCP, data);
            await Stream.WriteAsync(b, 0, b.Length, ct).ConfigureAwait(false);
        }
        internal async Task Heartbeat()
        {
            if (State != ConnectionState.Connected || !Enter())
                return;
            try
            {
                if (Clock.Elapsed(Interlocked.Read(ref lastTcp), Node.Configuration.IdleTimeout))
                {
                    Close(DisconnectReason.Timeout);
                    return;
                }
                lock (receiveGate)
                {
                    if (previousUdp != null && Clock.Now >= previousUntil)
                    {
                        previousUdp.Dispose();
                        previousUdp = null;
                    }
                }
                if (server)
                    await Control(Kind.Ping, Array.Empty<byte>(), Stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { Fail(ex); }
            finally { Exit(); }
        }
        internal async Task Disconnect(DisconnectReason reason)
        {
            if (!Enter())
                return;
            try
            {
                if (State == ConnectionState.Connected)
                {
                    lock (lifeGate)
                    {
                        requestedClose = reason;
                        if (closed == 0)
                            Volatile.Write(ref state, (int)ConnectionState.Closing);
                    }
                    try
                    {
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(Stop.Token))
                        {
                            timeout.CancelAfter(Node.Configuration.SendTimeout);
                            await Control(Kind.Bye, new[] { reason == DisconnectReason.ServerStopped ? (byte)1 : (byte)0 }, timeout.Token).ConfigureAwait(false);
                            await Wire.Wait(byeAck.Task, timeout.Token).ConfigureAwait(false);
                        }
                    }
                    catch { }
                }
            }
            finally { Close(reason); Exit(); }
        }
        internal void Fail(Exception ex)
        {
            if (State == ConnectionState.Closed)
                return;
            var reason = ex is ResourceException ? DisconnectReason.ResourceLimit : ex is ProtocolException || ex is CryptographicException ? DisconnectReason.ProtocolError : DisconnectReason.ConnectionLost;
            Node.Report(Id, ex is ResourceException ? FailureCode.ResourceLimit : ex is CryptographicException ? FailureCode.Integrity : ex is ProtocolException ? FailureCode.Protocol : FailureCode.Socket, ex is CryptographicException ? "Record integrity failure." : ex.Message);
            Close(reason);
        }
    }
    internal sealed class ResourceException : IOException
    {
        internal ResourceException() : base("Resource admission limit exceeded.") { }
    }
    internal sealed class DeadlineQueue
    {
        private readonly List<Link> heap = new List<Link>();
        internal Link? Due(long now) => heap.Count != 0 && heap[0].NextHeartbeat <= now ? heap[0] : null;
        internal void Add(Link c)
        {
            c.HeapIndex = heap.Count;
            heap.Add(c);
            Up(c.HeapIndex);
        }
        internal void Remove(Link c)
        {
            int i = c.HeapIndex;
            if (i < 0)
                return;
            int last = heap.Count - 1;
            var other = heap[last];
            heap.RemoveAt(last);
            c.HeapIndex = -1;
            if (i == last)
                return;
            heap[i] = other;
            other.HeapIndex = i;
            if (i > 0 && heap[i].NextHeartbeat < heap[(i - 1) / 2].NextHeartbeat)
                Up(i);
            else
                Down(i);
        }
        private void Swap(int a, int b)
        {
            var t = heap[a];
            heap[a] = heap[b];
            heap[b] = t;
            heap[a].HeapIndex = a;
            heap[b].HeapIndex = b;
        }
        private void Up(int i)
        {
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (heap[p].NextHeartbeat <= heap[i].NextHeartbeat)
                    break;
                Swap(p, i);
                i = p;
            }
        }
        private void Down(int i)
        {
            while (true)
            {
                int a = i * 2 + 1;
                if (a >= heap.Count)
                    return;
                int b = a + 1;
                if (b < heap.Count && heap[b].NextHeartbeat < heap[a].NextHeartbeat)
                    a = b;
                if (heap[i].NextHeartbeat <= heap[a].NextHeartbeat)
                    return;
                Swap(i, a);
                i = a;
            }
        }
    }
}
namespace VibeNet
{
    public sealed class VibeNetServer : VibeNetNode
    {
        public int TcpPort
        {
            get;
        }
        public int UdpPort
        {
            get;
        }
        public IPAddress BindAddress
        {
            get;
        }
        private readonly object gate = new object(); private readonly Dictionary<Guid, Link> links = new Dictionary<Guid, Link>();
        private readonly HashSet<Guid> connected = new HashSet<Guid>(); private readonly HashSet<Task> workers = new HashSet<Task>();
        private readonly Ring<ConnectionInfo> connects;
        private readonly DeadlineQueue deadlines = new DeadlineQueue();
        private TcpListener? listener; private UdpClient? udp; private Task accepting = Task.CompletedTask, receiving = Task.CompletedTask, heartbeat = Task.CompletedTask;
        private Task? stopping; private int attempted, stoppingFlag, heartbeatWork, broadcasts;
        private readonly RateGate attempts;
        private readonly Dictionary<IPAddress, AddressRate> sources = new Dictionary<IPAddress, AddressRate>();
        private readonly Queue<IPAddress> sourceOrder = new Queue<IPAddress>();
        private sealed class AddressRate
        {
            internal readonly RateGate Rate; internal long Stamp; internal AddressRate(int max)
            {
                Rate = new RateGate(max, max);
                Stamp = Clock.Now;
            }
        }
        public int ConnectedClientCount
        {
            get
            {
                lock (gate)
                    return connected.Count;
            }
        }
        public int PendingClientCount
        {
            get
            {
                lock (gate)
                    return links.Count - connected.Count;
            }
        }
        public bool IsRunning => Volatile.Read(ref attempted) != 0 && Volatile.Read(ref stoppingFlag) == 0 && listener != null;
        internal override long EventLoss => Interlocked.Read(ref connects.Lost) + Interlocked.Read(ref Disconnects.Lost) + Interlocked.Read(ref Failures.Lost);
        public IReadOnlyList<ConnectionInfo> Connections
        {
            get
            {
                var a = Snapshot();
                var b = new ConnectionInfo[a.Length];
                for (int i = 0; i < a.Length; i++)
                    b[i] = new ConnectionInfo(a[i]);
                return b;
            }
        }
        public VibeNetServer(int tcpPort = 7777, int? udpPort = null, IPAddress? bindAddress = null, VibeNetConfiguration? configuration = null) : base(configuration)
        {
            Port(tcpPort);
            Port(udpPort ?? tcpPort);
            TcpPort = tcpPort;
            UdpPort = udpPort ?? tcpPort;
            BindAddress = bindAddress ?? IPAddress.Any;
            connects = new Ring<ConnectionInfo>(Configuration.MaxEvents);
            attempts = new RateGate(Configuration.MaxConnectionAttemptsPerSecond, Configuration.MaxConnectionAttemptsPerSecond);
        }
        public bool TryDequeueConnected(out ConnectionInfo info) => connects.Take(out info);
        public bool TryGetConnection(Guid id, out ConnectionInfo info)
        {
            lock (gate)
            {
                if (links.TryGetValue(id, out var c))
                {
                    info = new ConnectionInfo(c);
                    return true;
                }
                info = default;
                return false;
            }
        }
        public Task<StartResult> StartAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0 || Volatile.Read(ref stoppingFlag) != 0)
                throw new InvalidOperationException("Instances are single-use.");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                listener = new TcpListener(BindAddress, TcpPort);
                if (BindAddress.Equals(IPAddress.IPv6Any))
                    listener.Server.DualMode = true;
                listener.Start();
                udp = new UdpClient(BindAddress.AddressFamily);
                if (BindAddress.Equals(IPAddress.IPv6Any))
                    udp.Client.DualMode = true;
                udp.Client.Bind(new IPEndPoint(BindAddress, UdpPort));
                accepting = AcceptLoop();
                receiving = UdpLoop();
                heartbeat = HeartbeatLoop();
                return Task.FromResult(new StartResult(null));
            }
            catch (Exception ex) { Dispose(); return Task.FromResult(new StartResult(new VibeNetFailure(null, FailureCode.Socket, ex.Message))); }
        }
        public Task<SendResult> SendAsync(Guid connectionId, byte[] data, VibeNetTransport transport, CancellationToken cancellationToken = default)
        {
            Validate(data, transport);
            cancellationToken.ThrowIfCancellationRequested();
            Link? c;
            lock (gate)
                links.TryGetValue(connectionId, out c);
            return c == null ? Task.FromResult(SendResult.NotConnected) : c.Send(data, transport, cancellationToken);
        }
        public Task<BroadcastResult> BroadcastAsync(byte[] data, VibeNetTransport transport, CancellationToken cancellationToken = default)
        {
            Validate(data, transport);
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref broadcasts) > 4)
            {
                Interlocked.Decrement(ref broadcasts);
                return Task.FromResult(new BroadcastResult(new[] { 0, 0, ConnectedClientCount, 0 }));
            }
            return BroadcastCore(data, transport, cancellationToken);
        }
        private async Task<BroadcastResult> BroadcastCore(byte[] data, VibeNetTransport transport, CancellationToken cancellationToken)
        {
            try
            {
                Validate(data, transport);
                cancellationToken.ThrowIfCancellationRequested();
                var targets = Snapshot();
                int[] counts = new int[4];
                for (int offset = 0; offset < targets.Length; offset += 32)
                {
                    int n = Math.Min(32, targets.Length - offset);
                    var tasks = new List<Task<SendResult>>(n);
                    try
                    {
                        for (int j = 0; j < n; j++)
                            tasks.Add(targets[offset + j].Send(data, transport, cancellationToken));
                    }
                    catch { try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { } throw; }
                    var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                    foreach (var r in results)
                        counts[(int)r]++;
                }
                return new BroadcastResult(counts);
            }
            finally { Interlocked.Decrement(ref broadcasts); }
        }
        public async Task<bool> DisconnectClientAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Link? c;
            lock (gate)
                links.TryGetValue(id, out c);
            if (c == null)
                return false;
            await Wire.Wait(c.Disconnect(DisconnectReason.LocalRequested), cancellationToken).ConfigureAwait(false);
            return true;
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Task task;
            lock (gate)
            {
                if (stopping == null)
                    stopping = Task.Run(StopCore);
                task = stopping;
            }
            return Wire.Wait(task, cancellationToken);
        }
        private async Task StopCore()
        {
            Interlocked.Exchange(ref stoppingFlag, 1);
            listener?.Stop();
            var snapshot = Snapshot();
            // Bounded fan-out, then force-close sockets; all owned operations are joined below.
            long deadline = Clock.Now + Clock.Ticks(Configuration.SendTimeout);
            for (int i = 0; i < snapshot.Length && Clock.Now < deadline; i += 32)
            {
                var pending = new List<Task>();
                for (int j = i; j < Math.Min(i + 32, snapshot.Length); j++)
                    pending.Add(snapshot[j].Disconnect(DisconnectReason.ServerStopped));
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            foreach (var c in snapshot)
                c.Close(DisconnectReason.ServerStopped);
            Lifetime.Cancel();
            udp?.Close();
            try
            {
                await Task.WhenAll(accepting, receiving, heartbeat).ConfigureAwait(false);
            }
            catch { }
            Task[] work;
            lock (gate)
            {
                work = new Task[workers.Count];
                workers.CopyTo(work);
            }
            try
            {
                await Task.WhenAll(work).ConfigureAwait(false);
            }
            catch { }
            foreach (var c in snapshot)
            {
                c.Close(DisconnectReason.ServerStopped);
                await c.Completion.Task.ConfigureAwait(false);
            }
        }
        public override void Dispose()
        {
            Interlocked.Exchange(ref stoppingFlag, 1);
            Lifetime.Cancel();
            listener?.Stop();
            udp?.Close();
            foreach (var c in Snapshot())
                c.Close(DisconnectReason.ServerStopped);
        }
        internal override async Task<int> SendDatagram(byte[] packet, IPEndPoint endpoint)
        {
            await DatagramGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await (udp ?? throw new ObjectDisposedException(nameof(VibeNetServer))).SendAsync(packet, packet.Length, endpoint).ConfigureAwait(false);
            }
            finally { DatagramGate.Release(); }
        }
        internal override void Closed(Link c, DisconnectReason reason, bool wasConnected)
        {
            lock (gate)
            {
                links.Remove(c.Id);
                connected.Remove(c.Id);
                deadlines.Remove(c);
            }
            if (wasConnected)
                Disconnects.Add(new DisconnectInfo(c.Id, reason));
        }
        private Link[] Snapshot()
        {
            lock (gate)
            {
                var a = new Link[links.Count];
                links.Values.CopyTo(a, 0);
                return a;
            }
        }
        private bool Admit(IPAddress source)
        {
            if (!attempts.Take(1))
                return false;
            source = source.MapToIPv6();
            // Called by the single accept loop. Capped table with age-ordered admission.
            while (sourceOrder.Count != 0)
            {
                var first = sourceOrder.Peek();
                if (!Clock.Elapsed(sources[first].Stamp, TimeSpan.FromMinutes(1)))
                    break;
                sourceOrder.Dequeue();
                sources.Remove(first);
            }
            if (!sources.TryGetValue(source, out var rate))
            {
                if (sources.Count >= Configuration.MaxTrackedAddresses)
                    return false;
                rate = new AddressRate(Configuration.MaxAttemptsPerAddressPerSecond);
                sources.Add(source, rate);
                sourceOrder.Enqueue(source);
            }
            return rate.Rate.Take(1);
        }
        private async Task AcceptLoop()
        {
            try
            {
                while (Volatile.Read(ref stoppingFlag) == 0)
                {
                    TcpClient tcp = await listener!.AcceptTcpClientAsync().ConfigureAwait(false);
                    Link? link = null;
                    try
                    {
                        if (Admit(((IPEndPoint)tcp.Client.RemoteEndPoint!).Address))
                        {
                            lock (gate)
                            {
                                if (stoppingFlag == 0 && links.Count < Configuration.MaxClients && links.Count - connected.Count < Configuration.MaxPendingHandshakes)
                                {
                                    link = new Link(this, tcp, Guid.NewGuid(), true);
                                    links.Add(link.Id, link);
                                }
                            }
                        }
                    }
                    catch { tcp.Dispose(); throw; }
                    if (link == null)
                    {
                        tcp.Dispose();
                        continue;
                    }
                    var c = link;
                    var task = Task.Run(() => Session(c));
                    c.Work = task;
                    lock (gate)
                        workers.Add(task);
                    _ = task.ContinueWith(t => { lock (gate) workers.Remove(t); _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
            catch (Exception ex) { if (Volatile.Read(ref stoppingFlag) == 0) { Report(null, FailureCode.Socket, ex.Message); Dispose(); } }
        }
        private async Task Session(Link c)
        {
            if (!c.Enter())
                return;
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token, c.Stop.Token))
                {
                    timeout.CancelAfter(Configuration.ConnectTimeout);
                    await c.Handshake(timeout.Token).ConfigureAwait(false);
                }
                lock (gate)
                {
                    if (c.State == ConnectionState.Connected && links.ContainsKey(c.Id))
                    {
                        connected.Add(c.Id);
                        c.NextHeartbeat = Clock.Now + (long)(Math.Abs(c.Id.GetHashCode() % 1000) / 1000.0 * Clock.Ticks(Configuration.HeartbeatInterval));
                        deadlines.Add(c);
                        connects.Add(new ConnectionInfo(c));
                    }
                }
                await c.Run().ConfigureAwait(false);
            }
            catch (Exception ex) { if (c.State != ConnectionState.Closed) { Report(c.Id, ex is OperationCanceledException ? FailureCode.Timeout : FailureCode.Handshake, "Secure handshake failed: " + ex.Message); c.Close(ex is OperationCanceledException ? DisconnectReason.Timeout : DisconnectReason.ProtocolError); } }
            finally { c.Close(DisconnectReason.ConnectionLost); c.Exit(); await c.Completion.Task.ConfigureAwait(false); }
        }
        private async Task UdpLoop()
        {
            byte[] buffer = new byte[65536];
            EndPoint any = new IPEndPoint(BindAddress.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
            try
            {
                while (!Lifetime.IsCancellationRequested)
                {
                    var p = await udp!.Client.ReceiveFromAsync(new ArraySegment<byte>(buffer), SocketFlags.None, any).ConfigureAwait(false);
                    if (!InputRate.Take(p.ReceivedBytes) || p.ReceivedBytes < 64 || !Wire.HeaderValid(buffer, VibeNetTransport.UDP, Configuration.MaxUdpPayloadBytes) || p.ReceivedBytes != Wire.Size((int)Wire.U32(buffer, 36)))
                    {
                        Interlocked.Increment(ref Dropped);
                        continue;
                    }
                    Link? c;
                    lock (gate)
                        links.TryGetValue(Wire.Id(buffer), out c);
                    if (c != null)
                    {
                        var packet = new byte[p.ReceivedBytes];
                        Buffer.BlockCopy(buffer, 0, packet, 0, packet.Length);
                        await c.ReceiveUdp(packet, (IPEndPoint)p.RemoteEndPoint).ConfigureAwait(false);
                    }
                    else
                        Interlocked.Increment(ref Dropped);
                }
            }
            catch (Exception ex) { if (!Lifetime.IsCancellationRequested) { Report(null, FailureCode.Socket, "UDP loop failed: " + ex.Message); Dispose(); } }
        }
        private async Task HeartbeatLoop()
        {
            try
            {
                while (!Lifetime.IsCancellationRequested)
                {
                    await Task.Delay(10, Lifetime.Token).ConfigureAwait(false);
                    List<Link>? due = null;
                    lock (gate)
                    {
                        Link? c;
                        while (Volatile.Read(ref heartbeatWork) < 128 && (c = deadlines.Due(Clock.Now)) != null)
                        {
                            deadlines.Remove(c);
                            c.NextHeartbeat = Clock.Now + Clock.Ticks(Configuration.HeartbeatInterval);
                            deadlines.Add(c);
                            Interlocked.Increment(ref heartbeatWork);
                            if (due == null)
                                due = new List<Link>();
                            due.Add(c);
                        }
                    }
                    if (due != null)
                    foreach (var c in due)
                        _ = HeartbeatOne(c);
                }
            }
            catch (OperationCanceledException) { }
        }
        private async Task HeartbeatOne(Link c)
        {
            try
            {
                await c.Heartbeat().ConfigureAwait(false);
            }
            finally { Interlocked.Decrement(ref heartbeatWork); }
        }
    }
    public sealed class VibeNetClient : VibeNetNode
    {
        public string Host
        {
            get;
        }
        public int TcpPort
        {
            get;
        }
        public int UdpPort
        {
            get;
        }
        public ConnectionState State => link?.State ?? (Volatile.Read(ref disposed) != 0 ? ConnectionState.Closed : Volatile.Read(ref attempted) == 0 ? ConnectionState.Created : ConnectionState.Connecting);
        public Guid ConnectionId => link?.Id ?? Guid.Empty;
        public ConnectionInfo? Connection => link == null ? (ConnectionInfo?)null : new ConnectionInfo(link);
        private Link? link; private UdpClient? udp; private TcpClient? connecting;
        private Task receiving = Task.CompletedTask, heartbeat = Task.CompletedTask; private int attempted, disposed;
        internal override long EventLoss => Interlocked.Read(ref Disconnects.Lost) + Interlocked.Read(ref Failures.Lost);
        public VibeNetClient(string host, int tcpPort = 7777, int? udpPort = null, VibeNetConfiguration? configuration = null) : base(configuration)
        {
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("Host required.", nameof(host));
            Port(tcpPort);
            Port(udpPort ?? tcpPort);
            Host = host;
            TcpPort = tcpPort;
            UdpPort = udpPort ?? tcpPort;
        }
        public async Task<StartResult> StartAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0 || Volatile.Read(ref disposed) != 0)
                throw new InvalidOperationException("Instances are single-use.");
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lifetime.Token))
            {
                timeout.CancelAfter(Configuration.ConnectTimeout);
                Link? c = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var lookup = Dns.GetHostAddressesAsync(Host);
                    await Wire.Wait(lookup, timeout.Token).ConfigureAwait(false);
                    Exception? error = null;
                    foreach (var address in await lookup.ConfigureAwait(false))
                    {
                        if (address.AddressFamily != AddressFamily.InterNetwork && address.AddressFamily != AddressFamily.InterNetworkV6)
                            continue;
                        TcpClient tcp = new TcpClient(address.AddressFamily);
                        connecting = tcp;
                        try
                        {
                            var connect = tcp.ConnectAsync(address, TcpPort);
                            try
                            {
                                await Wire.Wait(connect, timeout.Token).ConfigureAwait(false);
                            }
                            catch { tcp.Close(); try { await connect.ConfigureAwait(false); } catch { } throw; }
                            c = new Link(this, tcp, Guid.Empty, false);
                            break;
                        }
                        catch (SocketException ex) { error = ex; tcp.Dispose(); }
                    }
                    if (c == null)
                        throw error ?? new IOException("No usable address.");
                    link = c;
                    if (!c.Enter())
                        throw new OperationCanceledException();
                    try
                    {
                        udp = new UdpClient(c.TcpEndpoint.AddressFamily);
                        udp.Connect(c.TcpEndpoint.Address, UdpPort);
                        c.UdpEndpoint = new IPEndPoint(c.TcpEndpoint.Address, UdpPort);
                        receiving = UdpLoop();
                        await c.Handshake(timeout.Token).ConfigureAwait(false);
                        c.Work = c.Run();
                        heartbeat = HeartbeatLoop();
                        return new StartResult(null);
                    }
                    finally { c.Exit(); }
                }
                catch (Exception ex) { Dispose(); if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken); return new StartResult(new VibeNetFailure(c?.Id, ex is OperationCanceledException ? FailureCode.Timeout : ex is PlatformNotSupportedException || ex is NotImplementedException ? FailureCode.UnsupportedRuntime : FailureCode.Handshake, ex.Message)); }
            }
        }
        public Task<SendResult> SendAsync(byte[] data, VibeNetTransport transport, CancellationToken cancellationToken = default)
        {
            Validate(data, transport);
            cancellationToken.ThrowIfCancellationRequested();
            return link == null ? Task.FromResult(SendResult.NotConnected) : link.Send(data, transport, cancellationToken);
        }
        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            var c = link;
            if (c != null)
                await Wire.Wait(c.Disconnect(DisconnectReason.LocalRequested), cancellationToken).ConfigureAwait(false);
            Dispose();
            await Wire.Wait(Task.WhenAll(receiving, heartbeat, c?.Work ?? Task.CompletedTask, c?.Completion.Task ?? Task.CompletedTask), cancellationToken).ConfigureAwait(false);
        }
        public override void Dispose()
        {
            Interlocked.Exchange(ref disposed, 1);
            Lifetime.Cancel();
            connecting?.Close();
            udp?.Close();
            link?.Close(DisconnectReason.LocalRequested);
        }
        internal override async Task<int> SendDatagram(byte[] packet, IPEndPoint endpoint)
        {
            await DatagramGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await (udp ?? throw new ObjectDisposedException(nameof(VibeNetClient))).SendAsync(packet, packet.Length).ConfigureAwait(false);
            }
            finally { DatagramGate.Release(); }
        }
        internal override void Closed(Link c, DisconnectReason reason, bool wasConnected)
        {
            Lifetime.Cancel();
            udp?.Close();
            if (wasConnected)
                Disconnects.Add(new DisconnectInfo(c.Id, reason));
        }
        private async Task UdpLoop()
        {
            byte[] buffer = new byte[65536];
            EndPoint any = new IPEndPoint(link!.TcpEndpoint.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
            try
            {
                while (!Lifetime.IsCancellationRequested)
                {
                    var p = await udp!.Client.ReceiveFromAsync(new ArraySegment<byte>(buffer), SocketFlags.None, any).ConfigureAwait(false);
                    if (InputRate.Take(p.ReceivedBytes) && p.ReceivedBytes >= 64 && Wire.HeaderValid(buffer, VibeNetTransport.UDP, Configuration.MaxUdpPayloadBytes) && p.ReceivedBytes == Wire.Size((int)Wire.U32(buffer, 36)))
                    {
                        var packet = new byte[p.ReceivedBytes];
                        Buffer.BlockCopy(buffer, 0, packet, 0, packet.Length);
                        await link.ReceiveUdp(packet, (IPEndPoint)p.RemoteEndPoint).ConfigureAwait(false);
                    }
                    else
                        Interlocked.Increment(ref Dropped);
                }
            }
            catch (Exception ex) { if (!Lifetime.IsCancellationRequested) link?.Fail(ex); }
        }
        private async Task HeartbeatLoop()
        {
            try
            {
                while (!Lifetime.IsCancellationRequested)
                {
                    await Task.Delay(Configuration.HeartbeatInterval, Lifetime.Token).ConfigureAwait(false);
                    await link!.Heartbeat().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }
    }
}


