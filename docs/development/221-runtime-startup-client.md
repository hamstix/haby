# Mandatory runtime configuration before application startup

`Haby.Configuration.Client.RuntimeStartupClient` retrieves an explicit ordered set
of exact published document revisions through generated `DeliverRuntime` calls.
The caller starts primary application work only after the complete set returns.
The module is not packaged for public NuGet distribution and provides no SDK/API
stability promise during this alpha. Existing static `Deliver` clients are unchanged.

## Explicit bootstrap and channel ownership

Supply one binding ID, 1..64 unique canonical lowercase UUIDv7 document revision
IDs, a projected token file path, and an explicitly configured generated client.
Use an absolute local token path in deployments. There is no discovery, latest
lookup, registry credential reuse, Kubernetes client, authentication endpoint or
token exchange. Publication and binding establishment remain separate server work.

The caller creates, owns and disposes the channel, HTTPS endpoint and TLS trust.
Use system trust or explicitly configure the channel's certificate validation
against the intended trust roots. Do not disable certificate validation. Set
`MaxReceiveMessageSize` to 8 MiB. Supply a dedicated client without ambient
Authorization headers/call credentials/interceptors or configured gRPC retry and
hedging policies. An opaque generated client does not expose its channel settings
for inspection: this is a caller bootstrap responsibility. The helper owns and
disposes each unary call, never the supplied channel.

```csharp
using var channel = GrpcChannel.ForAddress(deliveryEndpoint,
    new GrpcChannelOptions { MaxReceiveMessageSize = 8 * 1024 * 1024 });
var generated = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
var startup = new RuntimeStartupClient(generated);
using var complete = await startup.FetchAsync(bindingId,
    new[] { exactSettingsRevisionId, exactOtherRevisionId },
    projectedTokenFile, cancellationToken: stoppingToken);

// Only now bind the complete in-memory documents and initialize primary work.
await InitializeApplicationAsync(complete.Documents, stoppingToken);
```

The compiled [consumer](../../examples/Haby.RuntimeStartup.Consumer/Program.cs)
demonstrates this boundary with two mandatory documents and a fixed primary marker.
Its command arguments contain only endpoint/binding/path/exact IDs. Token bytes
never enter arguments or environment variables. HTTPS uses system trust. Its
explicit loopback HTTP flag is for isolated controlled-endpoint tests; it does
not configure in-cluster trust or deploy anything. Application configuration
binding is application-owned, with no automatic appsettings mutation or
`IHostedService` ordering mechanism.

## Validated startup options and bounds

Options are a per-startup immutable snapshot. Invalid values are refused, not
silently replaced with defaults. Durations are finite `TimeSpan` values.

| Option | Default | Inclusive valid range |
| --- | --- | --- |
| `TotalTimeout` | 300 seconds | 1..3600 seconds |
| `RpcTimeout` | 30 seconds | 100 ms..30 seconds and <= total |
| `InitialBackoff` | 250 ms | 1 ms..5 seconds |
| `MaximumBackoff` | 5 seconds | initial..30 seconds |
| `MaximumAggregateBytes` | 16 MiB | 1 byte..64 MiB |

Fixed safety ceilings: at most 64 unique documents, each JSON at most 4 MiB,
complete protobuf response at most 8 MiB, and JSON depth at most 64 containers.
The aggregate limit covers accepted raw JSON, not every library/transport copy.
Aggregate checks subtract before copying so byte addition cannot overflow.
Replies must match the requested revision exactly, contain a nonempty source
fingerprint in the current `sha256:configuration-revision-v1:` plus 64 lowercase
hex format, and contain complete strictly encoded UTF-8 JSON. Comments, trailing
commas, malformed/trailing documents and invalid depth/size are refused.

The source fingerprint identifies the unresolved saved artifact. The client does
not calculate a digest from secret-bearing returned JSON or invent a bootstrap
fingerprint pin. Raw JSON bytes are retained without deserialize/reserialize or
floating-point conversion, preserving original number lexemes.

