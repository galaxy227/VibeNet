# VibeNet 1 Library Guide

VibeNet is a single-file, dependency-free C# transport with encrypted TCP and UDP, server-assigned connection IDs, and plaintext byte-array polling. Copy `VibeNet.cs` into the application. The source targets C# 8 and .NET Standard 2.1 APIs. Protocol framing and cryptography remain internal.

## 1. Straightforward Example Program

This complete console example starts an echo server and one client. Both TCP and UDP ports must be reachable, even if the application only sends TCP messages. The default uses port 7777 for each protocol.

```csharp
using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using VibeNet;

internal static class Example
{
    private static async Task Main()
    {
        // Create the server using TCP/UDP port 7777 on the local machine.
        VibeNetServer server = new VibeNetServer(
            7777,
            7777,
            IPAddress.Loopback);

        // Create a client that will connect to the local server.
        VibeNetClient client = new VibeNetClient(
            "127.0.0.1",
            7777,
            7777);

        try
        {
            // Start the server and begin listening for connections.
            StartResult serverStart = await server.StartAsync();

            // Stop if the server failed to start.
            if (!serverStart.Success)
            {
                throw new Exception(
                    "Server failed to start: " +
                    serverStart.Failure?.Detail);
            }

            // Connect the client to the server.
            StartResult clientStart = await client.StartAsync();

            // Stop if the client failed to connect.
            if (!clientStart.Success)
            {
                throw new Exception(
                    "Client failed to connect: " +
                    clientStart.Failure?.Detail);
            }

            // Convert the text message into bytes for VibeNet.
            byte[] messageData = Encoding.UTF8.GetBytes("Hello");

            // Send the message from the client to the server over TCP.
            SendResult sendResult = await client.SendAsync(
                messageData,
                VibeNetTransport.TCP);

            // Stop if the message could not be sent.
            if (sendResult != SendResult.Sent)
            {
                throw new Exception(
                    "Client send failed: " + sendResult);
            }

            // Give the echo operation up to five seconds to complete.
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);

            // Track whether the client receives the echoed message.
            bool received = false;

            // Continue processing network events until the message arrives
            // or the five-second deadline is reached.
            while (!received && DateTime.UtcNow < deadline)
            {
                // Poll all messages currently waiting on the server.
                VibeNetMessage incoming;

                while (server.TryDequeueMessage(out incoming))
                {
                    // Echo the received message back to the client
                    // that originally sent it.
                    SendResult echoResult = await server.SendAsync(
                        incoming.ConnectionId,
                        incoming.Data,
                        incoming.Transport);

                    // Stop if the server could not send the echo.
                    if (echoResult != SendResult.Sent)
                    {
                        throw new Exception(
                            "Server echo send failed: " + echoResult);
                    }
                }

                // Poll all messages currently waiting on the client.
                VibeNetMessage echoed;

                while (client.TryDequeueMessage(out echoed))
                {
                    // Convert the received bytes back into text.
                    string text = Encoding.UTF8.GetString(echoed.Data);

                    // Display the echoed message.
                    Console.WriteLine(text);

                    // Mark the echo operation as complete.
                    received = true;
                }

                // Poll any server failures that occurred in the background.
                VibeNetFailure serverFailure;

                while (server.TryDequeueFailure(out serverFailure))
                {
                    // Display the server failure information.
                    Console.WriteLine(
                        "Server failure: " +
                        serverFailure.Code + ": " +
                        serverFailure.Detail);
                }

                // Poll any client failures that occurred in the background.
                VibeNetFailure clientFailure;

                while (client.TryDequeueFailure(out clientFailure))
                {
                    // Display the client failure information.
                    Console.WriteLine(
                        "Client failure: " +
                        clientFailure.Code + ": " +
                        clientFailure.Detail);
                }

                // Briefly yield before polling the queues again.
                await Task.Delay(1);
            }

            // Fail if the client never received the echoed message.
            if (!received)
            {
                throw new TimeoutException(
                    "No echo received.");
            }

            // Gracefully disconnect the client.
            await client.DisconnectAsync();

            // Gracefully stop the server.
            await server.StopAsync();
        }
        finally
        {
            // Release any remaining client resources.
            client.Dispose();

            // Release any remaining server resources.
            server.Dispose();
        }
    }
}
```

For connections between machines, bind the server to an appropriate interface and supply its reachable address to the client. Loopback in this example keeps traffic local. Configure firewalls and any necessary port forwarding for both protocols. VibeNet does not discover endpoints or configure routers.

Poll messages, failures, and lifecycle notifications from the application's normal processing loop. Await sends and shutdown rather than blocking. Reconnecting or restarting requires a new node instance.

**Security boundary:** encryption is automatic, but peer identity is not authenticated. An attacker intercepting the initial exchange can impersonate either endpoint and read or modify the entire resulting session. There are deliberately no certificates, fingerprints, shared passwords, TLS, or DTLS configuration options. Application login alone over this connection does not remove that limitation.

## 2. Complete Public API Reference

All types are in namespace `VibeNet`. All properties below are get-only. Public data structs have no explicit public constructor; obtain them from library operations. Default struct values are placeholders, not successful operation results. Nullable properties are marked `?`.

### Common operation rules

