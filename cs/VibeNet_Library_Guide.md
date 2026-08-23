# VibeNet Library Guide

This document is the standalone guide for the `VibeNet` library.

`VibeNet` is a minimal C# networking library. It exposes a small raw-byte API while internally handling:

- TCP connection establishment
- mandatory UDP registration before a connection becomes valid
- framed binary protocol headers
- connection lifecycle and graceful disconnect
- TCP heartbeat and timeout detection
- bounded message, failure, and event queues

The intended mental model is:

- TCP is the reliable application transport.
- UDP is required to validate the logical connection, then remains available for best-effort payloads.
- The application only sends and receives raw `byte[]`.
- The server is authoritative.
- The public API is poll-based, not callback-based.

# 1. Straightforward Example Program

The example below shows one server and one client in the same process. It demonstrates:

- creating configuration
- starting server and client
- waiting for the authoritative server-side connected event
- sending both TCP and UDP payloads
- receiving messages on both ends
- polling disconnect and failure queues
- targeted server send and server broadcast
- graceful shutdown

```csharp
using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using VibeNet;

internal static class Program
{
    private static async Task Main()
    {
        VibeNetConfiguration config = new VibeNetConfiguration(
            clientConnectTimeout: TimeSpan.FromSeconds(5),
            udpHandshakeInterval: TimeSpan.FromMilliseconds(250),
            udpHandshakeTimeout: TimeSpan.FromSeconds(2),
            serverConnectTimeout: TimeSpan.FromSeconds(5),
            tcpHeartbeatInterval: TimeSpan.FromSeconds(2),
            tcpHeartbeatTimeout: TimeSpan.FromSeconds(10),
            maxTcpPayloadBytes: 1024 * 1024,
            maxUdpPayloadBytes: 1200,
            maxQueuedMessages: 256,
            maxQueuedFailures: 64,
            maxQueuedEvents: 64,
            addressMode: VibeNetAddressMode.IPv4);

        VibeNetServer server = new VibeNetServer(
            tcpPort: 7777,
            udpPort: 7777,
            maxClients: 8,
            bindAddress: IPAddress.Loopback,
            configuration: config);

        VibeNetClient client = new VibeNetClient(
            remoteHost: "127.0.0.1",
            tcpPort: 7777,
            udpPort: 7777,
            configuration: config);

        try
        {
            // Server start only means the listener is live.
            VibeNetConnectResult serverStart = await server.StartAsync();
            if (!serverStart.Success)
            {
                Console.WriteLine("Server failed: " + serverStart.Failure?.Code + " - " + serverStart.Failure?.Message);
                return;
            }

            // Client start means the full logical connection completed:
            // TCP connected, HELLO received, UDP registered, READY sent, READY_ACK received.
            VibeNetConnectResult clientStart = await client.StartAsync();
            if (!clientStart.Success)
            {
                Console.WriteLine("Client failed: " + clientStart.Failure?.Code + " - " + clientStart.Failure?.Message);
                return;
            }

            // The server is authoritative, so wait for its connection event.
            VibeNetConnectionInfo serverSideConnection =
                await WaitForServerConnectionAsync(server);

            Console.WriteLine("Client connected.");
            Console.WriteLine("Server connection id: " + serverSideConnection.Id);
            Console.WriteLine("Client sees server at: " + client.RemoteAddress);
            Console.WriteLine("Server sees client at: " + serverSideConnection.RemoteAddress);

            // Application payloads are raw bytes of your own choosing.
            // VibeNet handles its own framing internally.
            byte[] clientTcp = Encoding.UTF8.GetBytes("client over tcp");
            byte[] clientUdp = Encoding.UTF8.GetBytes("client over udp");

            if (!await client.SendAsync(clientTcp, VibeNetTransport.TCP))
                Console.WriteLine("Client TCP send failed.");

            if (!await client.SendAsync(clientUdp, VibeNetTransport.UDP))
                Console.WriteLine("Client UDP send failed.");

            DrainServer(server);

            // Server can target a single client by authoritative connection id.
            await server.SendAsync(
                serverSideConnection.Id,
                Encoding.UTF8.GetBytes("server targeted tcp"),
                VibeNetTransport.TCP);

            await server.SendAsync(
                serverSideConnection.Id,
                Encoding.UTF8.GetBytes("server targeted udp"),
                VibeNetTransport.UDP);

            // Broadcast is also available. With one client this still reaches that same client.
            await server.BroadcastAsync(
                Encoding.UTF8.GetBytes("server broadcast tcp"),
                VibeNetTransport.TCP);

            DrainClient(client);

            // At any point, poll for background failures.
            DrainFailures("server", server);
            DrainFailures("client", client);

            // Graceful client disconnect.
            await client.DisconnectAsync();
            DrainServer(server);
            DrainClient(client);
        }
        finally
        {
            await SafeDisconnectAsync(client);
            await SafeStopAsync(server);

            client.Dispose();
            server.Dispose();
        }
    }

    private static async Task<VibeNetConnectionInfo> WaitForServerConnectionAsync(VibeNetServer server)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (server.TryDequeueConnected(out VibeNetConnectionInfo connection))
                return connection;

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for server-side connection event.");
    }

    private static void DrainServer(VibeNetServer server)
    {
        while (server.TryDequeueMessage(out VibeNetMessage message))
        {
            Console.WriteLine(
                "Server received " +
                message.Transport +
                " from " +
                message.ConnectionId +
                ": " +
                Encoding.UTF8.GetString(message.Data));
        }

        while (server.TryDequeueDisconnected(out VibeNetDisconnectInfo disconnect))
        {
            Console.WriteLine(
                "Server saw disconnect: " +
                disconnect.Connection.Id +
                " -> " +
                disconnect.Reason +
                " (" +
                disconnect.Detail +
                ")");
        }
    }

    private static void DrainClient(VibeNetClient client)
    {
        while (client.TryDequeueMessage(out VibeNetMessage message))
        {
            Console.WriteLine(
                "Client received " +
                message.Transport +
                ": " +
                Encoding.UTF8.GetString(message.Data));
        }

        while (client.TryDequeueDisconnected(out VibeNetDisconnectInfo disconnect))
        {
            Console.WriteLine(
                "Client saw disconnect: " +
                disconnect.Reason +
                " (" +
                disconnect.Detail +
                ")");
        }
    }

    private static void DrainFailures(string label, VibeNetNode node)
    {
        while (node.TryDequeueFailure(out VibeNetFailure failure))
        {
            Console.WriteLine(
                label +
                " failure: " +
                failure.Code +
                " during " +
                failure.Operation +
                " - " +
                failure.Message);
        }
    }

    private static async Task SafeDisconnectAsync(VibeNetClient client)
    {
        try
        {
            await client.DisconnectAsync();
        }
        catch
        {
        }
    }

    private static async Task SafeStopAsync(VibeNetServer server)
    {
        try
        {
            await server.StopAsync();
        }
        catch
        {
        }
    }
}
```

