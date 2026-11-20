# Explicit Kubernetes Provider TLS policy

This revision implements [ADR-060](../adr/0060-explicit-kubernetes-provider-tls-policy.md)
for Kubernetes Provider access. The operator listener and application configuration
delivery transports are separate and unchanged. This source change is not a
statement that hosted qualification, installation or merge approval has completed.

## Declaration and defaults

Use the existing authenticated API-only Provider administration path. Within its
`spec.properties`, these are valid policy fragments:

```json
{ "endpoint": "https://api.example.invalid:6443", "tls": { "mode": "system", "revocation": "online" } }
```

```json
{ "endpoint": "https://api.example.invalid:6443", "serverCaPem": "<complete PEM CA bundle>", "tls": { "mode": "customCa", "revocation": "offline" } }
```

The PEM marker is explanatory, not a usable certificate. Continue supplying exact
Environment-owned ProviderAdministrator SecretRefs through the existing bearer
or mTLS slots; policy introduces no inline password, kubeconfig or ambient auth.

| Mode | CA bundle | Revocation |
| --- | --- | --- |
| `system` | Forbidden | `online` default, explicit `offline` or `noCheck` |
| `customCa` | Required, complete existing `serverCaPem` bundle | Same choices |
| `insecure` | Forbidden | Field forbidden |

When `tls` is omitted, no CA means system/online and a CA means customCa/online.
Omitted and explicit defaults normalize to identical saved policy and equivalent
configuration. When `tls` is present, `mode` is required. Unknown or duplicate
members, null policy/mode/revocation, unknown enum spellings, invalid PEM and
conflicting combinations refuse before material resolution or network I/O.
The endpoint remains an HTTPS origin without user info, query, fragment or path.

`noCheck` disables revocation only. It still validates trust, hostname, validity
and server-authentication usage. `offline` uses the platform cache; it never
fetches a missing revocation answer or silently becomes NoCheck. Unavailable
revocation fails closed. `online` permits bounded platform revocation retrieval;
it does not guarantee a response from every certificate's CRL/OCSP service.

`insecure` explicitly skips server certificate validation. Use only as a deliberate
operator choice; it is not a fallback. HTTPS and the exact selected bearer or
mTLS authentication remain active, API 401/403 still refuse, and every policy
refuses redirects and cross-origin credential forwarding. For example:

```json
{ "endpoint": "https://api.example.invalid:6443", "tls": { "mode": "insecure" } }
```

## Immutable revision and consumers

Apply a new Provider revision, run the existing exact-revision check and select
it through normal conditional alias preflight. Policy changes never mutate the
old revision or accepted generation. The codec persists normalized policy and
exact CA bytes in format 2 with exact credential versions, using generated JSON
metadata. System policy captures the OS trust-source choice, not a snapshot of
the operating system's trust store.

Provider check/alias preflight, Deployment execution and observation, runtime
identity execution/observation, recipient reads and TokenReview all use the same
production factory and accepted format-2 payload. Reloaded consumers retain the
frozen policy instead of reading a current alias or Host default. Client lease
failure does not select a weaker policy or retry with another credential.

Check/preflight results expose only closed safe connection diagnostics. A
certificate failure is classified as trust/name/time/revoked/revocation-unavailable
only where actual callback/chain evidence distinguishes it; otherwise it is
`tls-validation-failed`. DNS/connect use typed transport evidence. Raw exception
messages, endpoint configuration, certificate/CA bytes and credentials are not
operator diagnostics. Text and source-generated JSON output show the safe codes.
Explicit insecure check/preflight also carries the visible warning:
`Server certificate validation is explicitly disabled.` This warning is not a
successful authentication result or additional authority.

## Fresh catalog boundary

Kubernetes configuration format 1 is unsupported by this current alpha reader.
There is no compatibility reader, silent reset, migration, backfill or rewriting
of saved payloads/hashes, accepted Plans, receipts or ownership. Do not point the
new build at an active old catalog and assume it has been upgraded.

Before an explicit replacement, preserve/export the old catalog and history,
retain access to exact credentials, inventory managed external Resources and
active work, and establish their separately authorized lifecycle/disposition.
Keep the old installation available where required to finish active work; a new
empty catalog cannot adopt Deployments, ServiceAccounts, bindings or permissions.
An adoption/migration path needs its own accepted contract. Never erase an old
catalog or delete external state merely to make format-2 startup succeed.

## Local TLS evidence and platform limits

The focused tests use disposable loopback TLS endpoints, private test issuers and
an owned local CRL server. They do not need a Kubernetes cluster, PostgreSQL,
public CRL/OCSP availability or a new image/version/load matrix. Positive requests
prove actual HTTP authentication after TLS; a generic handshake failure alone is
not evidence that the intended certificate policy rejected a server.

Windows tests cover valid custom trust/NoCheck, wrong trust/name/time, unavailable
Online/Offline, a local CRL's valid/revoked Online result, cached Offline without
additional CRL fetches and explicit NoCheck. Unix/OpenSSL cache retention may
differ between fresh chain instances: Offline may accept a cached valid answer
or refuse as unavailable, but must not claim clean revocation when unknown.
The tests enforce that boundary and no-fetch behavior. A local Windows pass is
not evidence of Linux hosted execution; record each actual platform separately.

Windows Schannel requires a platform key association for mTLS. The production
factory loads its private key with temporary default key storage, without
PersistKeySet; certificate lease disposal releases the association. Intermediate
PKCS#12 and resolved byte buffers are erased. Runtime/OS failure or hard process
termination is not a promised global key-store cleanup proof. No trust root is
installed system-wide by these tests. The third-party Kubernetes SDK remains a
managed server-side dependency; Haby-owned payloads and CLI diagnostics use
generated metadata and must retain Native AOT validation.

Focused commands (PowerShell):

```powershell
$env:HABY_TEST_SOURCE_ROOT = (Get-Location).Path
dotnet test ./tests/Haby.Kubernetes.Tests/Haby.Kubernetes.Tests.csproj --filter 'FullyQualifiedName~Tls|FullyQualifiedName~AccessConfigurationTests'
dotnet test ./tests/Haby.Cli.Tests/Haby.Cli.Tests.csproj --filter FullyQualifiedName~ProviderTlsPresentationTests
```

These do not replace required solution checks, PostgreSQL, published Native AOT
or applicable Kubernetes qualification before merge. Full hosted campaigns need
their separately coordinated invocation; do not rerun unchanged failures or add a
parallel infrastructure qualification project. Preserve exact-SHA review and the
required hosted-failure STOP rule.