Operations support concurrent sends and polling. TCP sends serialize per connection; ordering between concurrent callers is unspecified. A successful send means local transport submission completed, not that the remote application processed it. Keep the supplied array unchanged until the returned task completes, including exceptional completion. Empty arrays are valid. The library does not retain caller plaintext after completion. Dequeued arrays belong to the application and require no pool-return or disposal call.

Messages are FIFO in local queue-admission order among surviving entries. There is no global network ordering across connections or TCP/UDP, and no reliability or ordering guarantee for UDP. Closing a connection removes its queued messages. Poll queues regularly. A slow application can cause UDP loss or reliable-connection closure.

Invalid arguments throw `ArgumentException`/`ArgumentOutOfRangeException` (`ArgumentNullException` for null payloads). Ports must be 1–65535; port zero is not supported. Host must be nonblank. Invalid transport enum values and oversized payloads are rejected before sending. Reusing a started/disposed node throws `InvalidOperationException` on startup. Operational startup failures return `StartResult`; caller cancellation throws `OperationCanceledException`. A canceled startup also consumes the instance.

Every asynchronous public method has an optional final `CancellationToken cancellationToken = default`. Cancellation is cooperative. Canceling a TCP send after a potentially partial write closes that connection to prevent framing corruption. Canceling a queued send before writing does not. Canceling a shutdown wait does not undo shutdown already in progress. The UDP socket API used by the library cannot cancel an individual send: the task waits for that operation before reporting cancellation/deadline expiry. Provider RSA generation is also not interruptible, so network deadlines are not hard process-wide execution-time guarantees.

### Enums

Members have sequential integer values starting at zero in the order shown.

| Type | Members and meaning |
|---|---|
| `VibeNetTransport` | `TCP`: reliable ordered records; `UDP`: best-effort datagrams |
| `SendResult` | `Sent`: local send completed; `NotConnected`: no usable connection; `Backpressured`: local admission/buffer capacity exhausted; `Failed`: operational error or deadline |
| `ConnectionState` | `Created`, `Connecting`, `Connected`, `Closing`, `Closed` |
| `FailureCode` | `Socket`, `Protocol`, `Integrity`, `Handshake`, `Timeout`, `ResourceLimit`, `UnsupportedRuntime`, `Internal`. Broad diagnostic categories; not every category is emitted separately by every path. A malformed/MAC-invalid TCP record currently surfaces as a protocol failure. |
| `DisconnectReason` | `LocalRequested`, `RemoteRequested`, `ServerStopped`, `ConnectionLost`, `Timeout`, `ProtocolError`, `ResourceLimit` |

### Configuration

`new VibeNetConfiguration(VibeNetLimits? limits = null, TimeSpan? connectTimeout = null, TimeSpan? heartbeatInterval = null, TimeSpan? idleTimeout = null, TimeSpan? sendTimeout = null, TimeSpan? udpRetryInterval = null)`

| Property / constructor parameter | Default | Meaning |
|---|---|---|
| `Limits` / `limits` | new default limits | Immutable resource policy |
| `ConnectTimeout` / `connectTimeout` | 60 s | Total client startup / admitted server handshake deadline |
| `HeartbeatInterval` / `heartbeatInterval` | 2 s | TCP liveness scheduling interval |
| `IdleTimeout` / `idleTimeout` | 15 s | Time without accepted TCP activity before closure |
| `SendTimeout` / `sendTimeout` | 5 s | Send/control deadline and graceful-close waiting budget |
| `UdpRetryInterval` / `udpRetryInterval` | 250 ms | Protected UDP registration retry interval |

Durations must be between 1 and 2,147,483,646 milliseconds inclusive. Idle timeout must exceed heartbeat interval. No configuration changes are allowed after construction; reuse immutable configuration objects between nodes if useful.

`new VibeNetLimits(...)` accepts the optional named parameters below, in this order. Each property exposes its corresponding constructor value. The table gives every parameter, property, type, and default.

| Parameter | Property | Type | Default | Limit scope |
|---|---|---|---:|---|
| `maxClients` | `MaxClients` | `int` | 10000 | Server total admitted connections, including pending |
| `maxPendingHandshakes` | `MaxPendingHandshakes` | `int` | 8 | Concurrent admitted server handshakes |
| `maxTcpPayloadBytes` | `MaxTcpPayloadBytes` | `int` | 1048576 | Maximum application TCP plaintext bytes |
| `maxUdpPayloadBytes` | `MaxUdpPayloadBytes` | `int` | 1024 | Maximum application UDP plaintext bytes |
| `maxQueuedMessages` | `MaxQueuedMessages` | `int` | 8192 | Node receive queue count |
| `maxMessagesPerConnection` | `MaxMessagesPerConnection` | `int` | 256 | Per-connection receive queue count |
| `maxQueuedBytes` | `MaxQueuedBytes` | `long` | 67108864 | Node queued plaintext bytes |
| `maxBufferedBytes` | `MaxBufferedBytes` | `long` | 134217728 | Node accounted payload/work reservations |
| `maxBufferedBytesPerConnection` | `MaxBufferedBytesPerConnection` | `long` | 8388608 | Per-connection accounted reservations |
| `maxPendingSends` | `MaxPendingSends` | `int` | 1024 | Node admitted application sends |
| `maxPendingSendsPerConnection` | `MaxPendingSendsPerConnection` | `int` | 64 | Per-connection admitted application sends |
| `maxEvents` | `MaxEvents` | `int` | 2048 | Capacity of each lifecycle/failure event ring |
| `maxPacketsPerSecond` | `MaxPacketsPerSecond` | `int` | 250000 | Node inbound records/datagrams, separate TCP and UDP buckets |
| `maxPacketsPerSecondPerConnection` | `MaxPacketsPerSecondPerConnection` | `int` | 1000 | Inbound per-connection rate, separate TCP and UDP buckets |
| `maxBytesPerSecond` | `MaxBytesPerSecond` | `long` | 134217728 | Inbound byte rate per node and per connection, separately per transport |
| `maxConnectionAttemptsPerSecond` | `MaxConnectionAttemptsPerSecond` | `int` | 64 | Server TCP admission attempt rate |
| `maxAttemptsPerAddressPerSecond` | `MaxAttemptsPerAddressPerSecond` | `int` | 16 | Server source-address admission rate; clients behind NAT share it |
| `maxTrackedAddresses` | `MaxTrackedAddresses` | `int` | 4096 | Server admission address-table capacity |

