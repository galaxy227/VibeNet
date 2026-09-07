# VibeNet test and qualification harness

The test suite is rewritten for VibeNet 1 and uses no test-framework packages. `Program.cs` covers configuration, crypto integrity, record sequencing, queues/admission, cancellation, real TCP/UDP, multi-client relay/broadcast/rekey, hostile traffic/overflow, and admission deadlines/rate isolation/IPv6. Tests compile with the source to exercise internal invariants; reflection in tests forces key thresholds and send contention. The distributed library uses no reflection.

## Run

From the repository root:

```powershell
dotnet run --project cs/Test/VibeNetTests.csproj -c Release
./cs/Test/Run-Tests.ps1
# Override -UnityEditor if the installed Unity Editor directory differs.
dotnet run --project cs/Test/VibeNetTests.csproj -c Release -- --load 128 10
```

`Run-Tests.ps1` executes .NET tests, compiles C# 8 against Unity's .NET Standard 2.1 reference assembly, and executes with bundled Mono. It checks exit codes and writes logs in `Artifacts`. This is not a Unity player test.

For cross-runtime checks, start `--serve PORT SECONDS` on one runtime and run `--client HOST PORT` on the other. Swap roles. Both TCP and UDP echo are checked. Mono executable is produced by the script. Allow the server enough time for Mono's variable RSA generation latency.

The optional `--load CLIENTS SECONDS` accepts up to 10,000 clients and 86,400 seconds. It connects sequentially, then attempts 64-byte UDP echoes at 20 Hz/client. It reports setup time, sends/echoes, failures/drops, sampled payload accounting, latency histogram ceilings, managed heap and GC counts. Latency includes local scheduling/polling. At saturation the loop can fall behind its nominal rate; calculate achieved throughput from counts. The final sample can include outstanding datagrams. Exit success checks nonzero received traffic, no failed sends, and released server accounting; it does not certify zero loss or a latency SLA. This single-process echo loop is not an independent distributed load generator or a complete churn/soak test. Full-collection heap samples alone cannot establish a memory plateau. Invalid negative runtime memory readings are labeled unavailable.

## Unity player harness

```powershell
./cs/Test/Prepare-Unity.ps1
```

This copies the current source/tests and the Unity runner to `work/UnityQualification`. It overwrites those generated files; choose `-Destination` for another generated project. Open using a licensed Unity 6000.3.20f1 editor, or invoke `-batchmode -nographics -quit -projectPath <absolute-project> -executeMethod VibeNetQualificationBuild.Build -logFile <log>` from the editor executable. The initial build method creates a Windows x64 Mono player. Launch `Build/VibeNetQualification.exe -resultPath <absolute-results-file>`; exit code is zero when all groups pass.

Use a fresh generated project if an earlier prototype left duplicate runner scripts. Native-player/IL2CPP/mobile qualification requires corresponding build targets, settings and installed toolchains; the provided Windows build method does not automatically validate these. Port allocation tests reserve then release ephemeral TCP ports, so parallel test processes may collide; rerun isolated before diagnosing a transport defect.

## Evidence: September 6, 2026

| Check | Result |
|---|---|
| .NET SDK 10.0.302 / runtime 10.0.10 | 2,304 assertions, nine groups, pass |
| Unity 6000.3.20f1 .NET Standard 2.1 compilation, C# 8 | Pass |
| Unity bundled Mono execution | 2,304 assertions, nine groups, pass |
| .NET server / Mono client TCP+UDP | Pass |
| Mono server / .NET client TCP+UDP | Pass |
| Local 128 clients, 10 seconds | 25,600 sent/received, zero failed sends and server UDP drops; RTT p50/p95/p99 ceilings 6/19/29 ms |
| Unity Windows player build | Blocked: licensing IPC refused; `com.unity.editor.headless` not found |
| Other native players, IL2CPP, mobile, lifecycle/stripping | Not run |
| 10,000 active clients, distributed load, 24-hour churn/soak | Not run |
| Independent protocol/cryptographic audit | Not performed |

Logs are in `Artifacts`: `dotnet-tests.txt`, `unity-mono-tests.txt`, `interop-mono-client.txt`, `interop-dotnet-client.txt`, `load-128.txt`, `unity-build.log`. Earlier `load-16.txt` is historical evidence from before subsequent changes, not final qualification.

Existing assertions include RFC 5869 known-answer derivation, every-byte protected-record tampering, replay poisoning/duplicate/old/cross-session/cross-direction rejection, epoch transitions, deterministic random-header rejection, concurrent budget/lease behavior, queue eviction/cleanup, 10,000 cancellation-registration iterations, bidirectional encrypted transport, empty UDP payloads, graceful close reasons, send admission, heartbeat liveness, relay forwarding, protected TCP/UDP key updates, corrupted TCP MAC rejection, queue-overflow closure, pending-handshake saturation/expiry, and TCP survival after UDP quota exhaustion.

Outstanding release work includes full fixed handshake/record interoperability vectors, deterministic partial-read/write and transport fault injection, sustained slow-reader/churn attacks, large legitimate-peer fairness measurements, real Unity player lifecycle/AOT/platform coverage, independent cryptographic review, and sustained scale qualification. Current passing tests are necessary evidence, not exhaustive proof of correctness or production readiness.

## Runtime feasibility decisions

The originally planned P-256 ECDH and AES-GCM construction was not usable with the probed Unity bundled Mono providers: ECDH raised NotImplementedException and AES-GCM raised PlatformNotSupportedException. RSA OAEP-SHA256 was also unavailable there. RSA OAEP-SHA1, AES-CBC, and HMAC-SHA256 were usable. RSA-3072 generation exhibited handshake delays exceeding 60 seconds, motivating the fixed RSA-2048 suite now specified in the library guide. This records a development decision and local evidence, not proof of portability to all native players or an independent security review. Native-player qualification remains outstanding as listed above.

Construction references: [Microsoft CBC encrypt-then-MAC guidance](https://learn.microsoft.com/en-us/dotnet/standard/security/vulnerabilities-cbc-mode), [RSA CSP encryption](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.rsacryptoserviceprovider.encrypt), [RFC 8017](https://www.rfc-editor.org/rfc/rfc8017.html), and [RFC 5869](https://www.rfc-editor.org/rfc/rfc5869.html). [Unity browser networking restrictions](https://docs.unity3d.com/6000.0/Documentation/Manual/webgl-networking.html) explain the separate raw-socket scope limitation.