### Normal application pattern

In a real application, the usual structure is:

1. Create one immutable `VibeNetConfiguration`.
2. Create one `VibeNetServer` or `VibeNetClient`.
3. Call `StartAsync()`.
4. In your update loop, repeatedly drain:
   - `TryDequeueMessage(...)`
   - `TryDequeueConnected(...)` on server
   - `TryDequeueDisconnected(...)`
   - `TryDequeueFailure(...)`
5. Serialize and deserialize your own application protocol into raw `byte[]`.
6. On shutdown, call `DisconnectAsync()` for the client or `StopAsync()` for the server.
7. If you need to reconnect or restart, create a brand new instance. Instances are single-use.

# 2. Complete Public API Reference

This section covers every public type and member that application code is expected to use.

## 2.1 Transport and lifecycle enums

### `VibeNetTransport`

```csharp
bool tcpSent = await client.SendAsync(data, VibeNetTransport.TCP);
bool udpSent = await client.SendAsync(data, VibeNetTransport.UDP);
```

Selects the application transport for one outbound payload. `TCP` is reliable and ordered. `UDP` is best-effort and connectionless after registration.

### `VibeNetAddressMode`

```csharp
VibeNetConfiguration config =
    new VibeNetConfiguration(addressMode: VibeNetAddressMode.DualStack);
```

Controls whether sockets and address filtering use `IPv4`, `IPv6`, or `DualStack`.

### `VibeNetConnectionState`

```csharp
if (client.State == VibeNetConnectionState.Connected)
{
    // Safe to send.
}
```

Represents the public lifecycle state of a node or connection snapshot:

- `Created`
- `Connecting`
- `Connected`
- `Disconnecting`
- `Disconnected`
- `Faulted`

### `VibeNetFailureCode`

```csharp
VibeNetConnectResult result = await client.StartAsync();
if (!result.Success && result.Failure.HasValue)
{
    Console.WriteLine(result.Failure.Value.Code);
}
```

Classifies operational networking failures. Important cases include:

- `DnsResolutionFailed`
- `ConnectionRefused`
- `ServerRejected`
- `ProtocolMismatch`
- `HandshakeFailed`
- `Timeout`
- `SendFailure`
- `ReceiveFailure`
- `QueueOverflow`
- `InternalError`

### `VibeNetDisconnectReason`

```csharp
while (client.TryDequeueDisconnected(out VibeNetDisconnectInfo info))
{
    Console.WriteLine(info.Reason);
}
```

Explains why an established connection ended:

- `LocalRequested`
- `RemoteRequested`
- `Timeout`
- `ConnectionLost`
- `ProtocolError`
- `ResourceLimit`
- `ServerStopped`

### `VibeNetOperation`

```csharp
while (server.TryDequeueFailure(out VibeNetFailure failure))
{
    Console.WriteLine(failure.Operation);
}
```

Explains where a failure happened. Common values include:

