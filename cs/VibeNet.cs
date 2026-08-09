using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    TCP,
    UDP
}

public enum VibeNetAddressMode
{
    IPv4,
    IPv6,
    DualStack
}

public enum VibeNetConnectionState
{
    Created,
    Connecting,
    Connected,
    Disconnecting,
    Disconnected,
    Faulted
}

public enum VibeNetConnectFailure
{
    None,
    Cancelled,
    Timeout,
    DnsResolutionFailed,
    ConnectionRefused,
    ServerRejected,
    ProtocolMismatch,
    TCPHandshakeFailed,
    UDPHandshakeFailed,
    ReadyHandshakeFailed,
    SocketError
}

public enum VibeNetDisconnectReason
{
    LocalRequested,
    RemoteRequested,
    Timeout,
    ConnectionLost,
    ProtocolError,
    ResourceLimit,
    ServerStopped
}

public enum VibeNetErrorCode
{
    SocketError,
    ProtocolError,
    SendFailure,
    ReceiveFailure,
    QueueOverflow,
    InternalError
}

public readonly struct VibeNetConnectResult
{
    public bool Success { get; }
    public VibeNetConnectFailure Failure { get; }
    public string? Error { get; }

    private VibeNetConnectResult(bool success, VibeNetConnectFailure failure, string? error)
    {
        Success = success;
        Failure = failure;
        Error = error;
    }

    public static VibeNetConnectResult Succeeded()
    {
        return new VibeNetConnectResult(true, VibeNetConnectFailure.None, null);
    }

    public static VibeNetConnectResult Failed(VibeNetConnectFailure failure, string? error = null)
    {
        return new VibeNetConnectResult(false, failure, error);
    }
}

public readonly struct VibeNetMessage
{
    public Guid? ConnectionId { get; }
    public VibeNetTransport Transport { get; }
    public byte[] Data { get; }

    public VibeNetMessage(Guid? connectionId, VibeNetTransport transport, byte[] data)
    {
        ConnectionId = connectionId;
        Transport = transport;
        Data = data ?? throw new ArgumentNullException(nameof(data));
    }
}

public readonly struct VibeNetConnectionInfo
{
    public Guid Id { get; }
    public IPAddress? RemoteAddress { get; }
    public int TCPRemotePort { get; }
    public int UDPRemotePort { get; }
    public VibeNetConnectionState State { get; }
    public DateTime TCPConnectedAtUtc { get; }
    public DateTime? ConnectedAtUtc { get; }
    public DateTime LastActivityUtc { get; }

    public VibeNetConnectionInfo(
        Guid id,
        IPAddress? remoteAddress,
        int tcpRemotePort,
        int udpRemotePort,
        VibeNetConnectionState state,
        DateTime tcpConnectedAtUtc,
        DateTime? connectedAtUtc,
        DateTime lastActivityUtc)
    {
        Id = id;
        RemoteAddress = remoteAddress;
        TCPRemotePort = tcpRemotePort;
        UDPRemotePort = udpRemotePort;
        State = state;
        TCPConnectedAtUtc = tcpConnectedAtUtc;
        ConnectedAtUtc = connectedAtUtc;
        LastActivityUtc = lastActivityUtc;
    }
}

public readonly struct VibeNetDisconnectInfo
{
    public VibeNetConnectionInfo Connection { get; }
    public VibeNetDisconnectReason Reason { get; }
    public string Detail { get; }
    public DateTime OccurredAtUtc { get; }

    public VibeNetDisconnectInfo(
        VibeNetConnectionInfo connection,
        VibeNetDisconnectReason reason,
        string detail,
        DateTime occurredAtUtc)
    {
        Connection = connection;
        Reason = reason;
        Detail = detail ?? string.Empty;
        OccurredAtUtc = occurredAtUtc;
    }
}

public readonly struct VibeNetErrorInfo
{
    public Guid? ConnectionId { get; }
    public VibeNetTransport? Transport { get; }
    public VibeNetErrorCode Code { get; }
    public string Message { get; }
    public Exception? Exception { get; }
    public DateTime OccurredAtUtc { get; }

    public VibeNetErrorInfo(
        Guid? connectionId,
        VibeNetTransport? transport,
        VibeNetErrorCode code,
        string message,
        Exception? exception,
        DateTime occurredAtUtc)
    {
        ConnectionId = connectionId;
        Transport = transport;
        Code = code;
        Message = message ?? string.Empty;
        Exception = exception;
        OccurredAtUtc = occurredAtUtc;
    }
}

public sealed class VibeNetConfiguration
{
    private static readonly TimeSpan MaximumTimerDuration =
        TimeSpan.FromMilliseconds(int.MaxValue - 1);

    public TimeSpan ClientConnectTimeout { get; }
    public TimeSpan UDPHandshakeInterval { get; }
    public TimeSpan UDPHandshakeTimeout { get; }
    public TimeSpan ServerConnectTimeout { get; }
    public TimeSpan TCPHeartbeatInterval { get; }
    public TimeSpan TCPHeartbeatTimeout { get; }

    public int MaxTCPPayloadBytes { get; }
    public int MaxUDPPayloadBytes { get; }
    public int MaxQueuedMessages { get; }
    public int MaxQueuedErrors { get; }
    public int MaxQueuedEvents { get; }

    public VibeNetAddressMode AddressMode { get; }

    public VibeNetConfiguration(
        TimeSpan? clientConnectTimeout = null,
        TimeSpan? udpHandshakeInterval = null,
        TimeSpan? udpHandshakeTimeout = null,
        TimeSpan? serverConnectTimeout = null,
        TimeSpan? tcpHeartbeatInterval = null,
        TimeSpan? tcpHeartbeatTimeout = null,
        int maxTcpPayloadBytes = 1024 * 1024,
        int maxUdpPayloadBytes = 1200,
        int maxQueuedMessages = 4096,
        int maxQueuedErrors = 256,
        int maxQueuedEvents = 1024,
        VibeNetAddressMode addressMode = VibeNetAddressMode.IPv4)
    {
        ClientConnectTimeout = clientConnectTimeout ?? TimeSpan.FromSeconds(5);
        UDPHandshakeInterval = udpHandshakeInterval ?? TimeSpan.FromMilliseconds(500);
        UDPHandshakeTimeout = udpHandshakeTimeout ?? TimeSpan.FromSeconds(3);
        ServerConnectTimeout = serverConnectTimeout ?? TimeSpan.FromSeconds(10);
        TCPHeartbeatInterval = tcpHeartbeatInterval ?? TimeSpan.FromSeconds(2);
        TCPHeartbeatTimeout = tcpHeartbeatTimeout ?? TimeSpan.FromSeconds(10);

        MaxTCPPayloadBytes = maxTcpPayloadBytes;
        MaxUDPPayloadBytes = maxUdpPayloadBytes;
        MaxQueuedMessages = maxQueuedMessages;
        MaxQueuedErrors = maxQueuedErrors;
        MaxQueuedEvents = maxQueuedEvents;
        AddressMode = addressMode;

        Validate();
    }

    private void Validate()
    {
        ValidateDuration(ClientConnectTimeout, nameof(ClientConnectTimeout));
        ValidateDuration(UDPHandshakeInterval, nameof(UDPHandshakeInterval));
        ValidateDuration(UDPHandshakeTimeout, nameof(UDPHandshakeTimeout));
        ValidateDuration(ServerConnectTimeout, nameof(ServerConnectTimeout));
        ValidateDuration(TCPHeartbeatInterval, nameof(TCPHeartbeatInterval));
        ValidateDuration(TCPHeartbeatTimeout, nameof(TCPHeartbeatTimeout));

        if (TCPHeartbeatTimeout <= TCPHeartbeatInterval)
            throw new ArgumentOutOfRangeException(nameof(TCPHeartbeatTimeout));

        if (MaxTCPPayloadBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxTCPPayloadBytes));

        if (MaxUDPPayloadBytes <= 0 ||
            MaxUDPPayloadBytes > VibeNetProtocol.MaxUserUdpPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxUDPPayloadBytes));
        }

        if (MaxQueuedMessages <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxQueuedMessages));

        if (MaxQueuedErrors <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxQueuedErrors));

        if (MaxQueuedEvents <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxQueuedEvents));

        if (!Enum.IsDefined(typeof(VibeNetAddressMode), AddressMode))
            throw new ArgumentOutOfRangeException(nameof(AddressMode));
    }

    private static void ValidateDuration(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > MaximumTimerDuration)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

public abstract class VibeNetNode : IDisposable
{
    public int TCPPort { get; }
    public int UDPPort { get; }
    public VibeNetConfiguration Configuration { get; }

    private readonly DropOldestQueue<VibeNetErrorInfo> errorQueue;

    protected readonly CancellationTokenSource Lifetime = new CancellationTokenSource();

    protected VibeNetNode(int tcpPort, int? udpPort, VibeNetConfiguration? configuration)
    {
        ValidatePort(tcpPort, nameof(tcpPort));

        int actualUdpPort = udpPort ?? tcpPort;
        ValidatePort(actualUdpPort, nameof(udpPort));

        TCPPort = tcpPort;
        UDPPort = actualUdpPort;
        Configuration = configuration ?? new VibeNetConfiguration();
        errorQueue = new DropOldestQueue<VibeNetErrorInfo>(Configuration.MaxQueuedErrors);
    }

    public bool TryDequeueError(out VibeNetErrorInfo error)
    {
        return errorQueue.TryDequeue(out error);
    }

    public abstract bool TryDequeueMessage(out VibeNetMessage message);

    public abstract Task<VibeNetConnectResult> StartAsync(
        CancellationToken cancellationToken = default);

    public abstract void Dispose();

    protected void ReportError(
        Guid? connectionId,
        VibeNetTransport? transport,
        VibeNetErrorCode code,
        string message,
        Exception? exception = null)
    {
        errorQueue.EnqueueDroppingOldest(
            new VibeNetErrorInfo(
                connectionId,
                transport,
                code,
                message,
                exception,
                DateTime.UtcNow));
    }

    protected void ValidatePayload(byte[] data, VibeNetTransport transport)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        int maximum = transport switch
        {
            VibeNetTransport.TCP => Configuration.MaxTCPPayloadBytes,
            VibeNetTransport.UDP => Configuration.MaxUDPPayloadBytes,
            _ => throw new ArgumentOutOfRangeException(nameof(transport))
        };

