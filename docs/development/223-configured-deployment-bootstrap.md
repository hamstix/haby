# Internal configured Deployment submission

Issue #223 extends the friend-only initial Deployment CREATE seam. It is not a
public operator execution command, readiness observer or AppliedGeneration gate.
The accepted Workload-only empty Resource stage and existing unchanged-Resource
baseline shape remain the only admitted shapes. Configured UPDATE, companion
objects, arbitrary container settings, #209 static secret projections, retirement
and the post-P4 load rehearsal remain unsupported.

## Frozen delivery contract

The exact selected KubernetesPlacement revision may contain
`spec.runtimeConfiguration`. It is required for a configured executable Workload;
an absent configuration block creates no runtime identity or token/bootstrap.
Invalid present Placement fields refuse even when currently unused. There is no
Workload override, Host-address fallback or implicit cluster loopback endpoint.

```json
{
  "runtimeConfiguration": {
    "endpoint": "https://configuration.example.test:7443/",
    "tls": { "mode": "system" },
    "client": {
      "totalTimeoutSeconds": 300,
      "rpcTimeoutMilliseconds": 30000,
      "initialBackoffMilliseconds": 250,
      "maximumBackoffMilliseconds": 5000,
      "maximumAggregateBytes": 16777216
    }
  }
}
```

Objects are closed; unknown/duplicate/null fields refuse. Endpoint and TLS are
required. Endpoint is an explicit ASCII HTTPS origin with optional port, root path
only, at most 2048 UTF-8 bytes, without userinfo/query/fragment/encoded components.
Loopback, localhost and unspecified addresses refuse. Planning performs no DNS or
network lookup; operators must provide an address reachable from their Pods.

TLS is either `{"mode":"system"}` or
`{"mode":"custom-ca","caPem":"<public certificate PEM>"}`. Custom trust accepts
1..8 distinct CA certificates, at most 16384 UTF-8 bytes, with no other PEM labels,
private keys or trailing non-whitespace. Only those custom roots are trusted.
Both modes verify hostname, validity and server-auth chain. Redirects, ambient
credentials/proxy, server-name overrides and public bypass switches are absent.
System trust preserves the existing gRPC transport's platform policy. The new
custom-root chain explicitly uses NoCheck for revocation and disables certificate
downloads; this provides no online-revocation proof or CRL requirement. The
Kubernetes administrator/API transport retains its independent Online policy.

Client settings are optional at import; absent values obtain the defaults above
once and are frozen. Integer ranges are total 1..3600 seconds; RPC 100..30000 ms
and no longer than total; initial backoff 1..5000 ms; maximum backoff initial..30000
ms; aggregate bytes 1..67108864. They map directly to #221 TimeSpan/byte settings.
Executable preparation accepts 1..64 mandatory documents per Workload, retaining
the 512 recipient/document-pair bound. The portable 128-item declaration is not
silently truncated. Plans freeze names/settings, not fabricated future UUIDs.

## Exact non-secret bootstrap and Pod fields

After genuine durable preparation, the configured main container receives one
literal `HABY_RUNTIME_BOOTSTRAP` environment value. It is format-1 compact JSON,
at most 64 KiB and depth 8, with required fields:

- `format`: 1;
- `bindingId`: exact canonical UUID;
- `documents`: ordinal Component-local name to exact canonical UUIDv7 revision map;
- `projectedTokenPath`: `/var/run/haby/runtime/token`;
- `delivery`: closed endpoint and TLS objects from frozen Placement;
- `client`: the complete effective integer settings listed above.

The neutral codec is in Haby.Configuration.Contracts. The application owns its
dedicated channel, calls the existing RuntimeStartupClient for every selected
revision and associates complete results by exact revision ID, never response
order. Only then may application binding/primary work begin. No current/latest
selection, token exchange, offline cache or configuration disk writes are added.

Pod serviceAccountName is the confirmed stable account, with explicit automount
false. The exact account UID remains durable validation evidence; PodSpec cannot
pin a UID. One `haby-runtime-token` volume projects exactly one token source with
saved audience, expirationSeconds=3600, path `token` and explicit defaultMode 0644.
Only the exact configured containerName mounts it at `/var/run/haby/runtime`,
readOnly=true, without subPath/subPathExpr/mountPropagation. Actual token lifetime
and issuance belong to Kubernetes. No RoleBinding or static token Secret is added.

The variable/volume name and mount directory are reserved for configured subjects.
User collisions and overlapping mounts refuse before preparation writes; user
settings are never overwritten. Unsupported ordinary env/ports/probes/mounts and
resource/companion settings stay unsupported. Receipt/response/observation checks
cover every new field, keyed-list ambiguity, additional projected sources and
reserved-volume injection into another container. Unrelated metadata/injections
remain outside Haby ownership. No secret/token/document body enters Plan, intent,
receipt, Deployment, ordinary ConfigMap or diagnostics.

The example consumer's zero-argument mode reads this environment value and uses
the same #221 helper. Its test image has a fixed entrypoint; no command/image
passthrough contract is introduced. It requires `other` and `settings`, prints a
fixed primary marker after complete retrieval and then remains running. Existing
controlled argument-mode tests remain distinct.

## Internal callable order and current authority

Trusted friend composition invokes the actual accepted Commit, then
InternalWorkloadConfiguration, InternalRuntimeIdentityPreparation,
InternalRuntimePublicationPreparation, and InternalDurableActions.AcceptAsync /
ExecuteAsync for the exact InternalDeploymentAction CREATE contract. The latter
reconstructs complete artifact/recipient, ResourcesReady, identity origin and
publication receipt evidence from the catalog. Caller-built envelopes or IDs are
not authority. Nothing registers this executor with Host/RPC/CLI; remaining
operator integration and full lifecycle need later slices.