- `Start`
- `Resolve`
- `Accept`
- `TcpConnect`
- `HelloHandshake`
- `UdpHandshake`
- `ReadyHandshake`
- `TcpSend`
- `UdpSend`
- `TcpReceive`
- `UdpReceive`
- `Heartbeat`
- `Disconnect`
- `Shutdown`

## 2.2 Result and data structures

### `VibeNetConnectResult`

```csharp
VibeNetConnectResult result = await client.StartAsync();
if (!result.Success && result.Failure.HasValue)
{
    Console.WriteLine(result.Failure.Value.Code);
    Console.WriteLine(result.Failure.Value.Operation);
    Console.WriteLine(result.Failure.Value.Message);
}
```

Returned by `StartAsync()`. `Success` tells whether startup completed. `Failure` contains the unified operational failure object when startup does not succeed.

The static helpers `VibeNetConnectResult.Succeeded()` and `VibeNetConnectResult.Failed(...)` are public, but they are mainly for library construction of results rather than normal application use.

### `VibeNetMessage`

```csharp
while (server.TryDequeueMessage(out VibeNetMessage message))
{
    Guid? source = message.ConnectionId;
    VibeNetTransport transport = message.Transport;
    byte[] payload = message.Data;
}
```

Represents one application payload received by the library.

- On the server, `ConnectionId` is the sending client.
- On the client, `ConnectionId` is `null` because the sender is always the server.

The constructor is public, but in normal usage application code consumes `VibeNetMessage` values produced by the library rather than constructing its own.

### `VibeNetConnectionInfo`

```csharp
VibeNetConnectionInfo snapshot = client.Connection;
Console.WriteLine(snapshot.RemoteAddress);
Console.WriteLine(snapshot.TCPRemotePort);
Console.WriteLine(snapshot.UDPRemotePort);
```

Represents a connection snapshot visible to application code. It exposes:

- connection `Id`
- `RemoteAddress`
- TCP and UDP remote ports
- public connection `State`
- `TCPConnectedAtUtc`
- final `ConnectedAtUtc`
- `LastActivityUtc`

The constructor is public, but the common pattern is to read snapshots returned by the library instead of building them manually.

### `VibeNetDisconnectInfo`

```csharp
if (server.TryDequeueDisconnected(out VibeNetDisconnectInfo disconnect))
{
    Console.WriteLine(disconnect.Connection.Id);
    Console.WriteLine(disconnect.Reason);
    Console.WriteLine(disconnect.Detail);
}
```

Represents one disconnection event with a final connection snapshot, reason, text detail, and timestamp.

The constructor is public, but application code normally consumes values emitted by the library.

### `VibeNetFailure`

```csharp
if (client.TryDequeueFailure(out VibeNetFailure failure))
{
    Console.WriteLine(failure.ConnectionId);
    Console.WriteLine(failure.Transport);
    Console.WriteLine(failure.Code);
    Console.WriteLine(failure.Operation);
    Console.WriteLine(failure.Message);
}
```

Represents one operational networking failure. The same type is used for:

- `StartAsync()` failures through `VibeNetConnectResult`
- background failures through `TryDequeueFailure(...)`

This keeps failure interpretation consistent even when delivery differs.

## 2.3 `VibeNetConfiguration`

### Constructor

```csharp
VibeNetConfiguration config = new VibeNetConfiguration(
    clientConnectTimeout: TimeSpan.FromSeconds(5),
    udpHandshakeInterval: TimeSpan.FromMilliseconds(250),
    udpHandshakeTimeout: TimeSpan.FromSeconds(2),
    serverConnectTimeout: TimeSpan.FromSeconds(5),
    tcpHeartbeatInterval: TimeSpan.FromSeconds(2),
    tcpHeartbeatTimeout: TimeSpan.FromSeconds(10),
    maxTcpPayloadBytes: 1024 * 1024,
    maxUdpPayloadBytes: 1200,
    maxQueuedMessages: 256,
    maxQueuedFailures: 64,
    maxQueuedEvents: 64,
    addressMode: VibeNetAddressMode.IPv4);
```

The configuration object is immutable and shared by a node for its entire lifetime.

### Timing properties

```csharp
TimeSpan tcpConnectBudget = config.ClientConnectTimeout;
TimeSpan heartbeatInterval = config.TCPHeartbeatInterval;
```

- `ClientConnectTimeout`: total client startup budget
- `UDPHandshakeInterval`: how often the client retransmits UDP registration
- `UDPHandshakeTimeout`: maximum duration for UDP registration acknowledgement
- `ServerConnectTimeout`: how long a pending server-side handshake may remain incomplete
- `TCPHeartbeatInterval`: how often heartbeat processing runs
- `TCPHeartbeatTimeout`: maximum quiet time before TCP liveness fails

### Size and queue properties

```csharp
int maxReliableBytes = config.MaxTCPPayloadBytes;
int maxDatagramBytes = config.MaxUDPPayloadBytes;
```