All limits must be positive. TCP payload hard maximum is 16,777,216; UDP hard maximum is 65,407. Queued-byte and per-connection-buffer limits cannot exceed the global buffer limit. Other capacities are independent: a configuration can validly be too small to admit a maximum-size message. Rates use token buckets with an initial one-second burst, not fixed wall-clock counters. Byte accounting is not a total managed-heap/RSS ceiling. Defaults are safety bounds, not a demonstrated 10,000-client capacity guarantee.

### Base node

`VibeNetNode : IDisposable` is an abstract shared base with no externally accessible constructor; it is not a user-extensibility interface.

| Member | Contract |
|---|---|
| `VibeNetConfiguration Configuration` | Effective immutable configuration |
| `NetworkStatistics Statistics` | Approximate concurrent snapshot; fields are not an atomic transaction |
| `bool TryDequeueMessage(out VibeNetMessage message)` | Removes oldest surviving plaintext message; false/default when empty |
| `bool TryDequeueFailure(out VibeNetFailure failure)` | Removes oldest retained diagnostic; false/default when empty |
| `bool TryDequeueDisconnected(out DisconnectInfo info)` | Removes oldest retained disconnect event; published only for established connections |
| `void Dispose()` | Idempotent immediate cancellation/socket closure; does not synchronously join workers. Prefer awaited stop/disconnect for completion. |

### Server

`new VibeNetServer(int tcpPort = 7777, int? udpPort = null, IPAddress? bindAddress = null, VibeNetConfiguration? configuration = null)`

Null UDP port means the TCP port number; null address means IPv4 Any. IPv6 Any enables dual mode where supported. The server also inherits every base-node member.

| Member | Contract |
|---|---|
| `int TcpPort`, `int UdpPort`, `IPAddress BindAddress` | Configured local endpoints |
| `int ConnectedClientCount` | Established clients |
| `int PendingClientCount` | Admitted clients not yet established |
| `bool IsRunning` | Listener started and shutdown not initiated; not a remote-health guarantee |
| `IReadOnlyList<ConnectionInfo> Connections` | Allocated snapshot of admitted connections, including pending; O(N), avoid frequent full-table polling at scale |
| `Task<StartResult> StartAsync(...)` | Binds both sockets and starts background work |
| `bool TryDequeueConnected(out ConnectionInfo info)` | Polls establishment events after the full TCP+UDP handshake |
| `bool TryGetConnection(Guid id, out ConnectionInfo info)` | Looks up admitted connection, including pending; false/default if absent |
| `Task<SendResult> SendAsync(Guid connectionId, byte[] data, VibeNetTransport transport, ...)` | Sends to one established client |
| `Task<BroadcastResult> BroadcastAsync(byte[] data, VibeNetTransport transport, ...)` | Attempts snapshot targets, batches of up to 32; at most four concurrent broadcasts. Excess broadcasts report current established count as backpressured. Not an atomic all-client delivery. |
| `Task<bool> DisconnectClientAsync(Guid id, ...)` | False if absent; otherwise requests closure and awaits its disconnect operation |
| `Task StopAsync(...)` | Idempotent shared shutdown: best-effort protected close, force-close, join workers |
| `void Dispose()` | Immediate server stop request; does not await worker completion |

### Client

`new VibeNetClient(string host, int tcpPort = 7777, int? udpPort = null, VibeNetConfiguration? configuration = null)`

DNS addresses are tried using available IPv4/IPv6 families. UDP targets the address selected by TCP. Null UDP port means TCP port number. All base members are inherited.

| Member | Contract |
|---|---|
| `string Host`, `int TcpPort`, `int UdpPort` | Configured destination |
| `ConnectionState State` | Lifecycle snapshot; Connecting also covers DNS/TCP startup |
| `Guid ConnectionId` | Server-assigned ID once received; `Guid.Empty` before assignment, retained after closure |
| `ConnectionInfo? Connection` | Snapshot once the internal TCP link exists; null before that; can represent Connecting/Closed |
| `Task<StartResult> StartAsync(...)` | Completes only after key confirmation, UDP registration, and TCP Ready exchange |
| `Task<SendResult> SendAsync(byte[] data, VibeNetTransport transport, ...)` | Sends to server |
| `Task DisconnectAsync(...)` | Best-effort protected disconnect, disposal and worker joining |
| `void Dispose()` | Immediate local closure; does not await workers |

