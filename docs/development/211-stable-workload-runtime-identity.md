# Internal stable Workload runtime identity

Issue #211 prepares a ServiceAccount and immutable RuntimeIdentityBinding after
complete exact #208 configuration preparation. It remains friend-only: no Host
registration, RPC, CLI switch, configured Deployment execution or public Workload
admission is added. Binding status `Prepared` grants no configuration access.
Exact friend-only publication and terminal binding revocation are described in the
[issue #213 guide](213-runtime-configuration-publication.md). TokenReview delivery,
Pod injection, readiness, AppliedGeneration and
#209 static Profile secret execution remain separate capabilities.

## Stable ownership and exact authority

A managed Workload has one persisted ID for Environment/Application deployment +
Component + Workload. Generation, operation and attempt IDs do not participate in
that key. New internal Deployment intents also use it; accepted markers, selectors
and receipts are never rewritten. Exact Provider identity, namespace and baseName
are immutable adapter target constraints. A changed target refuses without rename,
relocation, adoption or object replacement. The existing planner validates complete
names including `-runtime`, nameOverride and whole-generation object collisions.

`InternalRuntimeIdentityPreparation` derives all configured recipients from the
actual accepted Commit. Every selected artifact and recipient association must
already exist and match the frozen declaration, Plan/revision, ResourcesReady
checkpoint, full input fingerprint and original audit. Current Resource output and
credential owner/purpose metadata are checked again. Missing/partial/foreign
preparation refuses; this use case never materializes documents or repairs missing
associations. A Workload without configuration selection has no runtime binding or
Haby ServiceAccount preparation. Ordinary P2 document eligibility remains unchanged.

The neutral `managed_workloads`, `owned_objects`, `runtime_identity_bindings` and
`installation_identity` concepts contain generic identities and versioned envelopes.
Kubernetes namespace/name/UID, exact ownership/write markers, disabled automount and
validation context remain adapter-owned source-generated payloads. No Kubernetes
SDK type or Provider-specific catalog column enters shared persistence.

## Dispatch, confirmation and recovery

The common durable intents/attempts/outputs/observations journal supplies the
immutable CREATE key, marker-before-call barrier and separate uncertain observation.
No second journal or scheduler exists. The account has `automountServiceAccountToken=false`.
The action creates no RoleBinding, token Secret, Deployment, Service or Ingress.

Catalog transactions/connections end before secret resolution or API work. A named
CREATE checks name absence and sends at most one POST. An occupied name refuses,
even if its labels look familiar. Successful confirmation atomically accepts the
validated receipt, common operation result, owned inventory and binding. The pure
adapter projection has no DbContext, callback or network access.

If the response or confirmation commit is lost, restart observes the original
intent. Only matching exact owner/operation/projection can learn the first UID.
Observation retains the original Uncertain attempt and appends separate evidence;
absence or unavailable observation never restores dispatch eligibility. A crash
between object confirmation and binding commit resumes by observation, never by
another CREATE. Known-account replay validates the saved UID/projection read-only;
missing objects and replacements refuse without recreating or remapping a binding.

Equivalent calls preserve the original binding and account. Trusted constraints
(Environment, ManagedWorkload, Provider/configuration, authenticator, audience,
namespace and UID) cannot mutate the old binding or select current aliases. There
is no identity-transition/adoption API. Binding revocation preserves the immutable
proof while disabling preparation and runtime permissions; replay cannot change
Revoked back to Prepared. Current internal authority still admits
only genuine initial or real Resource-baseline mixed generations. This slice does
not manufacture a prior successful Workload generation or claim an executed v1/v2
rollout. Future supported generation continuation must keep current-call authority
separate from the immutable original CREATE receipt.

## Required permissions and TLS

Before account CREATE, the action performs SelfSubjectReview, scoped
SelfSubjectAccessReview checks and reads the exact selected active namespace.
Credentials require `create` on `authentication.k8s.io/selfsubjectreviews` and
`authorization.k8s.io/selfsubjectaccessreviews`, `get` on the selected core namespace,
and `get`/`create` on core ServiceAccounts in that namespace. GET reviews include
the exact object name; CREATE is checked at namespace scope. These ephemeral review
requests create no persistent probe object. No future Deployment or TokenReview
permission is claimed. #204 access-only preflight is insufficient for these rights.

Use existing exact Environment-owned ProviderAdministrator SecretRefs and the
strict explicit HTTPS factory. Production hostname/chain/usage/Online revocation
checks remain fail-closed. The disposable launcher uses the existing friend-only
fixture revocation seam for its private CA; positive fixture evidence does not
prove successful production revocation. No raw credential or API body is persisted.

## Installation identity, restore and alpha compatibility

The catalog singleton initializes once under an installation-wide transaction lock,
including concurrent calls from different deployment locks. Audience is exactly
`urn:haby:<installation-id>:<provider-instance-id>` with canonical UUIDs. Host URLs,
Environment names and process restarts cannot determine or regenerate it.

An in-place current-build backup/restore preserves installation identity, audience,
receipts and bindings. An independently active clone requires a future explicit
identity/binding rollout; startup never silently regenerates identity. Copying a
catalog does not create an independent owner of external resources.

The additive migration accepts empty catalogs and valid earlier P2-only history.
Before changing schema/data, the existing initializer checks the validated old
migration prefix for superseded per-intent Workload state. Such catalogs require
an explicitly initialized fresh catalog under ADR-033. No implicit backfill,
attachment or adoption is performed. Runtime acceptance/loading also refuses
unbound old Workload intents if initialization was bypassed. External objects are
unchanged by catalog refusal/reset; their disposition requires a separate explicit
operator decision. Never reset a persistent catalog automatically.

The trust boundary is per Workload ServiceAccount, shared by replicas and later
generations. It is not per-generation Pod isolation. Old Pods may become eligible
for later explicitly published revisions. Neither document materialization nor
binding persistence publishes any permission; existing P2 grants remain independent.

## Validation map and commands

| Acceptance | Evidence obligations |
| --- | --- |
| A1, A7 | Actual initial/mixed accepted preparation, complete selected artifacts, no-configuration/foreign refusal, no grants or Applied change |
| A2, A6 | Real PostgreSQL concurrency, singleton first-use races, current-build dump/restore and original identity/audience |
| A3, A9 | Real API response loss/restart, marker-before-send absence, prebinding transaction rollback and observation-only recovery |
| A4, A5 | Exact marker/UID/projection conflicts, occupied and missing objects, planner suffix/collision checks, real disabled automount/no RBAC/token Secrets |
| A8 | Real namespace/permission refusal, fast cancellation/authentication controls and retained production/fixture TLS separation |

Use CONTRIBUTING solution commands and the existing hosted full Docker PostgreSQL
gate. Extend the existing durable API contour without another runtime or harness:

```powershell
dotnet restore tools/Haby.Kubernetes.Qualification --locked-mode
./scripts/run-kubernetes-qualification.ps1 -Runtime docker -DurableActions -EvidencePath .artifacts/runtime-identity/evidence.txt
```

D01-D18 retain their original obligations. D19-D32 add ServiceAccount/binding API
and catalog cases. PostgreSQL fixture tests use separate arrange/act/assert contexts;
their synthetic action transport proves persistence, while the real launcher proves
actual API effects. Ordinary fast, full PostgreSQL, existing API and both published
Native AOT gates remain required. Preserve failed results and scoped cleanup;
exact-source executed results belong to task/CI evidence, not a readiness claim.