- `MaxTCPPayloadBytes`: largest allowed application TCP payload
- `MaxUDPPayloadBytes`: largest allowed application UDP payload
- `MaxQueuedMessages`: capacity for received application messages
- `MaxQueuedFailures`: capacity for failure reporting
- `MaxQueuedEvents`: capacity for connection/disconnection event queues

### Address mode

```csharp
VibeNetAddressMode mode = config.AddressMode;
```

Defines how sockets are created and which resolved addresses are accepted.

## 2.4 `VibeNetNode`

This is the common abstract base class for both server and client.

### Common properties

```csharp
int tcpPort = node.TCPPort;
int udpPort = node.UDPPort;
VibeNetConfiguration config = node.Configuration;
```

Exposes the node’s configured ports and immutable configuration.

### `TryDequeueFailure`

```csharp
while (node.TryDequeueFailure(out VibeNetFailure failure))
{
    Console.WriteLine(failure.Message);
}
```

Polls one background operational failure at a time. This is the primary way to observe asynchronous networking problems that happen when no awaited public method is currently returning.

### `TryDequeueMessage`

```csharp
while (node.TryDequeueMessage(out VibeNetMessage message))
{
    // Deserialize your own byte[] payload here.
}
```

Polls one received application message at a time.

### `StartAsync`

```csharp
VibeNetConnectResult result = await node.StartAsync();
```

Starts the node. For a server, success means listening started. For a client, success means the full logical connection is established.

### `Dispose`

```csharp
node.Dispose();
```

Immediately releases local resources. Use `DisconnectAsync()` or `StopAsync()` first when you want graceful shutdown semantics.

## 2.5 `VibeNetServer`

### Constructor

```csharp
VibeNetServer server = new VibeNetServer(
    tcpPort: 7777,
    udpPort: 7777,
    maxClients: 32,
    bindAddress: IPAddress.Any,
    configuration: config);
```

Creates the authoritative server. `udpPort` defaults to `tcpPort`.

### Server properties

```csharp
bool running = server.IsRunning;
int connected = server.ConnectedClientCount;
int pending = server.PendingClientCount;
IReadOnlyList<VibeNetConnectionInfo> connections = server.Connections;
```

- `MaxClients`: connection capacity
- `BindAddress`: local bind address
- `IsRunning`: whether the listener is active
- `ConnectedClientCount`: number of fully connected clients
- `PendingClientCount`: number of still-handshaking clients
- `Connections`: snapshot of all tracked sessions

### `StartAsync`

```csharp
VibeNetConnectResult result = await server.StartAsync();
```

Starts the TCP listener, UDP socket, accept loop, UDP loop, and heartbeat loop.

### `TryDequeueConnected`

```csharp
if (server.TryDequeueConnected(out VibeNetConnectionInfo connection))
{
    Console.WriteLine(connection.Id);
}
```

Polls a newly connected client event. This is the authoritative moment a client becomes usable on the server.

### `TryDequeueDisconnected`

```csharp
if (server.TryDequeueDisconnected(out VibeNetDisconnectInfo disconnect))
{
    Console.WriteLine(disconnect.Reason);
}
```

Polls a client disconnection event.

### `TryGetConnection`

```csharp
if (server.TryGetConnection(connectionId, out VibeNetConnectionInfo connection))
{
    Console.WriteLine(connection.RemoteAddress);
}
```

Looks up one current connection snapshot by authoritative server connection id.

### `SendAsync`

```csharp
bool sent = await server.SendAsync(
    connectionId,
    data,
    VibeNetTransport.TCP);
```

Sends one raw application payload to one connected client. Returns `false` if the target is no longer valid or if UDP has no registered endpoint.

### `BroadcastAsync`

```csharp
int recipients = await server.BroadcastAsync(
    data,
    VibeNetTransport.UDP);
```

Sends one payload to every currently connected client and returns how many sends succeeded.

### `DisconnectClientAsync`

```csharp
bool disconnected = await server.DisconnectClientAsync(connectionId);
```

Requests graceful disconnection of one client. Returns `false` if that connection id is no longer tracked.

### `StopAsync`

```csharp
await server.StopAsync();
```

Gracefully stops the server, notifies connected clients when possible, and waits for shutdown completion.

### `Dispose`

```csharp
server.Dispose();
```

Immediately tears down local resources. Use `StopAsync()` first if you want graceful remote notification.

## 2.6 `VibeNetClient`

### Constructor

```csharp
VibeNetClient client = new VibeNetClient(
    remoteHost: "127.0.0.1",
    tcpPort: 7777,
    udpPort: 7777,
    configuration: config);
```

Creates a client that will connect to exactly one server endpoint. `udpPort` defaults to `tcpPort`.

### Client properties

```csharp
string host = client.RemoteHost;
Guid id = client.ConnectionId;
IPAddress? address = client.RemoteAddress;
bool connected = client.IsConnected;
VibeNetConnectionState state = client.State;
VibeNetDisconnectReason? reason = client.LastDisconnectReason;
VibeNetConnectionInfo snapshot = client.Connection;
```