### Result and observation structs

| Type | Every public property |
|---|---|
| `VibeNetMessage` | `Guid ConnectionId` identifies the session on both endpoints; `VibeNetTransport Transport`; `byte[] Data` is caller-owned plaintext |
| `VibeNetFailure` | `Guid? ConnectionId` (null for node-wide/unassigned failures); `FailureCode Code`; `string Detail` diagnostic text, not a stable machine-readable protocol |
| `StartResult` | `bool Success`; `VibeNetFailure? Failure` (null on success) |
| `ConnectionInfo` | `Guid Id`; `IPEndPoint TcpEndpoint`; `IPEndPoint? UdpEndpoint`; `ConnectionState State`. Endpoints are copied when constructing snapshots. |
| `DisconnectInfo` | `Guid ConnectionId`; `DisconnectReason Reason`. Reasons are best effort: abrupt failures may prevent delivery of a graceful reason. |
| `BroadcastResult` | `int Sent`, `int NotConnected`, `int Backpressured`, `int Failed`, each counting the corresponding send result |
| `NetworkStatistics` | `long BufferedBytes`, `int QueuedMessages`, `long QueuedBytes`, `long DroppedUdp`, `long LostEvents`, `int PendingSends` |

`BufferedBytes` includes conservative transient payload reservations and queued plaintext; `QueuedBytes` measures queued plaintext alone. Dequeue releases library accounting even while the caller retains its array. Fixed socket buffers, runtime socket state, RSA/provider allocations, connection objects, and event-ring storage are outside this metric. `DroppedUdp` includes rejected/invalid/overloaded inbound datagrams and queue evictions. `LostEvents` sums ring overwrites. Event rings discard oldest entries when full; reconcile server state through snapshots when loss is observed. These counters do not promise an event per rejected hostile packet.


### Interpreting lifecycle and failure categories

`Created` means startup has not been attempted; `Connecting` includes DNS, socket connection and handshake work; `Connected` permits application sends; `Closing` is teardown in progress; `Closed` is terminal. The client exposes this state directly and each `ConnectionInfo` captures its session's state at snapshot time. State inspection followed by a send is not an atomic operation: always inspect the send result as well.

For failures, `Socket` denotes transport I/O or binding, `Protocol` invalid framing/state, `Integrity` an integrity category, `Handshake` startup negotiation, `Timeout` an internal deadline, `ResourceLimit` admission/accounting pressure, `UnsupportedRuntime` an unavailable capability, and `Internal` an internal-error category. These categories are not a promise of one dedicated code for every underlying exception. In particular, unsupported-provider startup and established TCP protocol failures follow the mappings described above.

Disconnect reasons distinguish local requests, received remote requests, server shutdown, connection loss, inactivity/deadline timeout, invalid protocol, and resource exhaustion in the enum's listed order. A reason is the locally observed outcome; it need not match the other endpoint's reason after simultaneous closure or transport failure. Closed is the terminal state for both orderly and error-driven closure.

### Working with limits and send results

Construct policy once, then pass it to a node. This fragment limits one node's admitted session count and sets its TCP application payload ceiling; all omitted values keep their documented defaults:

```csharp
var limits = new VibeNetLimits(
    maxClients: 500,
    maxTcpPayloadBytes: 64 * 1024);
var configuration = new VibeNetConfiguration(
    limits: limits,
    connectTimeout: TimeSpan.FromSeconds(30));
```

The following fragment assumes an established `client` and a `byte[] payload`. Do not repeatedly retry in a tight loop when admission is exhausted:

```csharp
SendResult result = await client.SendAsync(payload, VibeNetTransport.TCP);
switch (result)
{
    case SendResult.Sent:
        break; // Local submission completed; this is not a remote receipt.
    case SendResult.Backpressured:
        Console.WriteLine("Send capacity is full; defer or discard at the caller.");
        break;
    case SendResult.NotConnected:
        Console.WriteLine("The connection is no longer available.");
        break;
    case SendResult.Failed:
        Console.WriteLine("Send failed; poll failures and connection state.");
        break;
}
```

`BroadcastResult` aggregates independent outcomes. A broadcast can partially succeed, and retrying the entire broadcast may duplicate messages for recipients that already received it. Cancellation throws rather than returning partial counters; earlier sends may still have reached recipients. The task joins already-started sends before exceptional completion, preserving outbound-array ownership. A missing target returns NotConnected for targeted sends and false for disconnect requests; neither result proves anything about a previous send's delivery.

Failure, message, and lifecycle queues are independent. There is no single ordering spanning all queues, and polling a failure does not itself remove a disconnect notification. Check startup results immediately, send results when awaiting each send, and background observations during the normal processing loop.

## 3. Technical Architecture Review