        if (data.Length > maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                "Payload exceeds the configured maximum for " + transport + ".");
        }
    }

    protected static void ValidatePort(int port, string parameterName)
    {
        if (port < 1 || port > 65535)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

public sealed class VibeNetServer : VibeNetNode
{
    private sealed class ServerSession
    {
        public Guid Id { get; }
        public byte[] Token { get; }
        public TcpClient TcpClient { get; }
        public NetworkStream TcpStream { get; }
        public SemaphoreSlim SendLock { get; }
        public CancellationTokenSource HandshakeLifetime { get; }
        public DateTime TcpConnectedAtUtc { get; }

        public IPEndPoint? TcpRemoteEndPoint { get; set; }
        public IPEndPoint? UdpRemoteEndPoint { get; set; }
        public VibeNetConnectionState State { get; set; }
        public DateTime? ConnectedAtUtc { get; set; }
        public Task SessionTask { get; set; }
        public long LastTcpActivityStamp;
        public long LastActivityUtcTicks;
        public int DisconnectStarted;

        public ServerSession(
            Guid id,
            byte[] token,
            TcpClient tcpClient,
            IPEndPoint? tcpRemoteEndPoint,
            CancellationTokenSource handshakeLifetime)
        {
            Id = id;
            Token = token;
            TcpClient = tcpClient;
            TcpStream = tcpClient.GetStream();
            SendLock = new SemaphoreSlim(1, 1);
            HandshakeLifetime = handshakeLifetime;
            TcpConnectedAtUtc = DateTime.UtcNow;
            TcpRemoteEndPoint = tcpRemoteEndPoint;
            State = VibeNetConnectionState.Connecting;
            SessionTask = Task.CompletedTask;
            LastTcpActivityStamp = VibeNetTime.Timestamp;
            LastActivityUtcTicks = TcpConnectedAtUtc.Ticks;
        }
    }

    private readonly object stateLock = new object();
    private readonly object sessionsLock = new object();

    private readonly DropOldestQueue<VibeNetConnectionInfo> connectedQueue;
    private readonly DropOldestQueue<VibeNetDisconnectInfo> disconnectedQueue;
    private readonly FairServerMessageQueue messageQueue;
    private readonly SemaphoreSlim udpSendLock = new SemaphoreSlim(1, 1);

    private readonly Dictionary<Guid, ServerSession> sessions =
        new Dictionary<Guid, ServerSession>();

    private readonly Dictionary<string, Guid> udpEndpoints =
        new Dictionary<string, Guid>(StringComparer.Ordinal);

    private TcpListener? tcpListener;
    private UdpClient? udpSocket;
    private Task acceptTask = Task.CompletedTask;
    private Task udpTask = Task.CompletedTask;
    private Task heartbeatTask = Task.CompletedTask;
    private Task? stopTask;

    private bool startAttempted;
    private bool running;
    private bool disposed;

    public int MaxClients { get; }
    public IPAddress BindAddress { get; }

    public bool IsRunning
    {
        get
        {
            lock (stateLock)
                return running && !disposed;
        }
    }

    public int ConnectedClientCount
    {
        get
        {
            lock (sessionsLock)
                return sessions.Values.Count(session => session.State == VibeNetConnectionState.Connected);
        }
    }

    public int PendingClientCount
    {
        get
        {
            lock (sessionsLock)
                return sessions.Values.Count(session => session.State == VibeNetConnectionState.Connecting);
        }
    }

    public IReadOnlyList<VibeNetConnectionInfo> Connections
    {
        get
        {
            lock (sessionsLock)
                return sessions.Values.Select(MakeConnectionInfo).ToArray();
        }
    }

    public VibeNetServer(
        int tcpPort = 7777,
        int? udpPort = null,
        int maxClients = 32,
        IPAddress? bindAddress = null,
        VibeNetConfiguration? configuration = null)
        : base(tcpPort, udpPort, configuration)
    {
        if (maxClients <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxClients));

        MaxClients = maxClients;
        BindAddress = ResolveBindAddress(bindAddress, Configuration.AddressMode);
        connectedQueue =
            new DropOldestQueue<VibeNetConnectionInfo>(Configuration.MaxQueuedEvents);
        disconnectedQueue =
            new DropOldestQueue<VibeNetDisconnectInfo>(Configuration.MaxQueuedEvents);
        messageQueue =
            new FairServerMessageQueue(Configuration.MaxQueuedMessages, maxClients);
    }

    public override bool TryDequeueMessage(out VibeNetMessage message)
    {
        return messageQueue.TryDequeue(out message);
    }

    public bool TryDequeueConnected(out VibeNetConnectionInfo connection)
    {
        return connectedQueue.TryDequeue(out connection);
    }

    public bool TryDequeueDisconnected(out VibeNetDisconnectInfo disconnect)
    {
        return disconnectedQueue.TryDequeue(out disconnect);
    }

    public bool TryGetConnection(Guid connectionId, out VibeNetConnectionInfo connection)
    {
        lock (sessionsLock)
        {
            if (sessions.TryGetValue(connectionId, out ServerSession? session))
            {
                connection = MakeConnectionInfo(session);
                return true;
            }
        }

        connection = default;
        return false;
    }

    public override Task<VibeNetConnectResult> StartAsync(
        CancellationToken cancellationToken = default)
    {
        lock (stateLock)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(VibeNetServer));

            if (startAttempted)
                throw new InvalidOperationException("Server instances are single-use.");

            startAttempted = true;
        }

        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(VibeNetConnectResult.Failed(VibeNetConnectFailure.Cancelled));

        try
        {
            tcpListener = new TcpListener(BindAddress, TCPPort);
            if (Configuration.AddressMode == VibeNetAddressMode.DualStack)
                tcpListener.Server.DualMode = true;

            tcpListener.Start();
            udpSocket = CreateServerUdpSocket(BindAddress, UDPPort, Configuration.AddressMode);

            acceptTask = RunAcceptLoopAsync();
            udpTask = RunUdpLoopAsync();
            heartbeatTask = RunHeartbeatLoopAsync();

            lock (stateLock)
                running = true;

            return Task.FromResult(VibeNetConnectResult.Succeeded());
        }
        catch (SocketException ex)
        {
            CloseServerSockets();
            return Task.FromResult(
                VibeNetConnectResult.Failed(VibeNetConnectFailure.SocketError, ex.Message));
        }
        catch
        {
            CloseServerSockets();
            throw;
        }
    }

    public async Task<bool> SendAsync(
        Guid connectionId,
        byte[] data,
        VibeNetTransport transport,
        CancellationToken cancellationToken = default)
    {
        ValidatePayload(data, transport);

        ServerSession? session = GetConnectedSession(connectionId);
        if (session == null)
            return false;

        if (transport == VibeNetTransport.TCP)
        {
            try
            {
                await SendTcpFrameAsync(
                    session,
                    VibeNetPacketType.Data,
                    data,
                    cancellationToken).ConfigureAwait(false);

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ReportError(
                    session.Id,
                    VibeNetTransport.TCP,
                    VibeNetErrorCode.SendFailure,
                    "TCP send failed.",
                    ex);

                await DisconnectSessionAsync(
                    session,
                    VibeNetDisconnectReason.ConnectionLost,
                    "TCP send failed.",
                    false,
                    CancellationToken.None).ConfigureAwait(false);

                return false;
            }
        }

        IPEndPoint? endpoint;
        lock (sessionsLock)
            endpoint = session.UdpRemoteEndPoint;

        if (endpoint == null)
            return false;

        try
        {
            await SendUdpFrameAsync(
                VibeNetPacketType.Data,
                data,
                endpoint,
                cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportError(
                session.Id,
                VibeNetTransport.UDP,
                VibeNetErrorCode.SendFailure,
                "UDP send failed.",
                ex);

            return false;
        }
    }

    public async Task<int> BroadcastAsync(
        byte[] data,
        VibeNetTransport transport,
        CancellationToken cancellationToken = default)
    {
        ValidatePayload(data, transport);

        Guid[] targets = SnapshotConnectedSessions().Select(session => session.Id).ToArray();

        if (transport == VibeNetTransport.TCP)
        {
            Task<bool>[] sends = targets
                .Select(id => SendAsync(id, data, VibeNetTransport.TCP, cancellationToken))
                .ToArray();

            bool[] results = await Task.WhenAll(sends).ConfigureAwait(false);
            return results.Count(result => result);
        }

        int successCount = 0;
        foreach (Guid id in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await SendAsync(id, data, VibeNetTransport.UDP, cancellationToken).ConfigureAwait(false))
                successCount++;
        }

        return successCount;
    }

    public async Task<bool> DisconnectClientAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        ServerSession? session;
        lock (sessionsLock)
            sessions.TryGetValue(connectionId, out session);

        if (session == null)
            return false;

        bool notifyRemote = session.State == VibeNetConnectionState.Connected;
        await DisconnectSessionAsync(
            session,
            VibeNetDisconnectReason.LocalRequested,
            "Disconnected by server.",
            notifyRemote,
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task task;

        lock (stateLock)
        {
            if (disposed)
                return;

            if (!startAttempted)
                return;

            stopTask ??= StopCoreAsync();
            task = stopTask;
        }

        await VibeNetTask.WaitAsync(task, cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        lock (stateLock)
        {
            if (disposed)
                return;

            disposed = true;
            running = false;
        }

        Lifetime.Cancel();
        CloseServerSockets();

        ServerSession[] snapshot = SnapshotSessions();
        foreach (ServerSession session in snapshot)
            DisconnectSessionImmediate(session, VibeNetDisconnectReason.ServerStopped, "Server disposed.");
    }

    private async Task StopCoreAsync()
    {
        lock (stateLock)
            running = false;

        try
        {
            tcpListener?.Stop();
        }
        catch
        {
        }

        ServerSession[] snapshot = SnapshotSessions();
        using CancellationTokenSource notifyTimeout =
            VibeNetCancellation.CreateTimeoutSource(VibeNetDefaults.ControlFrameTimeout);

        Task[] disconnects = snapshot
            .Select(session => DisconnectSessionAsync(
                session,
                VibeNetDisconnectReason.ServerStopped,
                "Server stopped.",
                session.State == VibeNetConnectionState.Connected,
                notifyTimeout.Token))
            .ToArray();

        try
        {
            await Task.WhenAll(disconnects).ConfigureAwait(false);
        }
        finally
        {
            Lifetime.Cancel();
            try
            {
                udpSocket?.Close();
            }
            catch
            {
            }

            await AwaitBackgroundTasksAsync().ConfigureAwait(false);
        }
    }

    private async Task RunAcceptLoopAsync()
    {
        if (tcpListener == null)
            return;

        try
        {
            while (!Lifetime.IsCancellationRequested)
            {
                TcpClient tcp =
                    await VibeNetSocket.AcceptTcpClientAsync(tcpListener, Lifetime.Token).ConfigureAwait(false);

                if (!TryReserveClientSlot())
                {
                    _ = RejectClientAsync(tcp, "Server capacity reached.");
                    continue;
                }

                ServerSession session = CreateSession(tcp);

                lock (sessionsLock)
                    sessions.Add(session.Id, session);

                session.SessionTask = RunSessionAsync(session);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException ex)
        {
            if (!Lifetime.IsCancellationRequested)
                FailServer(ex, VibeNetTransport.TCP, VibeNetErrorCode.SocketError, "TCP accept loop failed.");
        }
        catch (Exception ex)
        {
            if (!Lifetime.IsCancellationRequested)
                FailServer(ex, VibeNetTransport.TCP, VibeNetErrorCode.InternalError, "Unexpected TCP accept-loop failure.");
        }
    }

    private async Task RejectClientAsync(TcpClient tcp, string reason)
    {
        using CancellationTokenSource timeout = VibeNetCancellation.CreateTimeoutSource(VibeNetDefaults.ControlFrameTimeout);

        try
        {
            await VibeNetProtocol.WriteTcpFrameAsync(
                tcp.GetStream(),
                VibeNetPacketType.Reject,
                VibeNetProtocol.CreateRejectPayload(reason),
                timeout.Token).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            try
            {
                tcp.Close();
            }
            catch
            {
            }
        }
    }

    private async Task RunSessionAsync(ServerSession session)
    {
        VibeNetDisconnectReason finalReason = VibeNetDisconnectReason.ConnectionLost;
        string finalDetail = "TCP connection closed.";

        try
        {
            await SendTcpFrameAsync(
                session,
                VibeNetPacketType.Hello,
                VibeNetProtocol.CreateHelloPayload(session.Id, session.Token),
                session.HandshakeLifetime.Token).ConfigureAwait(false);

            while (!Lifetime.IsCancellationRequested)
            {
                CancellationToken readToken =
                    session.State == VibeNetConnectionState.Connecting
                    ? session.HandshakeLifetime.Token
                    : Lifetime.Token;

                NetworkFrame? frame = await VibeNetProtocol.ReadTcpFrameAsync(
                    session.TcpStream,
                    Math.Max(Configuration.MaxTCPPayloadBytes, VibeNetProtocol.MaxControlPayloadBytes),
                    readToken).ConfigureAwait(false);

                if (frame == null)
                {
                    finalDetail =
                        session.State == VibeNetConnectionState.Connecting
                        ? "Connection closed during handshake."
                        : "TCP connection closed.";
                    break;
                }

                TouchTcp(session);

                if (session.State == VibeNetConnectionState.Connecting)
                    await HandleConnectingFrameAsync(session, frame).ConfigureAwait(false);
                else
                    await HandleConnectedFrameAsync(session, frame).ConfigureAwait(false);
            }
        }
        catch (VibeNetDisconnectSignal signal)
        {
            finalReason = signal.Reason;
            finalDetail = signal.Detail;
        }
        catch (OperationCanceledException) when (
            session.State == VibeNetConnectionState.Connecting &&
            session.HandshakeLifetime.IsCancellationRequested &&
            !Lifetime.IsCancellationRequested)
        {
            finalReason = VibeNetDisconnectReason.Timeout;
            finalDetail = "Connection handshake timed out.";
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested)
        {
            finalReason = VibeNetDisconnectReason.ServerStopped;
            finalDetail = "Server stopped.";
        }
        catch (VibeNetProtocolException ex)
        {
            ReportError(
                session.Id,
                VibeNetTransport.TCP,
                VibeNetErrorCode.ProtocolError,
                ex.Message,
                ex);

            finalReason = VibeNetDisconnectReason.ProtocolError;
            finalDetail = ex.Message;
        }
        catch (IOException ex)
        {
            if (Volatile.Read(ref session.DisconnectStarted) == 0)
            {
                ReportError(
                    session.Id,
                    VibeNetTransport.TCP,
                    VibeNetErrorCode.ReceiveFailure,
                    "TCP receive failed.",
                    ex);
            }
        }
        catch (SocketException ex)
        {
            if (Volatile.Read(ref session.DisconnectStarted) == 0)
            {
                ReportError(
                    session.Id,
                    VibeNetTransport.TCP,
                    VibeNetErrorCode.SocketError,
                    "TCP receive failed.",
                    ex);
            }
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref session.DisconnectStarted) == 0)
            {
                ReportError(
                    session.Id,
                    VibeNetTransport.TCP,
                    VibeNetErrorCode.InternalError,
                    "Unexpected TCP session failure.",
                    ex);
            }
        }
        finally
        {
            await DisconnectSessionAsync(
                session,
                finalReason,
                finalDetail,
                false,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task HandleConnectingFrameAsync(ServerSession session, NetworkFrame frame)
    {
        if (frame.Type != VibeNetPacketType.Ready)
            throw new VibeNetProtocolException("Unexpected TCP packet during handshake: " + frame.Type + ".");

        VibeNetProtocol.RequireEmptyPayload(frame);

        bool hasUdp;
        lock (sessionsLock)
            hasUdp = session.UdpRemoteEndPoint != null;

        if (!hasUdp)
            throw new VibeNetProtocolException("READY received before UDP registration completed.");

        await SendTcpFrameAsync(
            session,
            VibeNetPacketType.ReadyAck,
            VibeNetProtocol.EmptyPayload,
            session.HandshakeLifetime.Token).ConfigureAwait(false);

        VibeNetConnectionInfo connectionInfo;
        lock (sessionsLock)
        {
            if (!sessions.ContainsKey(session.Id) ||
                Volatile.Read(ref session.DisconnectStarted) != 0)
            {
                throw new VibeNetDisconnectSignal(
                    VibeNetDisconnectReason.ConnectionLost,
                    "Session closed during handshake.");
            }

            session.State = VibeNetConnectionState.Connected;
            session.ConnectedAtUtc = DateTime.UtcNow;
            connectionInfo = MakeConnectionInfo(session);
        }

        connectedQueue.EnqueueDroppingOldest(connectionInfo);
    }

    private Task HandleConnectedFrameAsync(ServerSession session, NetworkFrame frame)
    {
        switch (frame.Type)
        {
            case VibeNetPacketType.Pong:
                VibeNetProtocol.RequireEmptyPayload(frame);
                return Task.CompletedTask;

            case VibeNetPacketType.Data:
                if (frame.Payload.Length > Configuration.MaxTCPPayloadBytes)
                    throw new VibeNetProtocolException("TCP payload exceeds configured maximum.");

                VibeNetMessage tcpMessage =
                    new VibeNetMessage(session.Id, VibeNetTransport.TCP, frame.Payload);

                ServerQueueEnqueueResult enqueueResult = messageQueue.Enqueue(
                    session.Id,
                    tcpMessage,
                    true);

                if (enqueueResult.Kind == ServerQueueEnqueueKind.DisconnectCurrent)
                {
                    ReportError(
                        session.Id,
                        VibeNetTransport.TCP,
                        VibeNetErrorCode.QueueOverflow,
                        "Reliable message queue limit exceeded.");

                    throw new VibeNetDisconnectSignal(
                        VibeNetDisconnectReason.ResourceLimit,
                        "Reliable message queue limit exceeded.");
                }

                if (enqueueResult.Kind == ServerQueueEnqueueKind.DisconnectOther &&
                    TryGetSession(enqueueResult.OffenderId, out ServerSession? offender) &&
                    offender != null)
                {
                    _ = DisconnectSessionAsync(
                        offender,
                        VibeNetDisconnectReason.ResourceLimit,
                        "Reliable message queue limit exceeded.",
                        false,
                        CancellationToken.None);
                }

                return Task.CompletedTask;

            case VibeNetPacketType.Disconnect:
                VibeNetDisconnectCode code = VibeNetProtocol.ParseDisconnectPayload(frame.Payload);
                if (code != VibeNetDisconnectCode.ClientRequested)
                    throw new VibeNetProtocolException("Client sent an invalid disconnect code.");

                throw new VibeNetDisconnectSignal(
                    VibeNetDisconnectReason.RemoteRequested,
                    "Remote client requested disconnection.");

            default:
                throw new VibeNetProtocolException("Unexpected TCP packet from client: " + frame.Type + ".");
        }
    }

    private async Task RunUdpLoopAsync()
    {
        if (udpSocket == null)
            return;

        try
        {
            while (!Lifetime.IsCancellationRequested)
            {
                UdpReceiveResult result =
                    await VibeNetSocket.ReceiveAsync(udpSocket, Lifetime.Token).ConfigureAwait(false);

                if (!VibeNetProtocol.TryParseUdpFrame(
                    result.Buffer,
                    Math.Max(Configuration.MaxUDPPayloadBytes, VibeNetProtocol.MaxControlPayloadBytes),
                    out NetworkFrame? frame))
                {
                    continue;
                }

                if (frame != null && frame.Type == VibeNetPacketType.UdpRegister)
                {
                    ProcessUdpRegistration(frame.Payload, result.RemoteEndPoint);
                    continue;
                }

                if (frame == null || frame.Type != VibeNetPacketType.Data)
                    continue;

                ServerSession? session = FindSessionByUdpEndPoint(result.RemoteEndPoint);
                if (session == null || session.State != VibeNetConnectionState.Connected)
                    continue;

                TouchActivity(session);

                if (frame.Payload.Length > Configuration.MaxUDPPayloadBytes)
                {
                    ReportError(
                        session.Id,
                        VibeNetTransport.UDP,
                        VibeNetErrorCode.ProtocolError,
                        "UDP payload exceeds configured maximum.");
                    continue;
                }

                VibeNetMessage udpMessage =
                    new VibeNetMessage(session.Id, VibeNetTransport.UDP, frame.Payload);

                ServerQueueEnqueueResult enqueueResult = messageQueue.Enqueue(
                    session.Id,
                    udpMessage,
                    false);

                if (enqueueResult.Kind != ServerQueueEnqueueKind.Enqueued)
                {
                    ReportError(
                        session.Id,
                        VibeNetTransport.UDP,
                        VibeNetErrorCode.QueueOverflow,
                        "Incoming UDP datagram dropped because the queue is full.");
                }

                if (enqueueResult.Kind == ServerQueueEnqueueKind.DisconnectOther &&
                    TryGetSession(enqueueResult.OffenderId, out ServerSession? offender) &&
                    offender != null)
                {
                    _ = DisconnectSessionAsync(
                        offender,
                        VibeNetDisconnectReason.ResourceLimit,
                        "Reliable message queue limit exceeded.",
                        false,
                        CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException ex)
        {
            if (!Lifetime.IsCancellationRequested)
                FailServer(ex, VibeNetTransport.UDP, VibeNetErrorCode.SocketError, "UDP receive loop failed.");
        }
        catch (Exception ex)
        {
            if (!Lifetime.IsCancellationRequested)
                FailServer(ex, VibeNetTransport.UDP, VibeNetErrorCode.InternalError, "Unexpected UDP receive-loop failure.");
        }
    }

    private void ProcessUdpRegistration(byte[] payload, IPEndPoint remoteEndPoint)
    {
        if (!VibeNetProtocol.TryParseRegistrationPayload(payload, out Guid id, out byte[]? token))
            return;

        ServerSession? session = null;
        string endpointKey = CreateEndpointKey(remoteEndPoint);

        lock (sessionsLock)
        {
            if (udpEndpoints.TryGetValue(endpointKey, out Guid existingId) && existingId != id)
                return;

            if (!sessions.TryGetValue(id, out ServerSession? candidate))
                return;

            if (token == null ||
                candidate.State != VibeNetConnectionState.Connecting ||
                Volatile.Read(ref candidate.DisconnectStarted) != 0 ||
                !FixedTimeEquals(candidate.Token, token))
            {
                return;
            }

            if (candidate.UdpRemoteEndPoint != null)
                udpEndpoints.Remove(CreateEndpointKey(candidate.UdpRemoteEndPoint));

            candidate.UdpRemoteEndPoint = remoteEndPoint;
            udpEndpoints[endpointKey] = candidate.Id;
            TouchActivity(candidate);
            session = candidate;
        }

        if (session == null)
            return;

        _ = AcknowledgeUdpRegistrationAsync(session, remoteEndPoint);
    }

    private async Task AcknowledgeUdpRegistrationAsync(ServerSession session, IPEndPoint remoteEndPoint)
    {
        using CancellationTokenSource timeout = VibeNetCancellation.CreateTimeoutSource(VibeNetDefaults.ControlFrameTimeout);

        try
        {
            await SendUdpFrameAsync(
                VibeNetPacketType.UdpAck,
                VibeNetProtocol.GuidToNetworkBytes(session.Id),
                remoteEndPoint,
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is OperationCanceledException ||
            ex is SocketException ||
            ex is ObjectDisposedException)
        {
            ReportError(
                session.Id,
                VibeNetTransport.UDP,
                VibeNetErrorCode.SendFailure,
                "Failed to acknowledge UDP registration.",
                ex);
        }
    }

    private async Task RunHeartbeatLoopAsync()
    {
        try
        {
            while (!Lifetime.IsCancellationRequested)
            {
                await Task.Delay(Configuration.TCPHeartbeatInterval, Lifetime.Token).ConfigureAwait(false);

                foreach (ServerSession session in SnapshotConnectedSessions())
                {
                    if (VibeNetTime.HasElapsed(
                        Interlocked.Read(ref session.LastTcpActivityStamp),
                        Configuration.TCPHeartbeatTimeout))
                    {
                        _ = DisconnectSessionAsync(
                            session,
                            VibeNetDisconnectReason.Timeout,
                            "TCP heartbeat timed out.",
                            false,
                            CancellationToken.None);
                        continue;
                    }

                    _ = SendHeartbeatAsync(session);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!Lifetime.IsCancellationRequested)
                FailServer(ex, VibeNetTransport.TCP, VibeNetErrorCode.InternalError, "Heartbeat loop failed.");
        }
    }

    private async Task SendHeartbeatAsync(ServerSession session)
    {
        using CancellationTokenSource timeout = VibeNetCancellation.CreateTimeoutSource(Configuration.TCPHeartbeatInterval);

        try
        {
            await SendTcpFrameAsync(
                session,
                VibeNetPacketType.Ping,
                VibeNetProtocol.EmptyPayload,
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is OperationCanceledException ||
            ex is IOException ||
            ex is SocketException ||
            ex is ObjectDisposedException)
        {
            ReportError(
                session.Id,
                VibeNetTransport.TCP,
                VibeNetErrorCode.SendFailure,
                "Heartbeat send failed.",
                ex);

            await DisconnectSessionAsync(
                session,
                VibeNetDisconnectReason.ConnectionLost,
                "Heartbeat send failed.",
                false,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task SendTcpFrameAsync(
        ServerSession session,
        VibeNetPacketType type,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await session.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.State == VibeNetConnectionState.Disconnecting ||
                session.State == VibeNetConnectionState.Disconnected ||
                session.State == VibeNetConnectionState.Faulted)
            {
                throw new ObjectDisposedException(nameof(ServerSession));
            }

            await VibeNetProtocol.WriteTcpFrameAsync(
                session.TcpStream,
                type,
                payload,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                session.TcpClient.Close();
            }
            catch
            {
            }

            throw;
        }
        finally
        {
            session.SendLock.Release();
        }
    }

    private async Task SendUdpFrameAsync(
        VibeNetPacketType type,
        byte[] payload,
        IPEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        if (udpSocket == null)
            throw new ObjectDisposedException(nameof(VibeNetServer));

        byte[] datagram = VibeNetProtocol.CreateUdpFrame(type, payload);

        await udpSendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await VibeNetSocket.SendAsync(udpSocket, datagram, endpoint, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            udpSendLock.Release();
        }
    }

    private async Task DisconnectSessionAsync(
        ServerSession session,
        VibeNetDisconnectReason reason,
        string detail,
        bool notifyRemote,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref session.DisconnectStarted, 1) != 0)
            return;

        bool publishDisconnect;
        VibeNetConnectionInfo finalInfo;

        lock (sessionsLock)
        {
            publishDisconnect = session.State == VibeNetConnectionState.Connected;
            session.State = VibeNetConnectionState.Disconnecting;
        }

        if (notifyRemote)
        {
            using CancellationTokenSource timeout = VibeNetCancellation.CreateLinkedTimeoutSource(
                VibeNetDefaults.ControlFrameTimeout,
                cancellationToken);

            try
            {
                VibeNetDisconnectCode code =
                    reason == VibeNetDisconnectReason.ServerStopped
                    ? VibeNetDisconnectCode.ServerStopping
                    : VibeNetDisconnectCode.ServerRequested;

                await SendTcpFrameAsync(
                    session,
                    VibeNetPacketType.Disconnect,
                    VibeNetProtocol.CreateDisconnectPayload(code),
                    timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is OperationCanceledException ||
                ex is IOException ||
                ex is SocketException ||
                ex is ObjectDisposedException)
            {
                ReportError(
                    session.Id,
                    VibeNetTransport.TCP,
                    VibeNetErrorCode.SendFailure,
                    "Failed to send disconnect control frame.",
                    ex);
            }
        }

        session.HandshakeLifetime.Cancel();

        lock (sessionsLock)
        {
            if (session.UdpRemoteEndPoint != null)
                udpEndpoints.Remove(CreateEndpointKey(session.UdpRemoteEndPoint));

            messageQueue.RemoveOwner(session.Id);

            session.State = IsFault(reason)
                ? VibeNetConnectionState.Faulted
                : VibeNetConnectionState.Disconnected;

            finalInfo = MakeConnectionInfo(session);
            sessions.Remove(session.Id);
        }

        try
        {
            session.TcpClient.Close();
        }
        catch
        {
        }

        session.HandshakeLifetime.Dispose();
        session.SendLock.Dispose();

        if (publishDisconnect)
        {
            disconnectedQueue.EnqueueDroppingOldest(
                new VibeNetDisconnectInfo(
                    finalInfo,
                    reason,
                    detail,
                    DateTime.UtcNow));
        }
    }

    private void DisconnectSessionImmediate(
        ServerSession session,
        VibeNetDisconnectReason reason,
        string detail)
    {
        if (Interlocked.Exchange(ref session.DisconnectStarted, 1) != 0)
            return;

        bool publishDisconnect;
        VibeNetConnectionInfo finalInfo;

        lock (sessionsLock)
        {
            publishDisconnect = session.State == VibeNetConnectionState.Connected;

            if (session.UdpRemoteEndPoint != null)
                udpEndpoints.Remove(CreateEndpointKey(session.UdpRemoteEndPoint));

            messageQueue.RemoveOwner(session.Id);

            session.State = IsFault(reason)
                ? VibeNetConnectionState.Faulted
                : VibeNetConnectionState.Disconnected;

            finalInfo = MakeConnectionInfo(session);
            sessions.Remove(session.Id);
        }

        session.HandshakeLifetime.Cancel();

        try
        {
            session.TcpClient.Close();
        }
        catch
        {
        }

        session.HandshakeLifetime.Dispose();
        session.SendLock.Dispose();

        if (publishDisconnect)
        {
            disconnectedQueue.EnqueueDroppingOldest(
                new VibeNetDisconnectInfo(
                    finalInfo,
                    reason,
                    detail,
                    DateTime.UtcNow));
        }
    }

    private void FailServer(Exception exception, VibeNetTransport transport, VibeNetErrorCode code, string message)
    {
        ReportError(null, transport, code, message, exception);

        lock (stateLock)
            running = false;

        Lifetime.Cancel();
        CloseServerSockets();

        foreach (ServerSession session in SnapshotSessions())
            DisconnectSessionImmediate(session, VibeNetDisconnectReason.ConnectionLost, message);
    }

    private async Task AwaitBackgroundTasksAsync()
    {
        List<Task> tasks = new List<Task>
        {
            acceptTask,
            udpTask,
            heartbeatTask
        };

        tasks.AddRange(SnapshotSessions().Select(session => session.SessionTask));

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private ServerSession CreateSession(TcpClient tcp)
    {
        CancellationTokenSource handshakeLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
        handshakeLifetime.CancelAfter(Configuration.ServerConnectTimeout);

        IPEndPoint? remoteEndPoint = tcp.Client.RemoteEndPoint as IPEndPoint;
        ServerSession session = new ServerSession(
            Guid.NewGuid(),
            CreateSessionToken(),
            tcp,
            remoteEndPoint,
            handshakeLifetime);

        return session;
    }

    private ServerSession? GetConnectedSession(Guid id)
    {
        lock (sessionsLock)
        {
            if (sessions.TryGetValue(id, out ServerSession? session) &&
                session.State == VibeNetConnectionState.Connected &&
                Volatile.Read(ref session.DisconnectStarted) == 0)
            {
                return session;
            }
        }

        return null;
    }

    private bool TryGetSession(Guid id, out ServerSession? session)
    {
        lock (sessionsLock)
            return sessions.TryGetValue(id, out session);
    }

    private ServerSession? FindSessionByUdpEndPoint(IPEndPoint remoteEndPoint)
    {
        string key = CreateEndpointKey(remoteEndPoint);

        lock (sessionsLock)
        {
            if (!udpEndpoints.TryGetValue(key, out Guid id))
                return null;

            sessions.TryGetValue(id, out ServerSession? session);
            return session;
        }
    }

    private ServerSession[] SnapshotSessions()
    {
        lock (sessionsLock)
            return sessions.Values.ToArray();
    }

    private ServerSession[] SnapshotConnectedSessions()
    {
        lock (sessionsLock)
        {
            return sessions.Values
                .Where(session =>
                    session.State == VibeNetConnectionState.Connected &&
                    Volatile.Read(ref session.DisconnectStarted) == 0)
                .ToArray();
        }
    }

    private bool TryReserveClientSlot()
    {
        lock (sessionsLock)
            return sessions.Count < MaxClients;
    }

    private VibeNetConnectionInfo MakeConnectionInfo(ServerSession session)
    {
        IPAddress? remoteAddress = session.TcpRemoteEndPoint == null
            ? null
            : NormalizeDisplayAddress(session.TcpRemoteEndPoint.Address);

        int tcpRemotePort = session.TcpRemoteEndPoint?.Port ?? 0;
        int udpRemotePort = session.UdpRemoteEndPoint?.Port ?? 0;
        DateTime lastActivityUtc =
            new DateTime(Interlocked.Read(ref session.LastActivityUtcTicks), DateTimeKind.Utc);

        return new VibeNetConnectionInfo(
            session.Id,
            remoteAddress,
            tcpRemotePort,
            udpRemotePort,
            session.State,
            session.TcpConnectedAtUtc,
            session.ConnectedAtUtc,
            lastActivityUtc);
    }

    private static void TouchTcp(ServerSession session)
    {
        Interlocked.Exchange(ref session.LastTcpActivityStamp, VibeNetTime.Timestamp);
        TouchActivity(session);
    }

    private static void TouchActivity(ServerSession session)
    {
        Interlocked.Exchange(ref session.LastActivityUtcTicks, DateTime.UtcNow.Ticks);
    }

    private void CloseServerSockets()
    {
        try
        {
            tcpListener?.Stop();
        }
        catch
        {
        }

        try
        {
            udpSocket?.Close();
        }
        catch
        {
        }
    }

    private static IPAddress ResolveBindAddress(IPAddress? bindAddress, VibeNetAddressMode addressMode)
    {
        if (bindAddress == null)
        {
            return addressMode == VibeNetAddressMode.IPv4
                ? IPAddress.Any
                : IPAddress.IPv6Any;
        }

        if (addressMode == VibeNetAddressMode.IPv4 &&
            bindAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("IPv4 mode requires an IPv4 bind address.", nameof(bindAddress));
        }

        if ((addressMode == VibeNetAddressMode.IPv6 ||
             addressMode == VibeNetAddressMode.DualStack) &&
            bindAddress.AddressFamily != AddressFamily.InterNetworkV6)
        {
            throw new ArgumentException("IPv6 and DualStack modes require an IPv6 bind address.", nameof(bindAddress));
        }

        return bindAddress;
    }

    private static UdpClient CreateServerUdpSocket(IPAddress bindAddress, int port, VibeNetAddressMode addressMode)
    {
        UdpClient udp = new UdpClient(bindAddress.AddressFamily);

        try
        {
            if (addressMode == VibeNetAddressMode.DualStack)
                udp.Client.DualMode = true;

            udp.Client.Bind(new IPEndPoint(bindAddress, port));
            return udp;
        }
        catch
        {
            udp.Dispose();
            throw;
        }
    }

    private static byte[] CreateSessionToken()
    {
        byte[] token = new byte[VibeNetProtocol.SessionTokenSize];
        RandomNumberGenerator.Fill(token);
        return token;
    }

    private static bool IsFault(VibeNetDisconnectReason reason)
    {
        return reason == VibeNetDisconnectReason.Timeout ||
               reason == VibeNetDisconnectReason.ConnectionLost ||
               reason == VibeNetDisconnectReason.ProtocolError ||
               reason == VibeNetDisconnectReason.ResourceLimit;
    }

    private static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static string CreateEndpointKey(IPEndPoint endPoint)
    {
        IPAddress normalized = NormalizeDisplayAddress(endPoint.Address) ?? endPoint.Address;
        return "[" + normalized + "]:" + endPoint.Port;
    }

    private static IPAddress? NormalizeDisplayAddress(IPAddress? address)
    {
        if (address != null && address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();

        return address;
    }
}

public sealed class VibeNetClient : VibeNetNode
{
    private enum ConnectStage
    {
        Resolve,
        TcpConnect,
        Hello,
        UdpHandshake,
        ReadySend,
        ReadyAck
    }

    private readonly object stateLock = new object();
    private readonly BoundedQueue<VibeNetMessage> messageQueue;
    private readonly DropOldestQueue<VibeNetDisconnectInfo> disconnectedQueue;
    private readonly SemaphoreSlim tcpSendLock = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim udpSendLock = new SemaphoreSlim(1, 1);

    private TcpClient? tcpClient;
    private NetworkStream? tcpStream;
    private UdpClient? udpSocket;

    private Task tcpTask = Task.CompletedTask;
    private Task udpTask = Task.CompletedTask;
    private Task heartbeatTask = Task.CompletedTask;
    private Task? shutdownTask;

    private bool startAttempted;
    private bool disposed;
    private bool shutdownStarted;
    private bool connectionEstablished;

    private byte[]? sessionToken;
    private Guid connectionId;
    private IPAddress? remoteAddress;
    private IPAddress? connectedRemoteAddress;
    private DateTime tcpConnectedAtUtc;
    private DateTime? connectedAtUtc;
    private long lastActivityUtcTicks;
    private long lastTcpActivityStamp;
    private int tcpRemotePort;
    private int udpRemotePort;
    private VibeNetConnectionState state = VibeNetConnectionState.Created;
    private VibeNetDisconnectReason? lastDisconnectReason;

    public string RemoteHost { get; }

    public Guid ConnectionId
    {
        get
        {
            lock (stateLock)
                return connectionId;
        }
    }

    public IPAddress? RemoteAddress
    {
        get
        {
            lock (stateLock)
                return remoteAddress;
        }
    }

    public VibeNetConnectionState State
    {
        get
        {
            lock (stateLock)
                return state;
        }
    }

    public bool IsConnected => State == VibeNetConnectionState.Connected;

    public VibeNetDisconnectReason? LastDisconnectReason
    {
        get
        {
            lock (stateLock)
                return lastDisconnectReason;
        }
    }

    public VibeNetConnectionInfo Connection
    {
        get
        {
            lock (stateLock)
            {
                DateTime lastActivity = lastActivityUtcTicks == 0
                    ? DateTime.MinValue
                    : new DateTime(lastActivityUtcTicks, DateTimeKind.Utc);

                return new VibeNetConnectionInfo(
                    connectionId,
                    remoteAddress,
                    tcpRemotePort,
                    udpRemotePort,
                    state,
                    tcpConnectedAtUtc,
                    connectedAtUtc,
                    lastActivity);
            }
        }
    }

    public VibeNetClient(
        string remoteHost,
        int tcpPort = 7777,
        int? udpPort = null,
        VibeNetConfiguration? configuration = null)
        : base(tcpPort, udpPort, configuration)
    {
        if (string.IsNullOrWhiteSpace(remoteHost))
            throw new ArgumentException("Remote host is required.", nameof(remoteHost));

        RemoteHost = remoteHost;
        messageQueue = new BoundedQueue<VibeNetMessage>(Configuration.MaxQueuedMessages);
        disconnectedQueue =
            new DropOldestQueue<VibeNetDisconnectInfo>(Configuration.MaxQueuedEvents);
    }

    public override bool TryDequeueMessage(out VibeNetMessage message)
    {
        return messageQueue.TryDequeue(out message);
    }

    public bool TryDequeueDisconnected(out VibeNetDisconnectInfo disconnect)
    {
        return disconnectedQueue.TryDequeue(out disconnect);
    }

    public override async Task<VibeNetConnectResult> StartAsync(
        CancellationToken cancellationToken = default)
    {
        lock (stateLock)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(VibeNetClient));

            if (startAttempted)
                throw new InvalidOperationException("Client instances are single-use.");

            startAttempted = true;
            state = VibeNetConnectionState.Connecting;
        }

        ConnectStage stage = ConnectStage.Resolve;

        using CancellationTokenSource timeout =
            VibeNetCancellation.CreateTimeoutSource(Configuration.ClientConnectTimeout);
        using CancellationTokenSource startup =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                Lifetime.Token,
                timeout.Token);

        CancellationToken token = startup.Token;

        try
        {
            IPAddress[] addresses = await ResolveRemoteAddressesAsync(token).ConfigureAwait(false);

            stage = ConnectStage.TcpConnect;
            SocketException? lastConnectError = null;
            foreach (IPAddress address in addresses)
            {
                try
                {
                    TcpClient candidate = new TcpClient(address.AddressFamily);
                    await VibeNetSocket.ConnectAsync(candidate, address, TCPPort, token).ConfigureAwait(false);
                    tcpClient = candidate;
                    tcpStream = candidate.GetStream();
                    connectedRemoteAddress = address;

                    IPEndPoint? remoteEndPoint = candidate.Client.RemoteEndPoint as IPEndPoint;
                    tcpRemotePort = remoteEndPoint?.Port ?? TCPPort;
                    break;
                }
                catch (SocketException ex)
                {
                    lastConnectError = ex;
                }
            }

            if (tcpClient == null || tcpStream == null || connectedRemoteAddress == null)
            {
                VibeNetConnectFailure failure =
                    lastConnectError != null &&
                    lastConnectError.SocketErrorCode == SocketError.ConnectionRefused
                    ? VibeNetConnectFailure.ConnectionRefused
                    : VibeNetConnectFailure.SocketError;

                return FailStart(failure, lastConnectError?.Message ?? "Unable to connect.");
            }

            tcpConnectedAtUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref lastActivityUtcTicks, tcpConnectedAtUtc.Ticks);
            Interlocked.Exchange(ref lastTcpActivityStamp, VibeNetTime.Timestamp);

            lock (stateLock)
                remoteAddress = NormalizeDisplayAddress(connectedRemoteAddress);

            stage = ConnectStage.Hello;
            NetworkFrame? hello = await VibeNetProtocol.ReadTcpFrameAsync(
                tcpStream,
                VibeNetProtocol.MaxControlPayloadBytes,
                token).ConfigureAwait(false);

            if (hello == null)
                return FailStart(VibeNetConnectFailure.TCPHandshakeFailed, "Server closed during handshake.");

            if (hello.Type == VibeNetPacketType.Reject)
                return FailStart(VibeNetConnectFailure.ServerRejected, VibeNetProtocol.ParseRejectPayload(hello.Payload));

            if (hello.Type != VibeNetPacketType.Hello ||
                !VibeNetProtocol.TryParseHelloPayload(hello.Payload, out Guid assignedId, out byte[]? tokenBytes))
            {
                return FailStart(VibeNetConnectFailure.TCPHandshakeFailed, "Invalid HELLO packet.");
            }

            lock (stateLock)
                connectionId = assignedId;

            sessionToken = tokenBytes;

            udpSocket = new UdpClient(connectedRemoteAddress.AddressFamily);
            udpSocket.Connect(connectedRemoteAddress, UDPPort);
            udpRemotePort = UDPPort;

            stage = ConnectStage.UdpHandshake;
            VibeNetConnectFailure udpFailure =
                await PerformUdpHandshakeAsync(token).ConfigureAwait(false);

            if (udpFailure != VibeNetConnectFailure.None)
            {
                return FailStart(
                    udpFailure,
                    udpFailure == VibeNetConnectFailure.UDPHandshakeFailed
                        ? "UDP registration acknowledgement was not received."
                        : "Connection attempt failed.");
            }

            stage = ConnectStage.ReadySend;
            await SendTcpFrameAsync(
                VibeNetPacketType.Ready,
                VibeNetProtocol.EmptyPayload,
                token).ConfigureAwait(false);

            stage = ConnectStage.ReadyAck;
            NetworkFrame? readyAck = await VibeNetProtocol.ReadTcpFrameAsync(
                tcpStream,
                VibeNetProtocol.MaxControlPayloadBytes,
                token).ConfigureAwait(false);

            if (readyAck == null || readyAck.Type != VibeNetPacketType.ReadyAck)
            {
                return FailStart(
                    VibeNetConnectFailure.ReadyHandshakeFailed,
                    "READY acknowledgement was not received.");
            }

            VibeNetProtocol.RequireEmptyPayload(readyAck);

            lock (stateLock)
            {
                if (disposed || shutdownStarted)
                    return FailStart(VibeNetConnectFailure.Cancelled, "Connection was closed during startup.");

                connectedAtUtc = DateTime.UtcNow;
                state = VibeNetConnectionState.Connected;
                connectionEstablished = true;
            }

            TouchTcp();

            tcpTask = RunTcpLoopAsync();
            udpTask = RunUdpLoopAsync();
            heartbeatTask = RunHeartbeatLoopAsync();

            return VibeNetConnectResult.Succeeded();
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested || Lifetime.IsCancellationRequested)
                return FailStart(VibeNetConnectFailure.Cancelled, "Connection attempt was cancelled.");

            if (stage == ConnectStage.UdpHandshake)
                return FailStart(VibeNetConnectFailure.UDPHandshakeFailed, "UDP registration acknowledgement was not received.");

            return FailStart(VibeNetConnectFailure.Timeout, "Connection attempt timed out.");
        }
        catch (VibeNetProtocolException ex)
        {
            return stage switch
            {
                ConnectStage.ReadySend or ConnectStage.ReadyAck =>
                    FailStart(VibeNetConnectFailure.ReadyHandshakeFailed, ex.Message),
                _ => FailStart(
                    ex.ProtocolMismatch
                        ? VibeNetConnectFailure.ProtocolMismatch
                        : VibeNetConnectFailure.TCPHandshakeFailed,
                    ex.Message)
            };
        }
        catch (SocketException ex)
        {
            VibeNetConnectFailure failure =
                ex.SocketErrorCode == SocketError.ConnectionRefused
                ? VibeNetConnectFailure.ConnectionRefused
                : VibeNetConnectFailure.SocketError;

            return FailStart(failure, ex.Message);
        }
        catch (Exception ex)
        {
            return FailStart(VibeNetConnectFailure.SocketError, ex.Message);
        }
    }

    public async Task SendAsync(
        byte[] data,
        VibeNetTransport transport,
        CancellationToken cancellationToken = default)
    {
        ValidatePayload(data, transport);

        if (!IsConnected)
            throw new InvalidOperationException("Client is not connected.");

        if (transport == VibeNetTransport.TCP)
        {
            try
            {
                await SendTcpFrameAsync(
                    VibeNetPacketType.Data,
                    data,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ReportError(
                    ConnectionId,
                    VibeNetTransport.TCP,
                    VibeNetErrorCode.SendFailure,
                    "TCP send failed.",
                    ex);

                await BeginShutdownAsync(
                    VibeNetDisconnectReason.ConnectionLost,
                    "TCP send failed.",
                    true,
                    false).ConfigureAwait(false);

                throw;
            }
        }

        try
        {
            await SendUdpFrameAsync(
                VibeNetPacketType.Data,
                data,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportError(
                ConnectionId,
                VibeNetTransport.UDP,
                VibeNetErrorCode.SendFailure,
                "UDP send failed.",
                ex);

            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Task shutdown = BeginShutdownAsync(
            VibeNetDisconnectReason.LocalRequested,
            "Client disconnected.",
            true,
            true);

        await VibeNetTask.WaitAsync(shutdown, cancellationToken).ConfigureAwait(false);
        await VibeNetTask.WaitAsync(AwaitBackgroundTasksAsync(), cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        lock (stateLock)
        {
            if (disposed)
                return;

            disposed = true;
        }

        _ = BeginShutdownAsync(
            VibeNetDisconnectReason.LocalRequested,
            "Client disposed.",
            connectionEstablished,
            false);
    }

    private async Task<IPAddress[]> ResolveRemoteAddressesAsync(CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(RemoteHost, out IPAddress? parsed))
        {
            IPAddress[] single = FilterAddresses(new[] { parsed });
            if (single.Length == 0)
            {
                throw new SocketException((int)SocketError.AddressFamilyNotSupported);
            }

            return single;
        }

        IPAddress[] addresses = await VibeNetSocket.GetHostAddressesAsync(RemoteHost, cancellationToken)
            .ConfigureAwait(false);

        IPAddress[] filtered = FilterAddresses(addresses);
        if (filtered.Length == 0)
            throw new VibeNetProtocolException("No resolved address matches the configured address mode.");

        return filtered;
    }

    private IPAddress[] FilterAddresses(IEnumerable<IPAddress> addresses)
    {
        return addresses.Where(address =>
        {
            return Configuration.AddressMode switch
            {
                VibeNetAddressMode.IPv4 => address.AddressFamily == AddressFamily.InterNetwork,
                VibeNetAddressMode.IPv6 => address.AddressFamily == AddressFamily.InterNetworkV6,
                VibeNetAddressMode.DualStack => address.AddressFamily == AddressFamily.InterNetwork ||
                                                address.AddressFamily == AddressFamily.InterNetworkV6,
                _ => throw new ArgumentOutOfRangeException(nameof(Configuration.AddressMode))
            };
        }).ToArray();
    }

    private async Task<VibeNetConnectFailure> PerformUdpHandshakeAsync(CancellationToken overallToken)
    {
        if (udpSocket == null || sessionToken == null)
            return VibeNetConnectFailure.UDPHandshakeFailed;

        using CancellationTokenSource udpTimeout =
            VibeNetCancellation.CreateLinkedTimeoutSource(Configuration.UDPHandshakeTimeout, overallToken);

        CancellationToken token = udpTimeout.Token;
        byte[] payload = VibeNetProtocol.CreateRegistrationPayload(ConnectionId, sessionToken);
        Task<UdpReceiveResult> receiveTask = VibeNetSocket.ReceiveAsync(udpSocket, token);

        await SendUdpFrameAsync(VibeNetPacketType.UdpRegister, payload, token).ConfigureAwait(false);
        long nextSendAt = VibeNetTime.Timestamp + VibeNetTime.ToStopwatchTicks(Configuration.UDPHandshakeInterval);

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();

                TimeSpan waitForSend = VibeNetTime.Remaining(nextSendAt);
                Task timerTask = Task.Delay(waitForSend, token);
                Task completed = await Task.WhenAny(receiveTask, timerTask).ConfigureAwait(false);

                if (completed == receiveTask)
                {
                    UdpReceiveResult result = await receiveTask.ConfigureAwait(false);

                    if (VibeNetProtocol.TryParseUdpFrame(
                        result.Buffer,
                        VibeNetProtocol.MaxControlPayloadBytes,
                        out NetworkFrame? frame) &&
                        frame != null &&
                        frame.Type == VibeNetPacketType.UdpAck &&
                        VibeNetProtocol.RegistrationAckMatches(frame.Payload, ConnectionId))
                    {
                        return VibeNetConnectFailure.None;
                    }

                    receiveTask = VibeNetSocket.ReceiveAsync(udpSocket, token);
                    continue;
                }

                await timerTask.ConfigureAwait(false);
                await SendUdpFrameAsync(VibeNetPacketType.UdpRegister, payload, token).ConfigureAwait(false);
                nextSendAt = VibeNetTime.Timestamp + VibeNetTime.ToStopwatchTicks(Configuration.UDPHandshakeInterval);
            }
        }
        catch (OperationCanceledException)
        {
            if (overallToken.IsCancellationRequested)
                throw;

            return VibeNetConnectFailure.UDPHandshakeFailed;
        }
    }

    private async Task RunTcpLoopAsync()
    {
        if (tcpStream == null)
            return;

        VibeNetDisconnectReason finalReason = VibeNetDisconnectReason.ConnectionLost;
        string finalDetail = "TCP connection closed.";

        try
        {
            while (!Lifetime.IsCancellationRequested)
            {
                NetworkFrame? frame = await VibeNetProtocol.ReadTcpFrameAsync(
                    tcpStream,
                    Math.Max(Configuration.MaxTCPPayloadBytes, VibeNetProtocol.MaxControlPayloadBytes),
                    Lifetime.Token).ConfigureAwait(false);

                if (frame == null)
                    break;

                TouchTcp();
                await HandleConnectedTcpFrameAsync(frame).ConfigureAwait(false);
            }
        }
        catch (VibeNetDisconnectSignal signal)
        {
            finalReason = signal.Reason;
            finalDetail = signal.Detail;
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested)
        {
            finalReason = VibeNetDisconnectReason.LocalRequested;
            finalDetail = "Client disconnected.";
        }
        catch (VibeNetProtocolException ex)
        {
            ReportError(
                ConnectionId,
                VibeNetTransport.TCP,
                VibeNetErrorCode.ProtocolError,
                ex.Message,
                ex);

            finalReason = VibeNetDisconnectReason.ProtocolError;
            finalDetail = ex.Message;
        }
        catch (IOException ex)
        {
            if (!shutdownStarted)
            {
                ReportError(
                    ConnectionId,
                    VibeNetTransport.TCP,
                    VibeNetErrorCode.ReceiveFailure,
                    "TCP receive failed.",
                    ex);
            }
        }
        catch (SocketException ex)
        {
            if (!shutdownStarted)
            {
                ReportError(
                    ConnectionId,
                    VibeNetTransport.TCP,
                    VibeNetErrorCode.SocketError,
                    "TCP receive failed.",
                    ex);
            }
        }
        catch (Exception ex)
        {
            if (!shutdownStarted)
            {
                ReportError(
                    ConnectionId,
                    VibeNetTransport.TCP,
                    VibeNetErrorCode.InternalError,
                    "Unexpected TCP receive failure.",
                    ex);
            }
        }
        finally
        {
            await BeginShutdownAsync(finalReason, finalDetail, true, false).ConfigureAwait(false);
        }
    }

    private async Task HandleConnectedTcpFrameAsync(NetworkFrame frame)
    {
        switch (frame.Type)
        {
            case VibeNetPacketType.Ping:
                VibeNetProtocol.RequireEmptyPayload(frame);
                using (CancellationTokenSource timeout = VibeNetCancellation.CreateTimeoutSource(Configuration.TCPHeartbeatInterval))
                {
                    await SendTcpFrameAsync(
                        VibeNetPacketType.Pong,
                        VibeNetProtocol.EmptyPayload,
                        timeout.Token).ConfigureAwait(false);
                }

                return;

            case VibeNetPacketType.Data:
                if (frame.Payload.Length > Configuration.MaxTCPPayloadBytes)
                    throw new VibeNetProtocolException("TCP payload exceeds configured maximum.");

                if (!messageQueue.TryEnqueue(
                    new VibeNetMessage(null, VibeNetTransport.TCP, frame.Payload)))
                {
                    ReportError(
                        ConnectionId,
                        VibeNetTransport.TCP,
                        VibeNetErrorCode.QueueOverflow,
                        "Reliable message queue limit exceeded.");

                    throw new VibeNetDisconnectSignal(
                        VibeNetDisconnectReason.ResourceLimit,
                        "Reliable message queue limit exceeded.");
                }

                return;

            case VibeNetPacketType.Disconnect:
                VibeNetDisconnectCode code = VibeNetProtocol.ParseDisconnectPayload(frame.Payload);
                if (code == VibeNetDisconnectCode.ClientRequested)
                    throw new VibeNetProtocolException("Server sent an invalid disconnect code.");

                throw new VibeNetDisconnectSignal(
                    code == VibeNetDisconnectCode.ServerStopping
                        ? VibeNetDisconnectReason.ServerStopped
                        : VibeNetDisconnectReason.RemoteRequested,
                    code == VibeNetDisconnectCode.ServerStopping
                        ? "Server stopped."
                        : "Server requested disconnection.");

            default:
                throw new VibeNetProtocolException("Unexpected TCP packet from server: " + frame.Type + ".");
        }
    }

    private async Task RunUdpLoopAsync()
    {
        if (udpSocket == null)
            return;

        try
        {
            while (!Lifetime.IsCancellationRequested)
            {
                UdpReceiveResult result =
                    await VibeNetSocket.ReceiveAsync(udpSocket, Lifetime.Token).ConfigureAwait(false);

                if (!VibeNetProtocol.TryParseUdpFrame(
                    result.Buffer,
                    Math.Max(Configuration.MaxUDPPayloadBytes, VibeNetProtocol.MaxControlPayloadBytes),
                    out NetworkFrame? frame))
                {
                    continue;
                }

                if (frame == null || frame.Type != VibeNetPacketType.Data)
                    continue;

                if (frame.Payload.Length > Configuration.MaxUDPPayloadBytes)
                {
                    ReportError(
                        ConnectionId,
                        VibeNetTransport.UDP,
                        VibeNetErrorCode.ProtocolError,
                        "UDP payload exceeds configured maximum.");
                    continue;
                }

                TouchActivity();

                if (!messageQueue.TryEnqueue(
                    new VibeNetMessage(null, VibeNetTransport.UDP, frame.Payload)))
                {
                    ReportError(
                        ConnectionId,
                        VibeNetTransport.UDP,
                        VibeNetErrorCode.QueueOverflow,
                        "Incoming UDP datagram dropped because the queue is full.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            if (!shutdownStarted)
            {
                ReportError(
                    ConnectionId,
                    VibeNetTransport.UDP,
                    ex is SocketException ? VibeNetErrorCode.SocketError : VibeNetErrorCode.InternalError,
                    "UDP receive loop failed.",
                    ex);
            }

            await BeginShutdownAsync(
                VibeNetDisconnectReason.ConnectionLost,
                "UDP transport failed.",
                true,
                false).ConfigureAwait(false);
        }
    }

    private async Task RunHeartbeatLoopAsync()
    {
        try
        {
            while (!Lifetime.IsCancellationRequested)
            {
                await Task.Delay(Configuration.TCPHeartbeatInterval, Lifetime.Token).ConfigureAwait(false);

                if (VibeNetTime.HasElapsed(
                    Interlocked.Read(ref lastTcpActivityStamp),
                    Configuration.TCPHeartbeatTimeout))
                {
                    await BeginShutdownAsync(
                        VibeNetDisconnectReason.Timeout,
                        "TCP heartbeat timed out.",
                        true,
                        false).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendTcpFrameAsync(
        VibeNetPacketType type,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        NetworkStream stream = tcpStream ?? throw new ObjectDisposedException(nameof(VibeNetClient));

        await tcpSendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != VibeNetConnectionState.Connected &&
                type == VibeNetPacketType.Data)
            {
                throw new InvalidOperationException("Client is not connected.");
            }

            await VibeNetProtocol.WriteTcpFrameAsync(
                stream,
                type,
                payload,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                tcpClient?.Close();
            }
            catch
            {
            }

            throw;
        }
        finally
        {
            tcpSendLock.Release();
        }
    }

    private async Task SendUdpFrameAsync(
        VibeNetPacketType type,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        UdpClient udp = udpSocket ?? throw new ObjectDisposedException(nameof(VibeNetClient));
        byte[] datagram = VibeNetProtocol.CreateUdpFrame(type, payload);

        await udpSendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await VibeNetSocket.SendConnectedAsync(udp, datagram, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            udpSendLock.Release();
        }
    }

    private Task BeginShutdownAsync(
        VibeNetDisconnectReason reason,
        string detail,
        bool publishEvent,
        bool notifyRemote)
    {
        lock (stateLock)
        {
            if (shutdownTask != null)
                return shutdownTask;

            shutdownStarted = true;
            shutdownTask = ShutdownCoreAsync(reason, detail, publishEvent, notifyRemote);
            return shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync(
        VibeNetDisconnectReason reason,
        string detail,
        bool publishEvent,
        bool notifyRemote)
    {
        bool shouldPublish;
        Guid currentId;
        IPAddress? currentAddress;
        int currentTcpPort;
        int currentUdpPort;
        DateTime currentTcpConnectedAtUtc;
        DateTime? currentConnectedAtUtc;
        DateTime currentLastActivityUtc;

        lock (stateLock)
        {
            currentId = connectionId;
            currentAddress = remoteAddress;
            currentTcpPort = tcpRemotePort;
            currentUdpPort = udpRemotePort;
            currentTcpConnectedAtUtc = tcpConnectedAtUtc;
            currentConnectedAtUtc = connectedAtUtc;
            currentLastActivityUtc = lastActivityUtcTicks == 0
                ? DateTime.MinValue
                : new DateTime(lastActivityUtcTicks, DateTimeKind.Utc);

            if (connectionEstablished)
                lastDisconnectReason = reason;

            if (state == VibeNetConnectionState.Connected ||
                state == VibeNetConnectionState.Connecting)
            {
                state = VibeNetConnectionState.Disconnecting;
            }

            shouldPublish = publishEvent && connectionEstablished;
        }

        if (notifyRemote && connectionEstablished)
        {
            using CancellationTokenSource timeout = VibeNetCancellation.CreateTimeoutSource(VibeNetDefaults.ControlFrameTimeout);

            try
            {
                await SendTcpFrameAsync(
                    VibeNetPacketType.Disconnect,
                    VibeNetProtocol.CreateDisconnectPayload(VibeNetDisconnectCode.ClientRequested),
                    timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is OperationCanceledException ||
                ex is IOException ||
                ex is SocketException ||
                ex is ObjectDisposedException)
            {
                ReportError(
                    currentId,
                    VibeNetTransport.TCP,
                    VibeNetErrorCode.SendFailure,
                    "Failed to send disconnect control frame.",
                    ex);
            }
        }

        Lifetime.Cancel();
        CloseClientSockets();

        lock (stateLock)
        {
            state = IsFault(reason)
                ? VibeNetConnectionState.Faulted
                : VibeNetConnectionState.Disconnected;
        }

        if (shouldPublish)
        {
            disconnectedQueue.EnqueueDroppingOldest(
                new VibeNetDisconnectInfo(
                    new VibeNetConnectionInfo(
                        currentId,
                        currentAddress,
                        currentTcpPort,
                        currentUdpPort,
                        IsFault(reason)
                            ? VibeNetConnectionState.Faulted
                            : VibeNetConnectionState.Disconnected,
                        currentTcpConnectedAtUtc,
                        currentConnectedAtUtc,
                        currentLastActivityUtc),
                    reason,
                    detail,
                    DateTime.UtcNow));
        }
    }

    private async Task AwaitBackgroundTasksAsync()
    {
        try
        {
            await Task.WhenAll(tcpTask, udpTask, heartbeatTask).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private VibeNetConnectResult FailStart(VibeNetConnectFailure failure, string detail)
    {
        CloseClientSockets();
        Lifetime.Cancel();

        lock (stateLock)
        {
            state =
                failure == VibeNetConnectFailure.Cancelled
                ? VibeNetConnectionState.Disconnected
                : VibeNetConnectionState.Faulted;
        }

        return VibeNetConnectResult.Failed(failure, detail);
    }

    private void CloseClientSockets()
    {
        try
        {
            udpSocket?.Close();
        }
        catch
        {
        }

        try
        {
            tcpClient?.Close();
        }
        catch
        {
        }
    }

    private void TouchTcp()
    {
        Interlocked.Exchange(ref lastTcpActivityStamp, VibeNetTime.Timestamp);
        TouchActivity();
    }

    private void TouchActivity()
    {
        long now = DateTime.UtcNow.Ticks;
        Interlocked.Exchange(ref lastActivityUtcTicks, now);
    }

    private static bool IsFault(VibeNetDisconnectReason reason)
    {
        return reason == VibeNetDisconnectReason.Timeout ||
               reason == VibeNetDisconnectReason.ConnectionLost ||
               reason == VibeNetDisconnectReason.ProtocolError ||
               reason == VibeNetDisconnectReason.ResourceLimit;
    }

    private static IPAddress? NormalizeDisplayAddress(IPAddress? address)
    {
        if (address != null && address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();

        return address;
    }
}

internal enum VibeNetPacketType : byte
{
    Hello = 1,
    Reject = 2,
    UdpRegister = 3,
    UdpAck = 4,
    Ready = 5,
    ReadyAck = 6,
    Data = 7,
    Ping = 8,
    Pong = 9,
    Disconnect = 10
}

internal enum VibeNetDisconnectCode : byte
{
    ClientRequested = 1,
    ServerRequested = 2,
    ServerStopping = 3
}

internal sealed class NetworkFrame
{
    public VibeNetPacketType Type { get; }
    public byte[] Payload { get; }

    public NetworkFrame(VibeNetPacketType type, byte[] payload)
    {
        Type = type;
        Payload = payload;
    }
}

internal sealed class VibeNetProtocolException : Exception
{
    public bool ProtocolMismatch { get; }

    public VibeNetProtocolException(string message, bool protocolMismatch = false)
        : base(message)
    {
        ProtocolMismatch = protocolMismatch;
    }
}

internal sealed class VibeNetDisconnectSignal : Exception
{
    public VibeNetDisconnectReason Reason { get; }
    public string Detail { get; }

    public VibeNetDisconnectSignal(VibeNetDisconnectReason reason, string detail)
    {
        Reason = reason;
        Detail = detail;
    }
}

internal static class VibeNetProtocol
{
    public const int HeaderSize = 12;
    public const ushort Version = 1;
    public const int SessionTokenSize = 32;
    public const int MaxControlPayloadBytes = 1024;
    public const int MaxUserUdpPayloadBytes = 65507 - HeaderSize;

    private const uint Magic = 0x56494245; // "VIBE"

    public static readonly byte[] EmptyPayload = Array.Empty<byte>();

    public static async Task WriteTcpFrameAsync(
        NetworkStream stream,
        VibeNetPacketType type,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        payload ??= EmptyPayload;
        byte[] header = CreateHeader(type, payload.Length);

        await stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);

        if (payload.Length > 0)
            await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<NetworkFrame?> ReadTcpFrameAsync(
        NetworkStream stream,
        int maximumPayloadBytes,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[HeaderSize];
        bool hasHeader = await ReadExactAsync(
            stream,
            header,
            0,
            header.Length,
            true,
            cancellationToken).ConfigureAwait(false);

        if (!hasHeader)
            return null;

        ParseHeader(header, maximumPayloadBytes, out VibeNetPacketType type, out int payloadLength);

        byte[] payload = payloadLength == 0 ? EmptyPayload : new byte[payloadLength];
        if (payloadLength > 0)
        {
            await ReadExactAsync(
                stream,
                payload,
                0,
                payload.Length,
                false,
                cancellationToken).ConfigureAwait(false);
        }

        return new NetworkFrame(type, payload);
    }

    public static byte[] CreateUdpFrame(VibeNetPacketType type, byte[] payload)
    {
        payload ??= EmptyPayload;
        byte[] frame = new byte[HeaderSize + payload.Length];
        byte[] header = CreateHeader(type, payload.Length);
        Buffer.BlockCopy(header, 0, frame, 0, HeaderSize);

        if (payload.Length > 0)
            Buffer.BlockCopy(payload, 0, frame, HeaderSize, payload.Length);

        return frame;
    }

    public static bool TryParseUdpFrame(
        byte[] datagram,
        int maximumPayloadBytes,
        out NetworkFrame? frame)
    {
        frame = null;

        if (datagram == null || datagram.Length < HeaderSize)
            return false;

        byte[] header = new byte[HeaderSize];
        Buffer.BlockCopy(datagram, 0, header, 0, HeaderSize);

        try
        {
            ParseHeader(header, maximumPayloadBytes, out VibeNetPacketType type, out int payloadLength);

            if (datagram.Length != HeaderSize + payloadLength)
                return false;

            byte[] payload = payloadLength == 0 ? EmptyPayload : new byte[payloadLength];
            if (payloadLength > 0)
                Buffer.BlockCopy(datagram, HeaderSize, payload, 0, payloadLength);

            frame = new NetworkFrame(type, payload);
            return true;
        }
        catch (VibeNetProtocolException)
        {
            return false;
        }
    }

    public static byte[] CreateHelloPayload(Guid connectionId, byte[] token)
    {
        if (token == null || token.Length != SessionTokenSize)
            throw new ArgumentException("Invalid session token.", nameof(token));

        byte[] payload = new byte[16 + SessionTokenSize];
        byte[] idBytes = GuidToNetworkBytes(connectionId);
        Buffer.BlockCopy(idBytes, 0, payload, 0, 16);
        Buffer.BlockCopy(token, 0, payload, 16, SessionTokenSize);
        return payload;
    }

    public static bool TryParseHelloPayload(byte[] payload, out Guid connectionId, out byte[]? token)
    {
        connectionId = Guid.Empty;
        token = null;

        if (payload == null || payload.Length != 16 + SessionTokenSize)
            return false;

        byte[] idBytes = new byte[16];
        byte[] tokenBytes = new byte[SessionTokenSize];

        Buffer.BlockCopy(payload, 0, idBytes, 0, 16);
        Buffer.BlockCopy(payload, 16, tokenBytes, 0, SessionTokenSize);

        connectionId = GuidFromNetworkBytes(idBytes);
        token = tokenBytes;
        return true;
    }

    public static byte[] CreateRegistrationPayload(Guid connectionId, byte[] token)
    {
        return CreateHelloPayload(connectionId, token);
    }

    public static bool TryParseRegistrationPayload(byte[] payload, out Guid connectionId, out byte[]? token)
    {
        return TryParseHelloPayload(payload, out connectionId, out token);
    }

    public static bool RegistrationAckMatches(byte[] payload, Guid expectedId)
    {
        return payload.Length == 16 && GuidFromNetworkBytes(payload) == expectedId;
    }

    public static byte[] CreateRejectPayload(string? reason)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(reason ?? "Rejected.");
        if (bytes.Length > MaxControlPayloadBytes)
            Array.Resize(ref bytes, MaxControlPayloadBytes);

        return bytes;
    }

    public static string ParseRejectPayload(byte[] payload)
    {
        return payload == null ? "Rejected." : Encoding.UTF8.GetString(payload);
    }

    public static byte[] CreateDisconnectPayload(VibeNetDisconnectCode code)
    {
        return new[] { (byte)code };
    }

    public static VibeNetDisconnectCode ParseDisconnectPayload(byte[] payload)
    {
        if (payload == null || payload.Length != 1)
            throw new VibeNetProtocolException("Invalid DISCONNECT payload.");

        VibeNetDisconnectCode code = (VibeNetDisconnectCode)payload[0];
        if (code != VibeNetDisconnectCode.ClientRequested &&
            code != VibeNetDisconnectCode.ServerRequested &&
            code != VibeNetDisconnectCode.ServerStopping)
        {
            throw new VibeNetProtocolException("Unknown DISCONNECT code.");
        }

        return code;
    }

    public static void RequireEmptyPayload(NetworkFrame frame)
    {
        if (frame.Payload.Length != 0)
            throw new VibeNetProtocolException(frame.Type + " packets must not carry a payload.");
    }

    public static byte[] GuidToNetworkBytes(Guid value)
    {
        string text = value.ToString("N");
        byte[] bytes = new byte[16];
        for (int index = 0; index < 16; index++)
            bytes[index] = Convert.ToByte(text.Substring(index * 2, 2), 16);

        return bytes;
    }

    public static Guid GuidFromNetworkBytes(byte[] bytes)
    {
        if (bytes == null || bytes.Length != 16)
            throw new ArgumentException("A network Guid requires exactly 16 bytes.", nameof(bytes));

        StringBuilder builder = new StringBuilder(32);
        for (int index = 0; index < bytes.Length; index++)
            builder.Append(bytes[index].ToString("x2"));

        return Guid.ParseExact(builder.ToString(), "N");
    }

    private static byte[] CreateHeader(VibeNetPacketType type, int payloadLength)
    {
        if (payloadLength < 0)
            throw new ArgumentOutOfRangeException(nameof(payloadLength));

        byte[] header = new byte[HeaderSize];
        WriteUInt32(header, 0, Magic);
        WriteUInt16(header, 4, Version);
        header[6] = (byte)type;
        header[7] = 0;
        WriteUInt32(header, 8, (uint)payloadLength);
        return header;
    }

    private static void ParseHeader(
        byte[] header,
        int maximumPayloadBytes,
        out VibeNetPacketType type,
        out int payloadLength)
    {
        if (ReadUInt32(header, 0) != Magic)
            throw new VibeNetProtocolException("Protocol magic mismatch.", true);

        ushort version = ReadUInt16(header, 4);
        if (version != Version)
        {
            throw new VibeNetProtocolException(
                "Unsupported protocol version " + version + "; expected " + Version + ".",
                true);
        }

        if (header[7] != 0)
            throw new VibeNetProtocolException("Reserved protocol header bits must be zero.");

        byte rawType = header[6];
        if (!Enum.IsDefined(typeof(VibeNetPacketType), rawType))
            throw new VibeNetProtocolException("Unknown packet type " + rawType + ".");

        uint rawPayloadLength = ReadUInt32(header, 8);
        if (rawPayloadLength > int.MaxValue || rawPayloadLength > maximumPayloadBytes)
            throw new VibeNetProtocolException("Frame payload exceeds the configured maximum.");

        type = (VibeNetPacketType)rawType;
        payloadLength = (int)rawPayloadLength;
    }

    private static async Task<bool> ReadExactAsync(
        Stream stream,
        byte[] buffer,
        int offset,
        int count,
        bool allowCleanEndOfStream,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < count)
        {
            int read = await stream.ReadAsync(
                buffer,
                offset + total,
                count - total,
                cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                if (total == 0 && allowCleanEndOfStream)
                    return false;

                throw new EndOfStreamException("Connection closed in the middle of a frame.");
            }

            total += read;
        }

        return true;
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }

    private static ushort ReadUInt16(byte[] buffer, int offset)
    {
        return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint ReadUInt32(byte[] buffer, int offset)
    {
        return ((uint)buffer[offset] << 24) |
               ((uint)buffer[offset + 1] << 16) |
               ((uint)buffer[offset + 2] << 8) |
               buffer[offset + 3];
    }
}

internal sealed class BoundedQueue<T>
{
    private readonly object sync = new object();
    private readonly Queue<T> queue = new Queue<T>();
    private readonly int capacity;

    public BoundedQueue(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        this.capacity = capacity;
    }

    public bool TryEnqueue(T item)
    {
        lock (sync)
        {
            if (queue.Count >= capacity)
                return false;

            queue.Enqueue(item);
            return true;
        }
    }

    public bool TryDequeue(out T item)
    {
        lock (sync)
        {
            if (queue.Count == 0)
            {
                item = default!;
                return false;
            }

            item = queue.Dequeue();
            return true;
        }
    }
}

internal sealed class DropOldestQueue<T>
{
    private readonly object sync = new object();
    private readonly Queue<T> queue = new Queue<T>();
    private readonly int capacity;

    public DropOldestQueue(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        this.capacity = capacity;
    }

    public void EnqueueDroppingOldest(T item)
    {
        lock (sync)
        {
            while (queue.Count >= capacity)
                queue.Dequeue();

            queue.Enqueue(item);
        }
    }

    public bool TryDequeue(out T item)
    {
        lock (sync)
        {
            if (queue.Count == 0)
            {
                item = default!;
                return false;
            }

            item = queue.Dequeue();
            return true;
        }
    }
}

internal enum ServerQueueEnqueueKind
{
    Enqueued,
    Dropped,
    DisconnectCurrent,
    DisconnectOther
}

internal readonly struct ServerQueueEnqueueResult
{
    public ServerQueueEnqueueKind Kind { get; }
    public Guid OffenderId { get; }

    public ServerQueueEnqueueResult(ServerQueueEnqueueKind kind, Guid offenderId = default)
    {
        Kind = kind;
        OffenderId = offenderId;
    }
}

internal sealed class FairServerMessageQueue
{
    private readonly struct OwnedMessage
    {
        public VibeNetMessage Message { get; }
        public bool Reliable { get; }

        public OwnedMessage(VibeNetMessage message, bool reliable)
        {
            Message = message;
            Reliable = reliable;
        }
    }

    private readonly object sync = new object();
    private readonly int totalCapacity;
    private readonly int perOwnerReliableCapacity;

    private readonly Dictionary<Guid, Queue<OwnedMessage>> queuesByOwner =
        new Dictionary<Guid, Queue<OwnedMessage>>();

    private readonly Dictionary<Guid, int> countsByOwner =
        new Dictionary<Guid, int>();

    private readonly Dictionary<Guid, int> reliableCountsByOwner =
        new Dictionary<Guid, int>();

    private readonly Queue<Guid> readyOwners = new Queue<Guid>();
    private readonly HashSet<Guid> scheduledOwners = new HashSet<Guid>();

    private int totalCount;

    public FairServerMessageQueue(int totalCapacity, int maxClients)
    {
        if (totalCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalCapacity));
        if (maxClients <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxClients));

        this.totalCapacity = totalCapacity;
        perOwnerReliableCapacity = Math.Max(1, totalCapacity / Math.Max(1, Math.Min(totalCapacity, maxClients)));
    }

    public ServerQueueEnqueueResult Enqueue(Guid ownerId, VibeNetMessage message, bool reliable)
    {
        lock (sync)
        {
            int ownerReliableCount =
                reliableCountsByOwner.TryGetValue(ownerId, out int count) ? count : 0;

            if (reliable && ownerReliableCount >= perOwnerReliableCapacity)
                return new ServerQueueEnqueueResult(ServerQueueEnqueueKind.DisconnectCurrent, ownerId);

            if (totalCount >= totalCapacity)
            {
                if (!reliable)
                    return new ServerQueueEnqueueResult(ServerQueueEnqueueKind.Dropped);

                if (TryDropOneUnreliableUnlocked())
                {
                    EnqueueUnlocked(ownerId, message, true);
                    return new ServerQueueEnqueueResult(ServerQueueEnqueueKind.Enqueued);
                }

                Guid offenderId = ownerReliableCount > 0 ? ownerId : FindLargestReliableOwner();
                if (offenderId == Guid.Empty)
                    return new ServerQueueEnqueueResult(ServerQueueEnqueueKind.DisconnectCurrent, ownerId);

                RemoveOwnerUnlocked(offenderId);

                if (offenderId == ownerId)
                    return new ServerQueueEnqueueResult(ServerQueueEnqueueKind.DisconnectCurrent, ownerId);

                EnqueueUnlocked(ownerId, message, true);
                return new ServerQueueEnqueueResult(ServerQueueEnqueueKind.DisconnectOther, offenderId);
            }

            EnqueueUnlocked(ownerId, message, reliable);
            return new ServerQueueEnqueueResult(ServerQueueEnqueueKind.Enqueued);
        }
    }

    public bool TryDequeue(out VibeNetMessage message)
    {
        lock (sync)
        {
            while (readyOwners.Count > 0)
            {
                Guid ownerId = readyOwners.Dequeue();
                scheduledOwners.Remove(ownerId);

                if (!queuesByOwner.TryGetValue(ownerId, out Queue<OwnedMessage>? queue) ||
                    queue.Count == 0)
                {
                    continue;
                }

                OwnedMessage ownedMessage = queue.Dequeue();
                message = ownedMessage.Message;
                totalCount--;

                if (ownedMessage.Reliable &&
                    reliableCountsByOwner.TryGetValue(ownerId, out int reliableCount))
                {
                    if (reliableCount <= 1)
                        reliableCountsByOwner.Remove(ownerId);
                    else
                        reliableCountsByOwner[ownerId] = reliableCount - 1;
                }

                int remainingOwnerCount = countsByOwner[ownerId] - 1;
                if (remainingOwnerCount <= 0)
                {
                    countsByOwner.Remove(ownerId);
                    queuesByOwner.Remove(ownerId);
                    reliableCountsByOwner.Remove(ownerId);
                }
                else
                {
                    countsByOwner[ownerId] = remainingOwnerCount;
                    readyOwners.Enqueue(ownerId);
                    scheduledOwners.Add(ownerId);
                }

                return true;
            }

            message = default!;
            return false;
        }
    }

    public void RemoveOwner(Guid ownerId)
    {
        lock (sync)
            RemoveOwnerUnlocked(ownerId);
    }

    private void EnqueueUnlocked(Guid ownerId, VibeNetMessage message, bool reliable)
    {
        if (!queuesByOwner.TryGetValue(ownerId, out Queue<OwnedMessage>? queue))
        {
            queue = new Queue<OwnedMessage>();
            queuesByOwner.Add(ownerId, queue);
        }

        queue.Enqueue(new OwnedMessage(message, reliable));
        totalCount++;
        countsByOwner[ownerId] = countsByOwner.TryGetValue(ownerId, out int count) ? count + 1 : 1;
        if (reliable)
        {
            reliableCountsByOwner[ownerId] =
                reliableCountsByOwner.TryGetValue(ownerId, out int reliableCount)
                ? reliableCount + 1
                : 1;
        }

        if (scheduledOwners.Add(ownerId))
            readyOwners.Enqueue(ownerId);
    }

    private Guid FindLargestReliableOwner()
    {
        Guid largestOwner = Guid.Empty;
        int largestCount = -1;

        foreach (KeyValuePair<Guid, int> pair in reliableCountsByOwner)
        {
            if (pair.Value > largestCount)
            {
                largestOwner = pair.Key;
                largestCount = pair.Value;
            }
        }

        return largestOwner;
    }

    private void RemoveOwnerUnlocked(Guid ownerId)
    {
        if (!countsByOwner.TryGetValue(ownerId, out int ownerCount))
            return;

        countsByOwner.Remove(ownerId);
        reliableCountsByOwner.Remove(ownerId);
        queuesByOwner.Remove(ownerId);
        scheduledOwners.Remove(ownerId);
        totalCount -= ownerCount;
        if (totalCount < 0)
            totalCount = 0;
    }

    private bool TryDropOneUnreliableUnlocked()
    {
        foreach (Guid ownerId in countsByOwner.Keys.ToArray())
        {
            if (!queuesByOwner.TryGetValue(ownerId, out Queue<OwnedMessage>? queue) ||
                queue.Count == 0)
            {
                continue;
            }

            Queue<OwnedMessage> rebuilt = new Queue<OwnedMessage>(queue.Count);
            bool dropped = false;

            while (queue.Count > 0)
            {
                OwnedMessage message = queue.Dequeue();
                if (!dropped && !message.Reliable)
                {
                    dropped = true;
                    totalCount--;
                    continue;
                }

                rebuilt.Enqueue(message);
            }

            if (!dropped)
            {
                queuesByOwner[ownerId] = rebuilt;
                continue;
            }

            if (rebuilt.Count == 0)
            {
                countsByOwner.Remove(ownerId);
                reliableCountsByOwner.Remove(ownerId);
                queuesByOwner.Remove(ownerId);
                scheduledOwners.Remove(ownerId);
            }
            else
            {
                countsByOwner[ownerId] = rebuilt.Count;
                queuesByOwner[ownerId] = rebuilt;
            }

            return true;
        }

        return false;
    }
}

internal static class VibeNetTime
{
    public static long Timestamp => Stopwatch.GetTimestamp();

    public static bool HasElapsed(long timestamp, TimeSpan duration)
    {
        long elapsed = Stopwatch.GetTimestamp() - timestamp;
        return elapsed >= ToStopwatchTicks(duration);
    }

    public static long ToStopwatchTicks(TimeSpan duration)
    {
        double ticks = duration.TotalSeconds * Stopwatch.Frequency;
        return (long)Math.Ceiling(ticks);
    }

    public static TimeSpan Remaining(long deadlineTimestamp)
    {
        long remaining = deadlineTimestamp - Stopwatch.GetTimestamp();
        if (remaining <= 0)
            return TimeSpan.Zero;

        double seconds = (double)remaining / Stopwatch.Frequency;
        return TimeSpan.FromSeconds(seconds);
    }
}

internal static class VibeNetTask
{
    public static async Task WaitAsync(Task task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            await task.ConfigureAwait(false);
            return;
        }

        Task completedTask = await Task.WhenAny(
            task,
            Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);

        if (!ReferenceEquals(completedTask, task))
            throw new OperationCanceledException(cancellationToken);

        await task.ConfigureAwait(false);
    }

    public static async Task<T> WaitAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
            return await task.ConfigureAwait(false);

        Task completedTask = await Task.WhenAny(
            task,
            Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);

        if (!ReferenceEquals(completedTask, task))
            throw new OperationCanceledException(cancellationToken);

        return await task.ConfigureAwait(false);
    }

    public static void Observe(Task task)
    {
        task.ContinueWith(
            observedTask =>
            {
                _ = observedTask.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

internal static class VibeNetSocket
{
    public static async Task<TcpClient> AcceptTcpClientAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

        try
        {
            return await VibeNetTask.WaitAsync(acceptTask, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            VibeNetTask.Observe(acceptTask);
            throw;
        }
    }

    public static async Task ConnectAsync(
        TcpClient client,
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        Task connectTask = client.ConnectAsync(address, port);

        try
        {
            await VibeNetTask.WaitAsync(connectTask, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            VibeNetTask.Observe(connectTask);
            try
            {
                client.Close();
            }
            catch
            {
            }

            throw;
        }
    }

    public static async Task<IPAddress[]> GetHostAddressesAsync(
        string host,
        CancellationToken cancellationToken)
    {
        Task<IPAddress[]> lookupTask = Dns.GetHostAddressesAsync(host);

        try
        {
            return await VibeNetTask.WaitAsync(lookupTask, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            VibeNetTask.Observe(lookupTask);
            throw;
        }
    }

    public static async Task<UdpReceiveResult> ReceiveAsync(
        UdpClient udp,
        CancellationToken cancellationToken)
    {
        Task<UdpReceiveResult> receiveTask = udp.ReceiveAsync();

        try
        {
            return await VibeNetTask.WaitAsync(receiveTask, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            VibeNetTask.Observe(receiveTask);
            throw;
        }
    }

    public static async Task<int> SendAsync(
        UdpClient udp,
        byte[] datagram,
        IPEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<int> sendTask = udp.SendAsync(datagram, datagram.Length, endpoint);

        try
        {
            return await VibeNetTask.WaitAsync(sendTask, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            VibeNetTask.Observe(sendTask);
            throw;
        }
    }

    public static async Task<int> SendConnectedAsync(
        UdpClient udp,
        byte[] datagram,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<int> sendTask = udp.SendAsync(datagram, datagram.Length);

        try
        {
            return await VibeNetTask.WaitAsync(sendTask, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            VibeNetTask.Observe(sendTask);
            throw;
        }
    }
}

internal static class VibeNetDefaults
{
    public static readonly TimeSpan ControlFrameTimeout = TimeSpan.FromSeconds(2);
}

internal static class VibeNetCancellation
{
    public static CancellationTokenSource CreateTimeoutSource(TimeSpan timeout)
    {
        CancellationTokenSource source = new CancellationTokenSource();
        source.CancelAfter(timeout);
        return source;
    }

    public static CancellationTokenSource CreateLinkedTimeoutSource(TimeSpan timeout, CancellationToken outer)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(outer);
        source.CancelAfter(timeout);
        return source;
    }
}
}