- `RemoteHost`: the configured host string
- `ConnectionId`: authoritative id assigned by the server after `Hello`
- `RemoteAddress`: normalized resolved address currently in use; usually `null` before connection completes
- `State`: public lifecycle state
- `IsConnected`: convenience check for `State == Connected`
- `LastDisconnectReason`: final reason for the last established connection
- `Connection`: latest connection snapshot

### `StartAsync`

```csharp
VibeNetConnectResult result = await client.StartAsync();
```

Attempts full connection establishment. Success means the client is fully connected and both transports are ready for application use.

Caller cancellation throws `OperationCanceledException`. Operational startup failure returns `Success == false` with a populated `Failure`.

### `SendAsync`

```csharp
bool sentTcp = await client.SendAsync(payload, VibeNetTransport.TCP);
bool sentUdp = await client.SendAsync(payload, VibeNetTransport.UDP);
```

Sends raw application bytes to the server over the selected transport.

- returns `true` when the send completed
- returns `false` for operational send failure
- throws for programmer misuse such as sending while disconnected
- throws `OperationCanceledException` when the caller cancels the awaited send

### `TryDequeueDisconnected`

```csharp
if (client.TryDequeueDisconnected(out VibeNetDisconnectInfo disconnect))
{
    Console.WriteLine(disconnect.Detail);
}
```

Polls one disconnect event for the client’s connection to the server.

### `DisconnectAsync`

```csharp
await client.DisconnectAsync();
```

Requests graceful client disconnect, waits for local shutdown to finish, and publishes a disconnect event if the connection had been established.

### `Dispose`

```csharp
client.Dispose();
```

Immediately releases local resources. Use `DisconnectAsync()` first if graceful disconnect is desired.

## 2.7 Public lifecycle rules worth knowing

### Instances are single-use

```csharp
VibeNetClient client = new VibeNetClient("127.0.0.1");
await client.StartAsync();

// Create a new instance instead of calling StartAsync again later.
```

`VibeNetServer` and `VibeNetClient` are intentionally single-use. Once `StartAsync()` has been attempted, that instance is not meant to be started again.

### Payloads are raw bytes

```csharp
byte[] payload = MySerializer.Write(message);
bool sent = await client.SendAsync(payload, VibeNetTransport.TCP);
```

The application owns all payload serialization. VibeNet only transports bytes.

### Treat outbound buffers as immutable until the awaited operation finishes

```csharp
byte[] payload = BuildPayload();
await client.SendAsync(payload, VibeNetTransport.TCP);
```

Do not modify or reuse a shared buffer until the awaited send completes. The library may still be writing from it.

# 3. Technical Architecture Review

This section provides a compact but complete technical breakdown of the library architecture and behavior.

## 3.1 Architectural goal

`VibeNet` is designed to be a small standalone C# networking library with a minimal surface API and a more opinionated internal architecture.

Primary goals:

- application code only deals with raw `byte[]`
- TCP is the reliable application channel
- UDP is mandatory before the connection is considered valid
- the server is authoritative
- the public concurrency boundary is queue polling, not callbacks
- the library should remain usable from ordinary C# application code

### Public design characteristics

Several design choices are intentional and should be treated as part of the library’s identity:

- the public namespace is `VibeNet`
- the common base type is `VibeNetNode`
- startup result naming uses `Connect` terminology
- timing configuration is explicit:
  - `ClientConnectTimeout`
  - `UDPHandshakeInterval`
  - `UDPHandshakeTimeout`
  - `ServerConnectTimeout`
  - `TCPHeartbeatInterval`
  - `TCPHeartbeatTimeout`
- protocol helpers, frame types, and queue helpers are internal implementation details rather than application API
- the server queue policy prefers dropping queued UDP pressure before disconnecting the sender that exceeded reliable limits

Secondary goals:

- preserve visibility into important network facts such as IP, ports, and disconnect reasons
- keep configuration explicit and immutable
- keep internals hidden unless they are truly part of the public contract

## 3.2 One logical connection always requires TCP and UDP

VibeNet does not treat TCP alone as a complete connection.

The logical connection only becomes valid when all of the following happen:

1. TCP socket connects.
2. Server sends `Hello` with authoritative connection id and session token.
3. Client opens UDP and sends `UdpRegister`.
4. Server validates the session token and replies with `UdpAck`.
5. Client sends `Ready` over TCP.
6. Server replies with `ReadyAck`.
7. Both sides transition to `Connected`.

Consequences:

- application code cannot send before `client.StartAsync()` succeeds
- a server-side session can exist in `Connecting` state while waiting for UDP registration
- a client that never completes UDP registration never becomes a valid logical connection

## 3.3 Authoritative server model

The server owns connection identity.

- The server creates the `Guid` connection id.
- The client does not invent or negotiate its own id.
- On the server, every received `VibeNetMessage` includes the authoritative `ConnectionId`.
- On the client, received `VibeNetMessage.ConnectionId` is always `null` because there is only one remote server.

