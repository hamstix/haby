# Kubernetes client qualification (#179)

Status: API-only qualification completed and merged in [PR #180](https://github.com/hamstix/haby/pull/180), merge `252646670cdea275789485b5192a0d78efb2ea00`. The historical correction/evidence sequence below is retained. The qualification tool is outside the production solution and has
no references from Domain, Application, Manifest, Host or habyctl. No production
capability, secret persistence, preflight or runtime identity binding is admitted.

## Evaluated candidate and binary terms

The qualified candidate is KubernetesClient **19.0.2**, net10.0, source commit
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
policy is now implemented separately by the [#200 adapter kernel](200-deployment-kernel.md): Online revocation fails closed for system and custom-root trust. The qualification factory remains a disposable-CA fixture. Real untrusted-CA and mismatched client-key probes refuse through this same fixture factory.

## Runtime and API evidence

Actual Windows process: .NET 10.0.14, resolved SDK 10.0.402 (global.json permits latest patch from 10.0.401), WSLc
3.0.5.0, WSL kernel 6.18.58.1-microsoft-standard-WSL2. Disposable API profile:
`rancher/k3s:v1.35.0-k3s1`, pinned manifest digest
`sha256:10464930d9bad0c06aef9830e84cd4019c24ed44d5eab594efb7416119097248`.
API reports v1.35.0+k3s1, git a6c6cd15c0c42ec9fce21f8ad5f42aa74fddb4f2.
The image labels identify Apache-2.0 and the k3s source revision. Full image component
license inventory is NOT PERFORMED; do not interpret that label as every component's
license. No Docker socket or privileged container is used on Windows.

The server runs with its agent disabled. Pods are real API objects with real UIDs
and TokenRequest binding, but no kubelet runs containers. This profile proves API
semantics only, not projected-file rotation, scheduling, rollout or readiness.
No arbitrary Kubernetes-version compatibility claim follows.

| Case | Actual local observation | Final qualification disposition |
| --- | --- | --- |
| A | Named Deployment create returns UID/RV; occupied name 409; original UID retained | Final clean e94de432 Windows/Linux passed; PR #180 merged |
| B | UID+RV tests; keyed app at index 1; unrelated injected container/annotation preserved; stale RV/wrong UID 422 | Final clean e94de432 Windows/Linux passed; PR #180 merged |
| C | UID DELETE, separate GET 404, replacement protected with 409 | Final clean e94de432 Windows/Linux passed; PR #180 merged |
| D | Real bound token validates SA UID/namespace/Pod name+UID/audience; invalid/wrong audience/unbound deny | No owner-chain inference; no production policy implementation |
| E | Watch cancellation about 2 seconds; accepted marked POST response loss injected, one SDK attempt, exact UID/marker/state GET | Broader transport edge-case analysis |
| F | mTLS API access; bearer SelfSubjectReview exact SA identity; invalid bearer 401 | All three strict TLS controls passed on reviewed f31a112 |
| G | Actual Windows WSLc run, initially dirty working-tree evidence | Final clean e94de432 Windows/Linux and corrected launcher scenarios passed; PR #180 merged |

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

At reviewed source f31a11277b78f2ea267bd9771ba153f682e2f3d3, clean Windows and
Linux A-G execution and the separate expected stock TLS failure passed. Build run
[43064761342](https://github.com/hamstix/haby/actions/runs/43064761342) passed all
jobs (synthetic merge e977b680de58e28000a0d4ddb49c3d4f9fe4c412); qualification run
[43064761469](https://github.com/hamstix/haby/actions/runs/43064761469) checked out
that exact PR head. Ordinary tests passed 1,255 cases, zero skips; Windows/Linux
published Native AOT each passed 74 cases with warnings-as-errors publication.
Full measured PostgreSQL passed 778/778, zero skips, test time 1140.6417616 s,
wall time 1171.6486064 s, sourceChangedDuringRun=false. Dependency/security and
format gates passed. These results do not cover later launcher corrections.

The supplemental local full PostgreSQL run started at 331696e and completed with
774 passed, four failed, zero skipped; sourceChangedDuringRun=true, test time
1339.0708969 s. Three failures included local socket-address allocation errors;
one was RPC Internal. Five cases across the four methods passed a fresh targeted
rerun. This does not replace the complete CI run or prove an environmental cause.
No production or integration-test code was changed to obtain a green run.

That intermediate review did not complete qualification. Final correction evidence
and renewed independent review were subsequently accepted for PR #180 at
`e94de4322e85ac7e29abedfa84f3b225fb8d09fe`; the PR is now merged. Historical failed
and partial runs above remain failed/partial. #178 architectural approval alone
did not cover qualification.
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
## Launcher review corrections

Independent review of f31a112 identified F1-F4. Focused behavioral controls first
failed against that launcher: anonymous-volume retention, private-directory loss
of cleanup after inspection failure, partially created container retention, and
an unbounded readiness child. The corrected controls pass, and remain distinct
from real Kubernetes evidence.

- F1: inspect the exact run label and container ID, capture only its image-created
  anonymous volumes, remove with --force --volumes, and independently observe
  container and each exact volume absent. Never prune or remove other volumes.
- F2: independently guard container/storage cleanup, private-key-directory cleanup
  and sanitized evidence writing. Failure in any stage fails the run without
  suppressing subsequent cleanup. Permission setup is inside the protected scope.
- F3: record launch attempt before invoking the engine; inspect after any launch
  outcome. A successful bounded inventory distinguishes absence from an unavailable
  daemon. Cleanup refuses an ownership mismatch. Port-collision behavior differs:
  WSLc leaves no container; Docker can leave an unstarted container and volumes.
- F4: each child process has a deadline and only its owned process tree is killed.
  Readiness combines kubectl request timeout with a total stopwatch budget and a
  remaining-budget child deadline. Cleanup and output draining are also bounded.

Run synthetic failure controls and the real four-scenario rehearsal:

```powershell
./scripts/test-kubernetes-qualification.ps1
./scripts/test-kubernetes-qualification-real.ps1 -Runtime wslc
# Linux CI runs the same real rehearsal with -Runtime docker.
```

The real rehearsal covers normal A-F, the expected stock TLS failure, port conflict,
and readiness failure. It verifies private cleanup and owned storage absence after
each, while a separately labeled control volume remains intact. The rehearsal
removes that exact control volume at the end. Only four explicitly named sanitized
reports are uploaded. Precommit Windows execution passed all four scenarios;
final clean-source Windows/Linux evidence was subsequently bound to PR #180's
`e94de4322e85ac7e29abedfa84f3b225fb8d09fe` (qualification run 43072650986).
Earlier runs used plain container removal and may have left anonymous volumes;
unknown historical volumes must not be guessed or globally pruned.

## Runtime image scope

The upstream-pulled test image is not distributed by Haby. Its full image component
inventory is NOT PERFORMED, not a claim of complete Apache-2.0 coverage. Mandatory
commercial qualification covers the selected client and resolved transitive
packages above. Image identity is linux/amd64: manifest index
10464930d9bad0c06aef9830e84cd4019c24ed44d5eab594efb7416119097248,
platform manifest 56b7147bf4430ede74e3d658e8bdabe767ebd4a9ea295fc271b05e66eba4de24,
config 263d3e3f948d5e1d785be3581af2921b2ba71ebda4bdeef778f0f4edd758c87f.
Upstream source and notices: [k3s revision](https://github.com/k3s-io/k3s/tree/a6c6cd15c0c42ec9fce21f8ad5f42aa74fddb4f2)
and [license](https://github.com/k3s-io/k3s/blob/a6c6cd15c0c42ec9fce21f8ad5f42aa74fddb4f2/LICENSE).
No worker, projection/rotation, readiness, scheduling or load qualification follows.
### F5-F6 follow-up corrections

F5 uses one shared resolver selecting the first application path, including when
both /usr/bin and /bin expose Docker. Docker inventory uses ps; WSLc uses list.
F6 replaces the any-exception negative oracle with exact invocation identity,
stage sequence, failure reason/category, and independent cleanup requirements.
The port scenario must arrange its listener and fail specifically at launch,
without readiness or harness entry. Readiness must reach its real probe and
exhaust the configured three-second budget without reaching copy or harness.
An early child timeout is distinct from exhausting that overall budget.

The previous oracle accepted a harmless fixture representing an unrelated copy
failure, a late failure and an old report. Focused red checks reproduced these
false positives before correction. Current controls reject those records, missing
probe/listener setup, premature timeout, unexpected harness entry and cleanup
failures. Injected ACL/launch/copy failures through the actual launcher are also
rejected as readiness evidence. Existing report files are refused before launch
and preserved; report creation uses CreateNew. Each rehearsal uses a unique
run directory and each report carries a fresh evidence ID and container run ID.
The four explicit report filenames under .artifacts/qualification-evidence/
haby179-control-*/ remain the only uploaded artifacts. No credentials are placed
in that evidence directory. Clean-source Windows/Linux reruns and refreshed
exact-SHA independent review were completed before PR #180 merged. This history
does not qualify the production adapter added in #200; that kernel has separate
tests and reuses the owned Docker launcher in `-DeploymentKernel` mode.
