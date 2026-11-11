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
policy remains an adapter decision. Untrusted CA and mismatched client-key probes
are still required before completing qualification.

## Runtime and API evidence

Actual Windows process: .NET 10.0.14, SDK selected by global.json (10.0.401), WSLc
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
| A | Named Deployment create returns UID/RV; occupied name 409; original UID retained | Strengthen full unchanged-state assertion |
| B | UID+RV tests; keyed app at index 1; unrelated injected container/annotation preserved; stale RV/wrong UID 422 | Replace provisional stabilization delay with bounded deterministic observation |
| C | UID DELETE, separate GET 404, replacement protected with 409 | Namespace absence polling |
| D | Real bound token validates SA UID/namespace/Pod name+UID/audience; invalid/wrong audience/unbound deny | No owner-chain inference; no production policy implementation |
| E | Watch cancellation about 2 seconds; accepted marked POST response loss injected, one SDK attempt, exact UID/marker/state GET | Broader transport edge-case analysis |
| F | mTLS API access; bearer SelfSubjectReview exact SA identity; invalid bearer 401 | Additional TLS/credential negative probes |
| G | Actual Windows WSLc run, initially dirty working-tree evidence | Exact committed local and Linux CI execution pending |

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