This keeps routing and identity simple:

- server application code chooses where messages go
- client code never needs peer-to-peer routing

## 3.4 Connection establishment state machine

### Server side

Server startup only means listeners are active. Per-client handshake then proceeds like this:

1. Accept TCP socket.
2. Reserve a client slot.
3. Create `ServerSession` with new id, new 32-byte session token, and handshake timeout.
4. Send `Hello`.
5. Wait for UDP registration and `Ready`.
6. Validate `Ready` only after UDP registration succeeded.
7. Send `ReadyAck`.
8. Transition session to `Connected`.
9. Publish `TryDequeueConnected(...)` event.

### Client side

Client startup is a full connection attempt:

1. Resolve host into allowed addresses.
2. Try TCP connect against filtered addresses.
3. Read server `Hello`.
4. Extract authoritative `ConnectionId` and session token.
5. Create UDP socket using the connected address family.
6. Perform UDP registration loop.
7. Send `Ready`.
8. Wait for `ReadyAck`.
9. Transition client state to `Connected`.
10. Start background TCP receive, UDP receive, and heartbeat tasks.

### Session token purpose

The session token binds UDP registration to the TCP handshake that created the pending connection. This prevents arbitrary UDP traffic from claiming a pending session by id alone.

## 3.5 UDP registration reliability

UDP registration is intentionally retried by the client.

- The client sends `UdpRegister` immediately.
- It waits for `UdpAck`.
- If no ack arrives, it retransmits every `UDPHandshakeInterval`.
- The entire registration phase is bounded by `UDPHandshakeTimeout`.

The server does not depend on the client’s source UDP port being pre-known. Instead, it learns that UDP endpoint from the validated registration datagram.

## 3.6 Final READY handshake

The final TCP `Ready` and `ReadyAck` stage exists so that the server does not declare the connection complete merely because UDP was registered.

This extra phase guarantees:

- the TCP control channel is still alive after UDP registration
- both sides know that both transports are ready
- application payloads cannot overtake final handshake completion

## 3.7 Pending-handshake management and capacity

The server enforces `MaxClients` across both pending and connected sessions.

Important behaviors:

- if capacity is already full, new TCP accepts are rejected with a `Reject` frame
- pending sessions are not allowed to exist forever; each has a `ServerConnectTimeout`
- a pending connection occupies a slot until it completes or times out

This means capacity pressure is intentionally conservative. A half-open handshaking client still counts.

## 3.8 Binary wire protocol

All application and control traffic is wrapped in the internal VibeNet frame format.

Header layout:

- bytes `0-3`: magic `"VIBE"` as `0x56494245`
- bytes `4-5`: protocol version, currently `1`
- byte `6`: packet type
- byte `7`: reserved, must be zero
- bytes `8-11`: payload length as unsigned big-endian 32-bit integer

Header size is always 12 bytes.

Rules:

- TCP uses framed stream reads and writes
- UDP uses one frame per datagram
- payload length is checked against the allowed maximum for the current context
- magic/version mismatches are protocol errors

## 3.9 Internal packet types

These are internal details, not public API, but they define the protocol:

- `Hello`
- `Reject`
- `UdpRegister`
- `UdpAck`
- `Ready`
- `ReadyAck`
- `Data`
- `Ping`
- `Pong`
- `Disconnect`

Purpose of each:

- `Hello`: server assigns authoritative id and session token
- `Reject`: server declines a new TCP connection, usually due to capacity
- `UdpRegister`: client proves UDP reachability for the pending session
- `UdpAck`: server confirms UDP registration
- `Ready`: client confirms TCP is still live after UDP registration
- `ReadyAck`: server finalizes the logical connection
- `Data`: carries raw application bytes
- `Ping` and `Pong`: maintain TCP liveness
- `Disconnect`: graceful closure signal with a one-byte disconnect code

## 3.10 UUID interoperability

Connection ids are serialized as 16 raw bytes derived from the canonical 32-digit `Guid` hex form.

This is deliberate:

- the wire format is stable and language-agnostic
- no .NET-specific mixed-endian `Guid.ToByteArray()` format is used
- other languages can reproduce the same identifier bytes by using the 32-digit hex string representation

## 3.11 Application-message identity and authority

Application payload routing is intentionally simple.

Server receiving TCP:

- TCP application frames are accepted only from fully connected sessions
- received messages are queued as `VibeNetMessage(connectionId, TCP, payload)`

Server receiving UDP:

- UDP datagrams are accepted only from the registered UDP endpoint of a connected session
- received messages are queued as `VibeNetMessage(connectionId, UDP, payload)`

Client receiving:

- all server payloads are queued as `VibeNetMessage(null, transport, payload)`

There is no built-in request/response layer, channels, RPC system, or schema system.

## 3.12 No automatic message routing

VibeNet deliberately avoids application-level semantics.

