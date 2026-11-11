# Kubernetes client qualification (#179)

Status: PARTIAL. The qualification tool is outside the production solution and has
no references from Domain, Application, Manifest, Host or habyctl. No production
capability, secret persistence, preflight or runtime identity binding is admitted.

## Evaluated candidate and binary terms

The provisional candidate is KubernetesClient **19.0.2**, net10.0, source commit
`f93cab47eb098242347d6f33752b304fd3bb9302`. The inspected downloaded nupkg's nuspec
specifies Apache-2.0. Its complete resolved dependency closure is Fractions 7.3.0
(the actual packaged license.txt is BSD-2-Clause) and YamlDotNet 16.3.0 (nuspec MIT,
no further dependencies). Preserve required notices when distributing binaries.
`tools/Haby.Kubernetes.Qualification/packages.lock.json` pins content hashes.
These terms permit commercial use subject to their notice/disclaimer conditions;
this does not establish support or legal approval for a downstream distribution.

Primary sources: [client source](https://github.com/kubernetes-client/csharp/tree/f93cab47eb098242347d6f33752b304fd3bb9302),
[package](https://www.nuget.org/packages/KubernetesClient/19.0.2),
[Fractions](https://www.nuget.org/packages/Fractions/7.3.0),
[YamlDotNet](https://www.nuget.org/packages/YamlDotNet/16.3.0).

KubeClient 3.1.1 is a plausible alternative with explicit credential configuration,
but its binary/transitive license qualification and real API execution are NOT RUN.
Its repository's MIT license alone is insufficient for selection. Raw HttpClient
would require maintaining our own Kubernetes protocol surface and is not selected.
No claim that the evaluated package is the latest: the gallery advertised 21.0.8;
NuGet API retrieval timed out locally, and the matching source tag was unavailable.
The available v21.0.7 source retains the TLS issue below. Version upgrades require
repeating qualification rather than extrapolating this result.

## Mandatory TLS configuration and reproduced defect

The stock 19.0.2 SslCaCerts path accepted an explicitly wrong TLS server name against
the real isolated API. The source callback returns chain validity without rejecting
a simultaneous RemoteCertificateNameMismatch. A failing real probe reproduced it.
The candidate is **not qualified using that stock configuration**.

ClientFactory instead leaves SslCaCerts unset and uses the public transport setup
hook with explicit custom-root chain validation, server-auth EKU and rejection of
missing certificates or hostname mismatch. Redirects are disabled. A repeated real
mismatch probe now refuses. This is qualification-only code, not a production fix.
Revocation checking is disabled for the disposable private CA; production revocation
policy remains an adapter decision. Real untrusted-CA and mismatched client-key probes now refuse through this same factory.

## Runtime and API evidence

Actual Windows process: .NET 10.0.14, resolved SDK 10.0.402 (global.json permits latest patch from 10.0.401), WSLc
3.0.5.0, WSL kernel 6.18.58.1-microsoft-standard-WSL2. Disposable API profile:
`rancher/k3s:v1.35.0-k3s1`, pinned manifest digest
`sha256:10464930d9bad0c06aef9830e84cd4019c24ed44d5eab594efb7416119097248`.
API reports v1.35.0+k3s1, git a6c6cd15c0c42ec9fce21f8ad5f42aa74fddb4f2.
The image labels identify Apache-2.0 and the k3s source revision. Full image component
license inventory remains pending; do not interpret that label as every component's
license. No Docker socket or privileged container is used on Windows.

The server runs with its agent disabled. Pods are real API objects with real UIDs
and TokenRequest binding, but no kubelet runs containers. This profile proves API
semantics only, not projected-file rotation, scheduling, rollout or readiness.
No arbitrary Kubernetes-version compatibility claim follows.

| Case | Actual local observation | Remaining qualification |
| --- | --- | --- |
| A | Named Deployment create returns UID/RV; occupied name 409; original UID retained | Strengthened assertion added; final-source execution pending |
| B | UID+RV tests; keyed app at index 1; unrelated injected container/annotation preserved; stale RV/wrong UID 422 | Bounded definitive-refusal stabilization added; final-source execution pending |
| C | UID DELETE, separate GET 404, replacement protected with 409 | Absence polling added; final-source execution pending |
| D | Real bound token validates SA UID/namespace/Pod name+UID/audience; invalid/wrong audience/unbound deny | No owner-chain inference; no production policy implementation |
| E | Watch cancellation about 2 seconds; accepted marked POST response loss injected, one SDK attempt, exact UID/marker/state GET | Broader transport edge-case analysis |
| F | mTLS API access; bearer SelfSubjectReview exact SA identity; invalid bearer 401 | Untrusted CA and wrong-key controls passed locally; final-source execution pending |
| G | Actual Windows WSLc run, initially dirty working-tree evidence | Initial committed Windows/Linux execution passed; updated final-source runs pending |

The SDK constructor does not install RetryDelegatingHandler. The accepted-response
loss probe injects failure after a real successful response; it is not a network
packet-loss simulation and does not prove every SocketsHttpHandler internal retry
condition. No application write retry is installed. Third-party serializers use
reflection; the managed qualification executable makes no Native AOT claim. Existing
Haby generated serialization and API-only Native AOT CLI are unchanged.

## Runbook

Prerequisites: .NET SDK from global.json; working WSLc on Windows or Docker on Linux;
free loopback port 16443; access to the pinned image and exact NuGet packages.

```powershell
dotnet restore tools/Haby.Kubernetes.Qualification --locked-mode
./scripts/run-kubernetes-qualification.ps1 -Runtime wslc
# Linux, same harness:
./scripts/run-kubernetes-qualification.ps1 -Runtime docker
```

The script generates a unique run identity, starts only its own container, copies
explicit CA/admin certificate/key into a private temporary directory, and connects
to explicit HTTPS loopback. It never reads user kubeconfig or installs trust roots.
The internal kubectl readiness check explicitly selects the generated container-only
kubeconfig. Credentials and token responses are never printed. Do not upload the
private directory, kubeconfig, binlogs or raw container logs. Only the named sanitized
text report is an allowed CI artifact. Namespace cleanup uses exact UID and label;
no finalizer is stripped. Container removal destroys this disposable API's storage.

## Validation status

Local solution restore/build passed (0 build warnings/errors); ordinary tests:
Domain 68, Manifest 282, Persistence 74, Application 493, CLI 338; all passed,
0 skipped. These ordinary tests do not replace measured PostgreSQL or published AOT.
Full measured PostgreSQL, published Windows/Linux AOT, dependency security audit,
formatting, Linux qualification and final SHA-bound local evidence remain pending.
ROADMAP is intentionally not marked complete. Independent review and a new
SHA-bound architectural decision are required; #178 approval does not apply.

## Alternative comparison and exact transport boundary

The alternative source inspected is dotnet-kube-client commit
`6dccfe7e6733a2401165e6068854cb852d72d507`:
[KubeClientOptions](https://github.com/tintoy/dotnet-kube-client/blob/6dccfe7e6733a2401165e6068854cb852d72d507/src/KubeClient.Core/KubeClientOptions.cs)
exposes endpoint, bearer, client certificate and CA explicitly.
DynamicResourceClient exposes cancellable JSON Patch. TokenReview models exist,
but equivalent end-to-end token binding, conditional delete, binary dependency
closure and negative TLS behavior were not executed. Its resource-watch helper
restarts successfully completed streams by default. Its project uses Newtonsoft,
Reactive and a larger dependency closure. This is a plausible candidate, not a
failed or proven-incompatible one. Prefer the official client's smaller evaluated
closure and generated typed API surface, **only with the tested strict factory**.
The selection does not claim a general benchmark or equivalent qualification of
all candidates. Source inspection does not establish a binary's license or behavior.

The factory's setup hook is a public Action<SocketsHttpHandler> in the exact selected
binary. With SslCaCerts absent and SkipTlsVerify false, initialization preserves the
installed callback. No ambient authentication helper, kubeconfig loader, exec
plugin, in-cluster discovery or global certificate callback is invoked. Credentials
are scoped to each disposable client. The mismatch, untrusted-root and mismatched-key
controls use the same construction path as positive API calls. Stock behavior and
strict-factory behavior must not be combined into a claim that stock TLS is safe.

Automatic SDK HTTP retry middleware is absent in the inspected constructor. Watches
are consumed using WatchListNamespacedPodAsync with the same cancellation token;
no retry loop wraps it. The only write retry in the fixture is a bounded positive
patch stabilization loop after a definitive 422 and an independently observed RV
change on the same UID. No timeout, transport exception or uncertain write retries.
The response-loss experiment records acceptance privately before throwing; its
separate GET is evidence inspection only, never adoption or production recovery.

For a future runtime-delivery slice, this agentless profile cannot supply running
recipients, projected token files/rotation, kubelet networking, readiness or 150-client
load. Those require a separately verified worker-capable isolated path. Nothing in
this qualification installs a system service, modifies firewall/trust or assumes
WSLc exposes Docker's API. Docker is used only by the Linux CI launcher.

Initial cross-platform evidence at source 331696efd665363d351eb1b42db71a150006c5a1:
Windows clean checkout passed; Linux qualification run
[43062134712](https://github.com/hamstix/haby/actions/runs/43062134712) passed on
merge ref f486b3d29e59836ce049183a9407e5ea266a88cb (.NET 10.0.15). Later qualification
workflow revisions explicitly check out the PR head SHA to avoid merge-ref ambiguity.
The initial Build run 43062134630 passed dependency review, validate and both
published Native AOT jobs; PostgreSQL was still running when this entry was written.
Local Windows AOT published with warnings-as-errors and 74 published loopback tests
passed with zero skips. Updated source still requires a final cross-platform run.
Exact local nupkg SHA-256 (nuspec and packaged-license inspection, not repository-only):

| Package | SHA-256 |
| --- | --- |
| KubernetesClient 19.0.2 | 89467e6ffea64dae1eeffd156a7b2d51f15b29efbf1f043aff36c0c44642126f |
| Fractions 7.3.0 | c2be08d668f5aab5e30f667775c6bbd0e05ed78cd93466a00cb5bdffcc50b425 |
| YamlDotNet 16.3.0 | e068bcc1243c46c8bfdfe2f27a026bfff03cde7c67d9f37c2cdd70bd24a9dfd4 |

Reproduce the stock TLS defect explicitly (expected nonzero, no capability admission):

```powershell
./scripts/run-kubernetes-qualification.ps1 -ProbeStockTls -EvidencePath .artifacts/stock-tls-negative.txt
```

Normal execution never selects this stock custom-CA path. The distinct failing
probe remains available so the diagnosis is reproducible rather than only a comment.
The scripted launch-failure control is synthetic and proves failure propagation /
sanitized report retention / private-directory cleanup, not any Kubernetes behavior.
Request surface (all names/namespaces are generated or fixed within the owned namespace):

| Purpose | API request |
| --- | --- |
| A | POST /apis/apps/v1/namespaces/{ns}/deployments; GET exact target |
| B | PATCH same target, application/json-patch+json, UID test then RV test then container-name test then image replacement |
| C | POST/DELETE/GET /api/v1/namespaces/{ns}/configmaps/{name}; DELETE body preconditions.uid |
| D | POST /api/v1/namespaces/{ns}/serviceaccounts/runtime/token; POST /apis/authentication.k8s.io/v1/tokenreviews |
| E | GET /api/v1/namespaces/{ns}/pods?watch=true with 60-second server bound and 2-second cancellation; marked ConfigMap POST and exact GET |
| F | HTTPS /version with mTLS; POST /apis/authentication.k8s.io/v1/selfsubjectreviews with bearer |

No credential values, JWTs, token-review bodies or headers are recorded. TokenReview
transport success is not the assertion: the harness checks authenticated, audiences,
username namespace, SA UID and the API-returned pod-name/pod-uid extra fields.
The unbound token may authenticate externally but fails the required Pod policy.
The temporary audience follows urn:haby:<installation>:<provider> with run-generated
IDs; these are test fixtures, not persisted installation or Provider records.
## Stable review handoff status

The next published revision adds strict untrusted-root and mismatched-key controls,
reproducible stock-TLS failure mode, explicit expected HTTP statuses, bounded
confirmed-refusal patch stabilization, namespace absence observation, watch event
confirmation, a synthetic launch-failure control, public-only qualification NuGet
sources and exact-head CI checkout. Some additions have not yet been executed as
one final-source run. This remains PARTIAL until those executions and required gates
complete. Source review may start on the published SHA with these pending gates.

The local full measured PostgreSQL run started at 331696e while qualification-only
files were changing. Its eventual sourceChangedDuringRun flag must be retained.
It is not final clean-source evidence. No production or integration-test source was
changed and no concurrent build was started during its test phase. Final CI must
run the complete gates against the stable published revision; earlier counts cannot
be attributed to that revision. No readiness or merge authorization is requested.