This section establishes the project's engineering intent as well as its current implementation. Preserve behavioral guarantees when changing internals; distinguish intentional scope boundaries from implementation choices that can improve. The API reference describes what callers can rely on, while the wire specification describes what another endpoint must implement exactly.

### 3.1 Purpose and enduring design goals

VibeNet should remain a small, standalone C# transport library with a minimal public API and deliberately opinionated internals. A caller supplies endpoints, starts a node, sends bytes, polls observations, and closes the node. Framing, cryptography, resource accounting, and connection coordination belong inside the library so ordinary use stays simple.

The foundational requirements are:

1. Distribute the library as one `VibeNet.cs` file with zero third-party dependencies. Development tools and supporting documentation remain separate from the distributable.
2. Remain generic C#: use standard runtime sockets, threading, and cryptographic primitives without binding applications to a framework, scheduler, host environment, or object model.
3. Exchange raw `byte[]` payloads. Decrypt internally and return complete plaintext messages with explicit ownership; never require callers to interpret transport headers or manage cryptographic buffers.
4. Require TCP and UDP for one logical connection. TCP is the reliable control and data path; UDP is the best-effort data path whose reachability is validated during startup.
5. Let the server assign connection identity and govern transport admission. Session identity is a transport fact, not an authenticated user identity or authority over payload meaning.
6. Use polling as the public concurrency boundary. Background work must not invoke application callbacks or require an application-specific processing loop.
7. Bound work and retained resources, make overload behavior explicit, and keep important facts observable through results, snapshots, counters, and queued notifications.
8. Keep configuration immutable and purposeful. Expose stable operational policy, not protocol machinery or a collection of cryptographic switches.
9. Provide automatic encryption and integrity protection without certificates, fingerprints, pre-shared secrets, or user-managed keys. Describe the resulting trust limitations precisely.

Minimalism is measured by the work required of the caller and the clarity of the implementation, not by minimizing line count. Internal complexity is justified when it enforces a clear invariant or removes a measured bottleneck. Avoid abstractions whose state, ownership, or failure behavior is harder to verify than the problem they solve.

### 3.2 Intentional boundaries

VibeNet does not interpret, serialize, compress, persist, or automatically route application messages. It provides no request/response protocol, remote invocation system, application acknowledgments, multiplexed channels, reliable UDP, automatic reconnect, endpoint discovery, NAT traversal, or mid-session endpoint migration. These are scope decisions, not missing transport error paths.

Applications may explicitly forward received bytes through other connections. Every hop remains an ordinary client/server connection; forwarding does not create end-to-end confidentiality through an intermediary. The intermediary terminates encryption and can read or change plaintext. Routing policy and authorization stay with the application.

Transport confidentiality and integrity are internal responsibilities in this version. Peer identity authentication is not provided under the current trust constraints. That distinction must not be used to justify omitting replay checks, authenticated controls, or robust parsing.

### 3.3 One logical connection and authoritative identity

A TCP socket alone is a pending connection. Successful startup requires cryptographic confirmation, bidirectional UDP registration, and a final protected TCP exchange. Application traffic must not be admitted on a half-established session.

The server creates a fresh `Guid` and assigns it to the connection. Both endpoints expose that same ID on received messages. Payload contents cannot override the transport-assigned sender identity. A received ID identifies the session through which bytes arrived; it cannot establish who controls the remote process.

The server tracks admitted sessions separately from established sessions. Pending connections consume `MaxClients` capacity and the additional pending-handshake allowance. Snapshots may include pending entries; establishment notifications and disconnect events have their separately documented lifecycle meaning.

### 3.4 Startup state machine and UDP registration

Server `StartAsync` binds listeners; client `StartAsync` performs the complete connection attempt. The per-connection sequence is:

1. Resolve/connect TCP on the client; accept and apply admission limits on the server.
2. Assign the server ID, generate the ephemeral key, and exchange strictly bounded handshake messages.
3. Derive transcript-bound directional secrets and exchange role-specific Finished proofs.
4. Send protected UDP Register from the client. The server validates the session and learns the source endpoint, then sends protected Registered.
5. Retry registration at `UdpRetryInterval` until acknowledgment or the total startup deadline. Each retry has a fresh sequence, so replay rejection remains active.
6. Exchange protected TCP Ready and ReadyAck. The server rejects Ready if UDP registration has not succeeded.
7. Transition to Connected and publish establishment. Endpoint scheduling can make the two observations occur at slightly different times.

The final TCP exchange coordinates completion after UDP validation; an arbitrary UDP datagram cannot establish a session merely by naming its ID. The initial UDP source port need not match the TCP source port. Once registered, the UDP endpoint remains fixed. Rebinding or migration requires a new connection rather than implicitly transferring session ownership.

Pending-handshake deadlines bound network waiting. Admission occurs before expensive per-session cryptographic work, which runs away from the shared accept loop with bounded concurrency. Capacity rejection need not include a protocol response, and a client must not depend on a trustworthy pre-key rejection reason.

### 3.5 Internal responsibilities and synchronization