It does not provide:

- message ids
- acknowledgements at the application layer
- matchmaking
- rooms
- replication
- serialization
- compression
- encryption

Those belong above the transport layer and should be built by the consumer if needed.

## 3.13 TCP behavior

TCP is treated as the reliable, ordered application transport.

Key behaviors:

- send uses one shared stream per connection
- all TCP frames are serialized under a per-connection send lock
- TCP application payloads larger than `MaxTCPPayloadBytes` are rejected before send
- if a TCP send or receive fails after connection establishment, the logical connection is usually considered lost

Because TCP is the authoritative control path, handshake, heartbeat, and disconnect signaling all run over TCP.

## 3.14 UDP behavior

UDP is best-effort after registration.

Key behaviors:

- no application-level retransmission is performed for regular UDP payloads
- unknown UDP traffic is ignored
- UDP send failure alone does not prove the logical connection is dead
- UDP application payloads larger than `MaxUDPPayloadBytes` are rejected before send

The client and server both use one UDP socket per node, not one UDP socket per peer.

## 3.15 Heartbeat and connection-health model

Heartbeat is TCP-based.

Server:

- periodically checks each connected session
- if the last TCP activity exceeds `TCPHeartbeatTimeout`, disconnects the session with `Timeout`
- otherwise sends `Ping`

Client:

- replies to `Ping` with `Pong`
- independently tracks TCP quiet time
- if the server has been silent for longer than `TCPHeartbeatTimeout`, begins shutdown with `Timeout`

Only TCP activity resets the TCP heartbeat clock. UDP traffic alone does not prove the control connection is healthy.

## 3.16 Queue-based public concurrency boundary

The public API is intentionally poll-driven.

The library owns background tasks and threads of control. Application code observes them through queues:

- message queue
- failure queue
- server connected-event queue
- disconnect-event queue

This avoids public event callbacks and keeps application integration predictable in game loops, service loops, and manual polling environments.

## 3.17 Receive-queue backpressure policy

The library uses bounded queues. Backpressure behavior is transport-aware.

### Client message queue

The client uses a simple bounded message queue. If the queue is full:

- incoming TCP application data causes shutdown with `ResourceLimit`
- incoming UDP application data is dropped and a `QueueOverflow` error is reported

### Server message queue

The server uses a bounded per-message queue with simple per-sender reliable accounting.

Its policy is:

- keep per-connection ownership so queued messages can be removed when a client disconnects
- preserve simple FIFO dequeue order
- track reliable TCP occupancy per owner
- drop incoming UDP first when the shared queue is full
- disconnect only the current reliable sender when reliable pressure exceeds local limits or cannot fit into the remaining shared queue

This keeps the queue logic small and predictable while still preventing best-effort UDP pressure from knocking out an unrelated client.

### Error and event queues

Failure and event queues drop the oldest item when full. They are observability channels, not connection-fatal paths.

## 3.18 Payload limits

There are two different payload ceilings:

- application limits from `VibeNetConfiguration`
- internal protocol control limits

Important constants:

- control payloads are capped internally at 1024 bytes
- UDP user payloads are additionally capped by the practical maximum datagram size minus the 12-byte header

Application code should still keep UDP payloads small. The library does not fragment large user-level messages.

## 3.19 Addressing, DNS, and ports

### Ports

Each node has:

- `TCPPort`
- `UDPPort`

If `udpPort` is omitted, it defaults to the TCP port.

### Server bind address

The server accepts an optional bind address.

- if omitted in IPv4 mode, it binds to `IPAddress.Any`
- if omitted in IPv6 or DualStack mode, it binds to `IPAddress.IPv6Any`
- bind address family must be compatible with the selected `AddressMode`

### Client host resolution

The client accepts either:

- a literal IP string
- a DNS host name

Resolved addresses are filtered by `AddressMode`.

For display, IPv4-mapped IPv6 addresses are normalized to IPv4 in public snapshots.

## 3.20 Client and server lifecycle

### Server

Server lifecycle states and actions:

- create `VibeNetServer`
- call `StartAsync()`
- accept pending sessions and fully connected sessions
- send to one client or broadcast
- call `StopAsync()` for graceful shutdown
- call `Dispose()` for immediate local cleanup

### Client

Client lifecycle states and actions:

- create `VibeNetClient`
- call `StartAsync()`
- send TCP or UDP payloads
- poll messages/failures/disconnects
- call `DisconnectAsync()` for graceful shutdown
- call `Dispose()` for immediate local cleanup

### Single-use instances

Both node types are single-use by design. Reconnect or restart means creating a brand new object.

## 3.21 Cancellation model

Cancellation is explicit but not the main public coordination mechanism.

Important behaviors:

- `StartAsync`, `SendAsync`, `BroadcastAsync`, `DisconnectAsync`, and `StopAsync` accept cancellation tokens
- client startup is bounded by `ClientConnectTimeout`
- server-side pending sessions are bounded by `ServerConnectTimeout`
- UDP registration is bounded by `UDPHandshakeTimeout`
- short internal control-frame deadlines are also used during shutdown and registration acknowledgement

