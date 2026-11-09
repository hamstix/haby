# ADR-045: Application generation updates and Resource continuity

- Status: Accepted
- Date: 2026-11-08
- Scope: Expanded P2 sequential Application updates before Workload implementation
- Extends: ADR-016/018/026/033/034/035/036/039/040/041/042/044
- Implementation: unchanged-Resource reuse in issue #158 and bounded mixed reuse/create in issue #160; [runbook and catalog boundary](../development/158-resource-reuse-generation-updates.md). The additive slice admits one independent standalone PostgreSQL database alongside the unchanged managed pair, with direct later provenance from its mixed creation generation. Changed Component/local Binding sets with unchanged managed Resources are implemented in issue #162; [target layout, assignment and cleanup boundary](../development/162-component-binding-generation-updates.md). Retained removal/compatible same-key reintroduction of the #160 standalone database, while the pair remains declared, is implemented in issue #164; [membership, original-source and cleanup boundaries](../development/164-retained-resource-reintroduction.md). Immutable typed generation membership is accepted atomically with the generation; stable management records and original evidence remain unchanged. Same-revision configuration-only generations are implemented in issue #166; [exact inputs, frozen defaults, explicit execution and storage boundary](../development/166-configuration-only-generations.md). One independent database/application-login pair per update is implemented in issue #168; [Resource-scoped producers, direct sources and alpha catalog boundary](../development/168-independent-database-login-pair.md). Broader Resource additions/removals and Resource identity changes remain planned.

## Context

The initial install/configuration/delivery path is proven. Useful deployment now
requires repeated releases, additions and removals of declarations, and frequent
logging configuration changes without unnecessary database or credential work.
These capabilities precede P3 Workloads. This decision fixes their semantics;
acceptance does not remove current pilot guards or implement them in one slice.

## Decision

1. Keep Import -> ChangeSet -> Plan -> Commit -> Execute, one Application per
   ChangeSet and sequential execution in one Haby instance. Deferred apply is not
   a prerequisite. Initial installation keeps ExpectedAbsence; updates require
   exact ExpectedGeneration(n), compared with current DesiredGeneration.

2. First admit a new generation only when DesiredGeneration equals
   AppliedGeneration. Imports may happen earlier. An incomplete/uncertain generation
   must be resolved before it can be superseded; no implicit cancellation or rollback.

3. ApplicationVersion and generation are distinct. A new ManifestRevision is a
   new target even if its normalized declaration is equivalent to the previous
   version. The same ManifestRevision can support a configuration-only generation.
   Retry/resume/observed state does not increment generation. Exact already accepted
   effective desired state is a successful no-op at its existing generation;
   Plan/ChangeSet identity, calculation time and actor alone are not desired changes.

4. Plan calculates a delta against a fixed accepted baseline, not a replay of the
   initial five operations. It records exact target inputs and explicit Resource
   decisions: reuse, create, retain after declaration removal, or unsupported
   change. Preserve immutable baseline and target references and reproducibility.

5. Resource identity remains EnvironmentRecordId + ApplicationRecordId +
   ResourceLocalId, independent of Component, ManifestRevision and generation.
   Moving Bindings does not create a Resource. A changed local ID denotes removal
   plus addition, never an implicit external rename.

6. Persist Resource management continuity across generations in shared
   Provider-neutral storage: stable owner, exact Provider binding, contract and
   accepted inputs, confirmed state/evidence references, and retained status.
   Adapter-specific data uses typed versioned payloads. No PostgreSQL database/
   role/host columns or Provider-specific tables in this common model. Removing
   a declaration must not lose the management record or ownership evidence.

7. Registered adapters compare new declarations with previous accepted inputs
   and confirmed state to decide compatibility/reuse. Common code does not infer
   Provider semantics. Finding an external object with a matching name is not
   evidence of Haby ownership and never grants automatic adoption.

8. First reuse unchanged Resources. PostgreSQL database, role, generated password
   and confirmed access policy remain unchanged. A version-only update does not
   repeat CREATE, generate credentials or reapply privileges without required work.
   Binding consumer options such as timeout are configuration inputs, not Resource
   mutations. This is not a general drift-repair or continuous-reconciliation claim.

9. Reuse is explicit provenance, not a fabricated successful external attempt.
   Bind the new generation to validated prior results, original producer/attempt,
   stable Resource owner and exact SecretRef. Do not rewrite old attempts/outputs
   or pretend CREATE ran again. Update output-consumer validation deliberately to
   recognize reuse provenance without weakening exact owner/purpose/evidence checks.