## One deadline and eligible retries

The monotonic total budget starts at entry, before validation. It includes file
work, all documents, server queue/RPC time, response validation/copying and retry
delays. Success of a document never resets it or causes earlier documents to be
refetched. Each attempt is capped by `min(RpcTimeout, remaining total)`, with both
an absolute RPC deadline and linked local cancellation timer. UTC clock changes
cannot extend the monotonic total. No new RPC begins after expiry.

Full jitter chooses a delay in `[0, min(exponential ceiling, maximum, remaining)]`.
The initial ceiling doubles with saturating overflow-safe arithmetic, resetting
per document while the total deadline remains shared. There are no server hints
or helper-configured transport retry/hedging policies. Zero jitter is allowed.

| Outcome | Action |
| --- | --- |
| `Unavailable` | Eligible temporary transport outcome within original total budget |
| `ResourceExhausted` + one `haby-runtime-reason: admission-overload` | Eligible admission retry |
| Individual RPC `DeadlineExceeded` or local RPC timeout | Retry only while caller and total remain live |
| `Unauthenticated` | Terminal `IdentityRefused` |
| `PermissionDenied` | Terminal `AccessDenied` |
| `FailedPrecondition` | Terminal `ConfigurationUnavailable` |
| `ResourceExhausted` + one `output-limit` reason | Terminal `OutputLimit` |
| Missing/unknown/duplicate/conflicting/binary reason or other transport status | Terminal `TransportFailure` |
| Caller cancellation | Terminal `CallerCancelled` |
| Total elapsed | Terminal `StartupDeadlineExceeded` |

The closed runtime-only reason trailer distinguishes delivery/authentication
admission from materializer/document/protobuf output bounds without changing
existing status codes or static `Deliver` trailers. No exception-message parsing
is used. `Unavailable` includes indeterminate TokenReview responses: eligibility
does not prove a valid credential or a temporary root cause. If caller cancellation
and total expiry are observed together, caller cancellation takes precedence.
Owned linked sources/timers are disposed; the caller's cancellation source is not.

## Read a fresh bounded projected credential before each attempt

Each attempt opens the original path again, reads at most 16386 bytes plus one
overflow probe, closes promptly, and sends exactly one Authorization Bearer
metadata value. No token bytes enter the protobuf payload. A token has 1..16384
ASCII bytes from `a-z A-Z 0-9 - . _ ~ + / =`. The file permits no terminator,
exactly one LF, or exactly one CRLF. BOM, lone CR, repeated terminators, embedded
whitespace/NUL and non-ASCII are refused as `InvalidCredential`. Missing/unreadable
files are terminal `CredentialUnavailable`; there is no credential-file retry.

File opens allow read/write/delete sharing to observe atomic replacement on
Windows. Symlink targets are resolved naturally on each open, never retained.
There are no cached handles, token watchers or token caches, and no helper writes,
chmod, renames or mount changes.

The supported input is an ordinary local regular file or projected-file symlink.
Synchronous open/metadata work runs on one owned read worker per active startup;
the startup awaits it only inside the same total/caller budget. If cancellation
wins while a read remains unsettled, startup terminates and never starts another
read/retry in that startup. A late observer closes/disposes through the worker and
clears any returned owned token bytes. This bounds caller waiting, not arbitrary
kernel or network-filesystem I/O termination. Unsupported devices/directories are
refused where identifiable; no remote-filesystem availability guarantee is made.

## Complete result, disposal and independent authority

`RuntimeStartupDocuments` is disposable and exposes only a complete set. Each
document owns its raw JSON array, exact revision and source fingerprint. Later
failure/cancellation clears all owned earlier/candidate document arrays and yields
only a closed `RuntimeStartupException.Failure`. No partial result reaches normal
startup. Success/repeated disposal clears owned arrays and subsequent access is
refused. Previously obtained memory views see cleared bytes; application-created
copies and managed strings/gRPC/protobuf copies cannot all be promised erased.