Configuration requirements, identity establishment and publication requirements
are separate neutral capabilities. Only identity establishment creates a binding;
Deployment inventory uses the separate generic `deployment` role. The binding,
account, document revisions and original publication intents survive restart.

Under the existing deployment lock, new acceptance/preparation and each new marker
check the complete set of current Prepared binding/Published permissions, including
origins and receipts. Old publication replay cannot repair revoke; only valid
explicit republish can restore rights. After marker/dispatch, immutable proof and
read-only observation remain available despite revoke, and truthful confirmation
of an already-sent effect may be recorded. No additional write follows uncertainty.
Delivery still authenticates and authorizes every request freshly.

Marker-to-network and account-GET-to-CREATE are non-atomic windows; no catalog
transaction spans network work. A concurrent account replacement cannot be pinned
in PodSpec; TokenReview rejects its different UID. Revocation cannot unsend an
already-sent write or erase application memory. Submitted is not ready or Applied.

Deployment intent/receipt payloads are format 2 with versioned input/output
contracts. Historical v1 Deployment actions cannot continue at action loading;
there is no legacy reader, backfill or blanket Host-start refusal. An installation
that needs to continue those obsolete actions must explicitly initialize a fresh
development catalog and separately account for existing external objects. Placement,
ServiceAccount and publication formats remain supported. No automatic reset occurs.

## Evidence and limits

Fast tests cover closed parsing/TLS negatives, exact maps, absent configuration,
format refusal and each owned runtime field. Twelve persisted cases cover real
accepted initial/mixed authority, original ApplicationLogin metadata/redaction,
selection changes, restart/restore, missing/revoked rights and post-dispatch
confirmation/observation. Arrange/act/assert use separate DbContexts.

The existing Linux Docker contour has one opt-in runnable-node profile:

```powershell
dotnet restore tools/Haby.Kubernetes.Qualification --locked-mode
./scripts/run-kubernetes-qualification.ps1 -Runtime docker -ConfiguredDeployment -EvidencePath .artifacts/configured/evidence.txt
```

The test consumer is published separately to public GHCR, once per actual input
set. Ordinary runs resolve a reviewed source-to-digest binding under
`tests/fixtures/runtime-consumer-images/<input-key>.json` and the same pinned k3s
node pulls that immutable digest normally. No binding is fabricated before the
publisher proof and anonymous access are independently verified; a missing or
mismatched binding stops before node launch. The fixture still copies its CA,
administrator certificate/key and freezes its reachable HTTPS IP/SAN before
planning. No Pod registry credentials, new registry platform, CRL service or global
trust-store mutation is introduced.
The production action creates the target Deployment; handcrafted target YAML does
not substitute. C01-C11 cover genuine two-document startup, Host restart/fresh Pod
with unchanged bootstrap, later-document revoke/no-primary and production
missing/revoked admission refusal. No catalog deletion manufactures cluster
permission absence. Existing API-only and lost-response cases remain separate.

Natural kubelet rotation is not proven by a short startup. #221's controlled file
replacement remains separate evidence and is never labeled automatic rotation.
Primary marker is functional evidence, not rollout readiness. Existing launcher,
job and API budgets remain; owned namespace/catalog/container/volume/private paths
must be cleaned and failures retained separately. No unchanged green-seeking rerun.

The fixed image graph is Consumer -> Configuration.Client -> Configuration.Contracts,
including proto, SDK-generated locks, relevant shared build settings and effective
package versions. The pinned SDK/runtime Dockerfile builds a content-only context,
without Git metadata or ambient credentials. Client/configuration changes invalidate
the key; unrelated docs/tests and unused central versions do not. Unknown graph or
conditional imports refuse. The publisher has only contents:read/packages:write;
ordinary tests never build, save, copy or import a consumer archive and never publish.
Secure fixture certificate/key copies and their failure cleanup remain mandatory.
The initial package-creation confirmation is false; package rights/public visibility
are prerequisites, not presumed from repository visibility. A 404 is ambiguous
unless first creation has been explicitly confirmed. Other registry/auth failures,
inconsistent accepted proof and uncertain pushes stop without repair or retry.
Only independently reviewed publication artifacts may create the committed binding,
which includes inputKey, digest, builtSource, publisherSource, publisherRunId and
proofArtifactSha256. Labels alone are not publication proof. No accepted binding is
included before an actual publisher release. Historical native-import failures
remain undiagnosed and are not acceptance evidence.

Final durable journal boundaries cover revoke, retained history, final
authorization, candidate disposal and inner cleanup entry/outcome. They are progress
evidence, never passing assertion receipts. Existing startup, command and cleanup
budgets remain unchanged; distinct parent and managed clock origins are not mixed.

The inventory adds 59 fast, 12 PostgreSQL and 11 consumer assertion rows. Four
setup failure/cancellation controls invoke the same initialized fixture lifetime
without opening a database or API; they separately retain primary and cleanup
failures. Fixed launcher copy-operation markers distinguish command outcomes
without publishing private arguments, paths or process output. Two
existing fast identities are explicitly replaced for format-2 invalid-version
and verified-preparation naming; no coverage obligation is removed. Required
solution, full PostgreSQL including infrastructure, qualification/kernel/durable,
dependency, formatting/diff and both published Native AOT gates remain. Inventories
are not passed executions: exact-source results, failures, cleanup and unperformed
checks belong in run evidence. No >=150-client/load or P3-completion claim.