10. A generation may require no external operations. Commit still only accepts
    desired state; explicit Execute verifies completion requirements and advances
    AppliedGeneration. It does not automatically materialize documents or deploy
    Workloads. No fake no-op external operation is needed merely to mark progress.

11. New Resources use ordinary durable operations, dependency planning and
    conflict checks. Admit multiple Resources of supported contracts without
    automatically admitting every adapter policy. PostgreSQL's single database
    owner rule still applies; unsupported additional-role/access shapes refuse.

12. Removing a Resource declaration retains its external object, data, credential
    and management evidence. Record that it is no longer declared in the target
    generation. Validate remaining references; do not silently retain dangling
    dependencies. Retention is not deletion, permission revocation or forgetting
    ownership. Renaming does not transfer the old Resource to the new identity.

13. Reintroducing a retained stable Resource key is not a fresh installation.
    Consult its retained state and require adapter compatibility before reuse.
    Incompatible declarations refuse; do not regenerate passwords or adopt external
    names. The same exact Provider/evidence requirements apply to retained reuse.

14. Existing-Resource changes require explicit adapter support. Initially refuse
    database-name changes, login-to-database changes, ownership-policy changes,
    credential rotation and other unsupported mutations. Reuse initially requires
    the same exact Provider configuration revision, not only the same instance or
    alias. Safely rebinding existing Resources to a different Provider revision is
    separate work; alias switching cannot silently redirect reuse to another target.

15. Added Components/Bindings participate in the new configuration layout; removed
    ones do not. Remaining Components may keep using shared Resources. Declaration
    removal alone does not revoke old delivery grants or remove Kubernetes objects.
    Existing exact documents/grants remain valid until explicit revocation/expiry.
    Explicit Workload/grant cleanup is tracked as required P3 follow-up, not omitted
    indefinitely or implemented as a hidden side effect of a P2 update.

16. Configuration-only planning accepts changed supported Profile/default inputs
    for the same ManifestRevision through a new Plan and Commit. Freeze exact
    versions, effective values and provenance. Assignment/default edits alone never
    mutate deployments or old generations. Logging changes need not create an
    ApplicationVersion. This does not enable unimplemented parameter/Liquid execution.

17. After successful Execute, materialize exact new-generation documents explicitly
    using frozen inputs and confirmed outputs, including validated reuse sources.
    Old documents and grants never follow latest. Runtime hot reload and credential
    distribution/rollout remain separate; receipt of a new document is not proof
    that an existing process has switched to it.

18. Commit atomically verifies the expected deployment generation, Plan freshness
    and significant Resource baseline inputs. Generation mismatch is
    deployment-change-set-stale; changed significant planning/Resource inputs with
    still-valid intent are deployment-plan-stale. No hidden replan or replay.

19. Until full completion, AppliedGeneration stays at the previous successful
    generation. New effects may already exist after partial failure; the old
    AppliedGeneration is not a claim that infrastructure rolled back. Preserve
    successful results and uncertainty. Do not accept another generation over an
    incomplete one or blindly retry a post-dispatch external effect.

20. Changes to historical diagnostics, reset of existing catalogs, implicit adoption
    and destructive deprovisioning are not authorized. Follow ADR-033's one-current-
    model policy with explicit catalog implications for each implementation slice,
    never silent resets or historical success/evidence fabrication.

## Required evidence and implementation order

First implement cross-generation reuse of unchanged Resources, then additions and
Component/Binding changes with retained removals/reintroduction, then configuration-
only generations. Finish current ADR-044 runtime/diagnostic, Profile discovery and
Application-wide materialization slices before beginning this sequence. P3 follows.

Required real PostgreSQL/Host/client scenarios include v1 writes data -> v2 keeps
the database/role/credential/data and delivers new configuration; new Resources
without recreating old ones; Component/Binding additions/removals; retained removal
and compatible reintroduction; configuration-only updates; no-op; stale inputs;
repeat/restart/partial failure; and continued old exact-document access. Preserve
full Native AOT, authorization, output-owner/evidence and backup/restore checks.

Full Application/data deletion, arbitrary Resource migration, rotation, adopting
existing objects, multi-Application updates, superseding incomplete generations,
automatic materialization/access issuance and Kubernetes rollout are not part of
this accepted P2 capability. They require their own supported contracts and tests.