| Internal component | Responsibility |
|---|---|
| `VibeNetNode` | Shared configuration, resource accounting, polling queues, transport rate gates |
| `VibeNetServer` / `VibeNetClient` | Socket ownership, startup, background-loop coordination, public lifecycle |
| `Link` | Session state, endpoint binding, handshake, send ownership, controls and teardown |
| `Wire` / `Crypto` / `Lane` | Framing, primitive composition, directional keys, epochs, sequencing and replay checks |
| `Budget` / `Lease` | Atomic reservation and exactly-once release of accounted payload work |
| `MessageQueue` / `Ring` | Bounded payload admission and lossy observation queues |
| `RateGate` / `DeadlineQueue` | Bounded inbound admission and scheduled heartbeat work |

These helpers are implementation details rather than extension points. Keep session state changes, key transitions, endpoint binding, queue ownership, and cleanup under explicit synchronization. Per-connection TCP and UDP send gates serialize their respective key/sequence state; a node gate serializes UDP socket sends. Receive-state and lifetime locks protect authentication state and disposal. The server table and polling queues have their own short critical sections.

Do not hold shared table/queue locks over asynchronous I/O or expensive key generation. Preserve consistent lock ordering, and keep teardown from disposing a provider or semaphore still used by an admitted operation. Concurrent sends and polling are supported; callers should coordinate their own startup/shutdown ownership rather than use lifecycle races as a synchronization mechanism.

### 3.6 TCP, UDP, and liveness semantics

TCP is a byte stream: the parser reads complete bounded headers and bodies rather than assuming one read equals one message. All writes for a connection serialize, including controls and key changes. An incomplete, invalid, or unauthenticated reliable record cannot be skipped safely; the session closes. Cancellation after a possible partial write likewise invalidates that connection.

UDP carries one protected record per datagram. It does not retransmit regular application data, reorder delivery, or add application acknowledgments. Unknown sessions, invalid lengths, unexpected endpoints, authentication failures, stale epochs, and replayed records are rejected. An isolated UDP send failure or dropped datagram does not itself prove TCP has failed; a failed coordinated key transition may require session closure.

Heartbeat health is based on accepted TCP activity. Protected Ping/Pong exchanges keep the control path observable. UDP traffic does not reset that clock, and successful initial registration does not guarantee UDP remains reachable forever. Scheduling delays, process suspension, and remote silence can all produce a legitimate timeout; another connection attempt requires a new node.

### 3.7 Polling, ordering, and observation

Polling separates background network work from application execution. The library queues complete plaintext messages, operational failures, server establishment events, and established-session disconnects. The application chooses when and where to consume them, including how much work to process per iteration.

Message order is local FIFO among admitted entries that remain queued. TCP's per-stream order does not imply an order across senders or transports. Concurrent consumers can dequeue safely but must coordinate processing themselves if they require completion order. Connection removal discards that owner's pending messages so stale session data is not retained indefinitely.

Notification rings overwrite their oldest entry when full. They preserve bounded observation cost rather than an audit trail. `LostEvents` makes this loss visible; server snapshots can reconcile current admission state. A clean disconnect need not generate a failure, an invalid hostile packet need not generate an individual diagnostic, and failure text is not a stable protocol for applications to parse.

### 3.8 Payload ownership, limits, and backpressure

Validate caller payload size before admission. Reserve global and per-connection send counts and byte budgets before payload work; release them on success, cancellation, and every failure path. Incoming framing must be validated before a peer-controlled length determines a body allocation. Exact control sizes are internal rules independent of application payload policy.

The application retains its outbound array and must not modify it until the operation completes. A dequeued plaintext array becomes application-owned without any pool-return obligation. Accounting ends at dequeue, so library statistics cannot measure memory the application continues to retain. Optimizations must not silently change these ownership rules.

The receive queue maintains global FIFO, owner membership, and UDP membership. Normal admission/dequeue and oldest-UDP eviction are O(1); removal of a connection costs O(its queued messages). Both message count and byte limits matter: many tiny messages and a few large messages create different pressure.

Under shared queue pressure, reliable data may evict queued UDP. Per-connection count caps can reject before this eviction policy is considered. Unadmittable UDP is dropped; unadmittable reliable data closes the responsible connection rather than pretending it was delivered. This contains local pressure but does not guarantee every other peer remains unaffected by global exhaustion.

`MaxBufferedBytes` bounds accounted payload reservations, not total process memory. Fixed receive buffers, runtime socket state, provider allocations, connection metadata, and event storage have other count/fixed bounds. Preserve that distinction in diagnostics and capacity claims. Do not add unbounded queues, tasks, retry lists, or source-address tables behind an apparently bounded configuration.

### 3.9 Scale and admission strategy

One UDP socket and a reusable 64-KiB receive buffer serve each node. Cheap header, type, length, and session checks precede copying candidate packets and authenticating them. Accepted traffic still allocates cryptographic intermediates and payload arrays; there is no zero-allocation promise.

Server heartbeat scheduling uses a deadline heap, bounded dispatch, and jitter rather than allocating and scanning a complete client snapshot on every tick. Heap updates are O(log N); explicit snapshots and broadcasts remain O(N). Broadcast uses bounded batches and admission rather than spawning unlimited concurrent operations. Per-recipient encryption remains necessary because sessions have distinct keys.