Do not print, trace or persist returned JSON, headers, tokens or connection strings.
Client failure wrappers retain no raw transport exception/inner exception/path.
Result/document diagnostics are fixed redactions. The sample reports only fixed
allowlisted reason/stage literals; stage markers indicate entered boundaries, not
root causes. No disk cache, offline fallback or automatic reload is provided.

Requests are independently authenticated and authorized, not an atomic server
snapshot or lease. A revoked later document can stop startup after an earlier
document was delivered. An already authorized request may finish after revocation;
revocation cannot erase already loaded application memory. Restart performs fresh
file reads and deliveries using the same explicit bootstrap, never stored config.

## Requirement-to-check map and evidence boundaries

| Requirement | Executable check |
| --- | --- |
| Included 1/10; A8 | Existing dependency tests plus small consumer in existing win/linux Native AOT matrix; published process test and native binary hash recorded by gate exporter |
| Included 2/6; A3 | `RuntimeStartupClientTests`: validated options, deterministic monotonic clock/timers/random, jitter/growth overflow, cross-document/read/RPC/delay total, remaining cap and cancellation |
| Included 3; A2/A5 | Real file ASCII/terminator/size/missing/directory cases, atomic replacement, Linux symlink-target replacement; unsettled-read finite caller wait and late disposal seam |
| Included 4; A5 | Wrong exact ID/fingerprint, strict UTF-8/JSON/trailing/depth/protobuf/aggregate limits and raw exact-number bytes |
| Included 5; A4 | Temporary/terminal status and reason table tests; actual Host lease wire regression, authentication/materializer/document outcome distinction, runtime/static protobuf guard and Host mapping |
| Included 7-9; A6/A7 | Owned buffers zeroed after later refusal/cancel, repeated/success disposal, no primary callback before complete set, restarted fresh calls; controlled consumer success/second refusal |
| A1/A2/A7 | Existing `RuntimeDeliveryCases` initial real API contour, checks S01..S08: two published documents, two genuine same-ServiceAccount Pod-bound tokens, first real TokenReview before controlled temporary failure, atomic file replacement, fresh real second-token reviews, restart and later-document revocation |
| A7/A8 | Existing full solution/PostgreSQL/Host/auth/publication/static/operator checks, both native targets and ordinary API qualification retained |

The real API contour observes token equality only transiently inside the existing
TokenReview transport seam; it never writes tokens into evidence. It deliberately
introduces a safe temporary failure after the first actual review/permission check,
then replaces a test-owned file. This proves filesystem replacement and fresh real
Pod-bound authentication, **not kubelet automatic projected-volume rotation**.
The API-server-only cluster has no kubelet. Owned Pods use UID-bound deletion and
private file fixtures are removed on every path. Result disposal itself is checked
with inspectable owned memory in deterministic tests.

`CliLoopbackTests.Runtime_startup_consumer_uses_generated_two_document_wire_and_never_starts_after_partial_refusal`
uses a controlled generated endpoint. Ordinary managed execution is distinct from
published Native AOT execution: native gates require the actual consumer binary,
launch it and record its hash. The managed test resolves the consumer's complete
own output using an explicit build-generated target path; the test output's copied
DLL alone can have an incomplete independent dependency closure. Neither controlled
endpoint path proves real TokenReview; S01..S08 supply that separate evidence.

Use current CONTRIBUTING solution/source-root commands, full hosted PostgreSQL
runner, both existing native gates and existing `-DurableActions` API launcher.
Keep case/parameter accounting complete. Record actual exact-source results,
failures, cleanup and unperformed gates separately; a listed check is not a passed
run. Existing failures are retained without unchanged automatic reruns.

The >=150-client/two-document rehearsal remains pending **after P4** under #217.
Capacity at that load is unverified. This slice provides no Deployment/projected
mount injection, workload execution/readiness/Applied, static secret projections
(#209), public SDK, offline cache or new authority. P3 remains incomplete.