The public expectation should be:

- cancellation stops waiting for the operation
- background cleanup still proceeds
- application code should still poll failures and disconnect queues

## 3.22 Error model

There are three distinct public outcomes:

1. programmer misuse throws normal C# exceptions
2. caller cancellation throws `OperationCanceledException`
3. operational networking failure uses `VibeNetFailure`

Examples:

- invalid usage such as sending while disconnected throws immediately
- `StartAsync()` returns `VibeNetConnectResult`, whose `Failure` is a `VibeNetFailure`
- background socket, protocol, send, receive, and queue issues are reported through `TryDequeueFailure(...)`
- `client.SendAsync(...)` and `server.SendAsync(...)` return transport success as `bool`, while the associated operational failure still enters the failure queue

Disconnect events are separate from failures. A connection can end for a clean reason without producing a failure.

## 3.23 Disconnect semantics and final states

Disconnect reasons map to final states roughly as follows:

- `LocalRequested`, `RemoteRequested`, `ServerStopped` -> `Disconnected`
- `Timeout`, `ConnectionLost`, `ProtocolError`, `ResourceLimit` -> `Faulted`

Notes:

- the client only publishes `LastDisconnectReason` after an established connection existed
- graceful shutdown still attempts to send `Disconnect` control frames when possible
- invalid disconnect payloads are treated as protocol errors

Disconnect control codes are direction-sensitive:

- client sends `ClientRequested`
- server sends `ServerRequested` or `ServerStopping`

## 3.24 Server send and broadcast behavior

### Targeted send

`server.SendAsync(connectionId, ...)` sends to one currently connected client.

- TCP returns `false` if the session is gone or if send fails after disconnect handling
- UDP returns `false` if the session is gone or has no registered UDP endpoint

### TCP broadcast

Broadcast over TCP awaits individual sends and returns the count that succeeded.

### UDP broadcast

Broadcast over UDP iterates each connected session and attempts one datagram send per session.

## 3.25 Internal synchronization

The library relies on a few specific synchronization strategies:

- per-connection TCP send locks
- per-node UDP send lock
- state locks for client and server snapshot consistency
- bounded queue locks for polling channels
- server session table lock for authoritative connection tracking

The public API is meant to be safe for ordinary multi-threaded use, but the intended consumption model is still simple polling and awaited sends rather than highly concurrent user-managed transport mutation.

## 3.26 Current security boundary

VibeNet is not a secure transport.

It currently provides:

- accidental cross-session UDP registration resistance via session tokens
- protocol validation
- payload limits

It does not provide:

- encryption
- TLS
- authentication
- replay protection
- rate limiting
- DDoS mitigation
- application authorization

The current threat model is cooperative or semi-trusted environments, not hostile internet deployment by itself.

## 3.27 Current UDP endpoint assumption

After registration, the server associates one UDP endpoint with one connection id.

This assumes:

- the client’s UDP endpoint remains stable for the lifetime of the connection
- no mid-session NAT rebinding or endpoint migration support exists

If future development needs roaming or NAT rebinding tolerance, this area would need explicit redesign.

## 3.28 Protocol interoperability requirements

A non-.NET implementation can interoperate with VibeNet if it follows the exact same rules:

- 12-byte VibeNet frame header
- magic `"VIBE"`
- version `1`
- big-endian numeric fields
- 16-byte network Guid encoding based on canonical 32-digit hex form
- exact handshake sequence
- exact packet meanings
- exact disconnect code semantics

Any alternate implementation must also respect:

- TCP as the control channel
- mandatory UDP registration before `Ready`
- raw `byte[]` application payload handling

## 3.29 What the library intentionally does not do

Current intentional non-features:

- no callbacks or events in the public API
- no application-level serialization
- no reliable UDP
- no peer-to-peer mode
- no reconnect support
- no multiplexed channels
- no encryption or authentication
- no automatic NAT traversal
- no message persistence

This is deliberate. The library aims to stay minimal and transport-focused.

## 3.30 Mental model for continuing development

If continuing development, the most important design constraints to preserve are:

1. Keep the public surface small. Most new complexity should remain internal.
2. Preserve the authoritative server model.
3. Preserve the rule that a logical connection requires both TCP and UDP.
4. Preserve raw-byte application payloads and internal protocol framing.
5. Keep public observability queue-based and polling-friendly.
6. Be cautious about adding features that leak protocol details into application code.
7. Prefer configuration knobs only when the behavior is broadly useful and stable enough to expose.

If you need to extend VibeNet, the safest direction is:

- improve internal robustness
- tighten protocol validation
- simplify internal state handling where possible
- add optional internal capabilities without increasing surface area

The riskiest direction is:

- exposing protocol-level internals publicly
- mixing application semantics into the transport layer
- making UDP optional after the library’s core identity has been established around mandatory dual-transport validation