Handshake admission combines total/pending capacity, global attempts, and bounded source-address tracking with expiry. Clients sharing an address also share its admission quota. Rate limits use token buckets with a defined burst. TCP and UDP quotas are separate so unauthenticated UDP traffic cannot directly consume reliable-lane quota. Known-session UDP can spend its UDP quota before authentication to bound cryptographic work; this trades UDP availability under attack for contained CPU cost.

The goal is efficient operation with many thousands of connections, supported by measured CPU, allocation, throughput, fairness, latency, and teardown behavior. A default capacity of 10,000 is a configured ceiling, not evidence that a particular machine can sustain that workload. Prefer changes that remove per-packet work or unbounded fan-out; justify pools, batching, and caching with measurements and auditable ownership.

### 3.10 Addressing and runtime portability

The API keeps addressing explicit: a server bind address, a client host, and TCP/UDP ports. Omitted UDP port uses the TCP port number, not the same socket. The default server bind is IPv4 Any; IPv6 Any requests dual mode where supported. Client DNS resolution tries usable IPv4/IPv6 addresses and targets UDP at the address selected for TCP. The library does not discover public addresses or manipulate network infrastructure.

C# syntax/API availability and working runtime implementations are different requirements. Keep the common source within the declared language/API baseline; verify actual socket and cryptographic providers on supported runtimes. Do not introduce direct native bindings, runtime code generation, or hidden package requirements to make one deployment work. An environment without raw TCP and UDP cannot satisfy this transport contract unchanged.

Unsupported capabilities must fail clearly, never trigger plaintext fallback. Support claims must identify qualified runtimes rather than assume compilation proves execution compatibility. Platform-specific integration and qualification procedures belong outside the generic library contract.

### 3.11 Lifecycle, cancellation, and failure model

Instances are single-use. A fresh instance supplies clean sockets, queues, cancellation scope, and session/key state. Automatic reuse would complicate ownership and stale-work rejection, so reconnection policy remains external.

Graceful disconnect sends protected Bye and awaits acknowledgment within the configured budget where possible. Shutdown then closes sockets, cancels owned work, and joins workers. Server shutdown batches notifications before force-closing remaining sessions. Per-link lifetime references defer cleanup until admitted work exits. Dispose requests immediate teardown without synchronously joining it; awaited StopAsync/DisconnectAsync is the completion-oriented path.

Keep three outcome classes distinct: invalid use throws ordinary argument/state exceptions; caller cancellation throws `OperationCanceledException`; operational problems use startup/send results and failure observations. Disconnect reasons describe the terminal cause separately from diagnostic text. Cancellation does not promise that bytes already submitted were not transmitted, and canceling a wait cannot reverse shutdown already initiated.

Network deadlines bound cooperative waiting, not arbitrary provider execution. RSA generation is not interruptible, and the chosen UDP send API must finish before cancellation can be observed. Do not claim a strict universal wall-clock shutdown bound without changing and validating those underlying constraints.

### 3.12 Security construction and trust boundary

The fixed suite uses fresh per-session **RSA-2048 with OAEP-SHA1**, HKDF-SHA256, AES-256-CBC with random 128-bit IVs and PKCS7 padding, and HMAC-SHA256 in encrypt-then-MAC order. This construction reflects the available runtime-provider baseline. RSA-2048 limits conventional overall strength to approximately 112 bits; AES-256 does not make the session 256-bit secure. OAEP-SHA1 is a key-transport compatibility choice, not a signature scheme.

The implementation checks the RSA provider's actual modulus size and uses a compatible CSP provider if the generic provider ignores the requested size. This is one suite, not negotiated algorithm fallback. A fresh private key is disposed after decrypting the fresh seed; no persistent server decryption key is installed. Role-specific Finished proofs bind possession of derived secrets to the handshake transcript without proving peer identity.

Authenticate the complete protected header and ciphertext before any CBC decryption or padding processing. Compare fixed-size tags in fixed work. Derive separate keys for direction and transport; advance replay state only after successful authentication/decryption. Clear owned temporary key material and dispose providers, while recognizing that a managed runtime cannot guarantee erasure of every copied secret. Key updates limit use of each traffic key; they do not heal compromise of current traffic secrets.

The intended protection is confidentiality against passive interception and integrity/replay resistance against outsiders lacking session keys, provided the initial exchange was not intercepted. An active initial man-in-the-middle can impersonate both sides and read or modify the entire resulting session. Key confirmation, server-assigned IDs, and ordinary application login do not independently solve that identity problem.

Malicious session participants, endpoint compromise, traffic analysis, and volumetric DDoS remain outside the guarantee. Rate limiting and bounded parsing mitigate resource abuse but cannot prevent network-link saturation. Authorization remains an application responsibility. This custom protocol requires independent review and runtime qualification; the guide is a specification, not a security certification.

### 3.13 Wire specification and interoperability

All numeric wire fields use big-endian encoding. GUIDs use the exact 16-byte .NET `Guid.ToByteArray()` representation (not textual UUID byte order). Magic is `0x564E5331`, ASCII `VNS1`, and version is 1. There is no cipher negotiation.

Plain handshake frames have a 12-byte header: magic at 0 (4 bytes), version at 4 (1), type at 5 (1), zero reserved at 6 (2), payload length at 8 (4). Exact payload lengths and expected types are checked.

| Type | Payload |
|---|---|
| 100 server hello | 16-byte ID, 32 random bytes, 256-byte RSA modulus, 3-byte exponent `01 00 01`: 307 bytes |
| 101 client reply | 32 random bytes, 256-byte OAEP-SHA1 encryption of a fresh 32-byte seed: 288 bytes |
| 102 client Finished | 32-byte client confirmation value |
| 103 server Finished | 32-byte server confirmation value |

Context is ASCII `VNS1/RSA2048-OAEP-SHA1/AES256-CBC-HMACSHA256/handshake`. Transcript salt is SHA256(context || hello payload || reply payload). Root is HKDF-SHA256(salt, seed, context, 32). `Expand(root,label)` means HKDF-SHA256(empty salt, root, ASCII `VNS1/` + label, 32). Finished values use `client-finished` and `server-finished`; lane secrets use `c2s/tcp`, `s2c/tcp`, `c2s/udp`, `s2c/udp`. Each lane derives `encryption` and `authentication` keys; the next epoch derives `next` from the current lane secret.

Protected records have a 64-byte authenticated header:

| Offset | Bytes | Field |
|---:|---:|---|
| 0 | 4 | Magic |
| 4 | 1 | Version |
| 5 | 1 | Kind |
| 6 | 1 | Transport: TCP=0, UDP=1 |
| 7 | 1 | Zero reserved |
| 8 | 16 | Session GUID |
| 24 | 4 | Key epoch |
| 28 | 8 | Sequence |
| 36 | 4 | Plaintext length |
| 40 | 4 | Ciphertext length |
| 44 | 4 | Zero reserved |
| 48 | 16 | Random CBC IV |

Ciphertext follows, then the full 32-byte HMAC tag. Ciphertext length is `(plaintextLength / 16 + 1) * 16`; total record size is 96 plus ciphertext length. A 1,024-byte UDP payload therefore occupies 1,136 bytes before IP/UDP headers. Larger datagrams can fragment; do not interpret the hard size ceiling as a safe path MTU.

Kinds: Data=1 (either transport), Register=2 and Registered=3 (UDP, empty), Ready=4 and ReadyAck=5 (TCP, empty), Ping=6 and Pong=7 (TCP, empty), Bye=8 (TCP, one byte: 0 ordinary request, 1 server stop), TcpUpdate=9 (TCP, empty), UdpUpdate=10 and UdpUpdated=11 (TCP, four-byte next UDP epoch), ByeAck=12 (TCP, empty). Control kinds, lengths, transport, and expected handshake state are validated. Invalid TCP records terminate the session; invalid UDP records are silently counted/dropped.

Each direction/transport starts at epoch and sequence zero. TCP expects exact next sequence. UDP uses a 256-record sliding replay window with independent state per accepted epoch. Writers update at 65,536 records or 64 MiB plaintext; a small control-record allowance permits the protected transition. Receivers enforce record exhaustion and a bounded byte allowance for the last maximum-sized record/control traffic. TCP announces updates under its old key then switches. UDP announces the next epoch over TCP, waits for protected acknowledgment, and switches; the receiver keeps the prior UDP epoch for two seconds. No attacker-selected epoch causes new key-state allocation. This ratchet is not recovery after compromise of current traffic secrets.



### 3.14 Rules for continuing development

Preserve the public model of endpoints, raw bytes, awaited operations, and polling. Add a public option only when it expresses broadly useful, stable application policy that cannot reasonably remain internal. Keep framing, secret material, replay windows, and key-update coordination private. The one-file requirement does not justify mixing unrelated responsibilities or removing explanatory invariants from code.

When changing a connection path, trace admission, ownership, state transition, observation, and cleanup. In particular, verify that no application payload becomes visible before establishment, no rejected record advances security state, no failed operation leaks reservations, and no shutdown disposes a resource still in use. Treat overload and cancellation as ordinary paths deserving the same reasoning as success.

When changing the protocol, specify exact bytes, lengths, encodings, state transitions, control meanings, and key derivation inputs together. Another implementation must match the .NET GUID wire representation as well as numeric byte order; textual UUID conversion is insufficient. Do not carry forward the earlier plaintext prototype's header, session-token scheme, or assumptions that encryption is outside the transport. Version 1 uses its own identifier and protected protocol. There is no obligation to preserve the unpublished prototype API or wire format; future compatibility decisions should be explicit once consumers exist.

Prefer standard cryptographic primitives and reviewable composition. A suite change is a protocol/security decision requiring analysis and interoperability evidence, not a silent runtime workaround. Do not add user-managed trust material or claim authenticated identity without an explicit change to the project's requirements.

Performance improvements must preserve bounded resource ownership and observable overload behavior. Demonstrate benefits with representative traffic and realistic slow or hostile peers, not just idle connection counts. Keep measured results separate from desired scale, and retain known limitations when updating documentation. The safest evolution improves correctness, validation, containment, and efficiency while keeping the caller's model small.

### 3.15 Supporting development material

`Test/` contains the standalone functional/internal-invariant harness, load and interoperability utilities, and runtime qualification helpers. Its `README.md` and `Artifacts/` hold execution instructions, coverage, measured results, and outstanding qualification work. Keep extensive test and environment-specific documentation there; it is development support, not part of the single-file distribution or public API.


