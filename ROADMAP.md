# Haby delivery roadmap

This roadmap tracks usable outcomes. The initial manifest foundation has been
delivered; configuration and bounded update/naming outcomes are established in P2.
The current focus is P3 public wait/resume and lifecycle completion after bounded
operator Workload submission.

The delivery stages below reorganize existing commitments. They do not change
accepted ADRs, stabilize alpha contracts or claim general production readiness.
There are no calendar estimates. Each stage is delivered through narrow issues
and independently reviewable PRs.

## Current position

| Delivery milestone | Status | Observable outcome |
| --- | --- | --- |
| P1 - Resource provisioning pilot | Delivered within the isolated PostgreSQL pilot | Prepare an Environment, import an Application, Plan/commit/execute, create its database and generated application login, and grant database-owner access. |
| P2 - Application configuration | Functionally delivered within the bounded PostgreSQL profile | Generate exact configuration documents from applied Resource outputs and shared Profiles; let an explicitly authorized application client retrieve and use them. |
| P3 - Workload lifecycle | In progress: internal durable CREATE, configuration delivery and readiness; operator desired-state admission and bounded public submission delivered | Deploy an Application to Kubernetes, deliver its configuration, update it and safely remove its managed workload objects. |
| P4 - Standard Providers | Planned | Add RabbitMQ, Redis and HTTP/auth integrations and extend PostgreSQL through complete tested application scenarios. |

P1 is a working checkpoint, not a claim that configuration delivery, all
Providers, general recovery or a complete production release are finished.
P3 is the current implementation focus after the bounded P2 checkpoints. P3/P4 ordering is the main delivery sequence; needed
Providers and narrow extension seams can be pulled forward for concrete adoption
requirements. Do not wait for P3 completion or public SDK packaging before
implementing a necessary adapter behind accepted boundaries.

The original M0-M8 capability decomposition and M2.1 work package are retained in
the [historical roadmap](docs/history/roadmap-before-delivery-milestones.md).
Their identifiers in ADRs and old issues remain valid historical references.
They are no longer the primary measure of delivery progress.

## P1 - Resource provisioning pilot

Status: delivered for one Haby instance, one Application per ChangeSet and the
accepted generation-1 PostgreSQL pilot.

- [x] Independent Domain/Manifest/Application boundaries, typed IDs and strict source-generated Application manifest parsing.
- [x] Deterministic validation, normalized immutable revisions, equivalent retries and version-content conflicts.
- [x] PostgreSQL catalog persistence, automatic startup migrations and current-build restart/backup/restore evidence.
- [x] Name-based Environment, administrative Secret and Provider bootstrap.
- [x] Immutable Provider configuration revisions, checked alias switching and exact pinned planning inputs.
- [x] Durable ChangeSets, Plans, desired generations, operation graphs and sequential execution.
- [x] Dispatch markers, honest uncertainty, no blind post-dispatch replay and explicitly consented standalone database-create recovery.
- [x] Stable generated application credentials, common outputs and Provider-neutral name reservations.
- [x] Five-step PostgreSQL database-owner path: create database, restrict PUBLIC, generate material, create login, apply owner/schema access.
- [x] Real application-password authentication and EF Core migration/operation evidence.
- [x] gRPC operator Host and API-only Native AOT habyctl, YAML contexts and safe text/JSON output.
- [x] Full operator path and one-command durable generation execution.

Evidence anchors: [five-step path](docs/development/120-five-step-operator-path.md),
[generation execution](docs/development/122-generation-execute.md), and merged
PRs [#119](https://github.com/hamstix/haby/pull/119),
[#121](https://github.com/hamstix/haby/pull/121) and
[#123](https://github.com/hamstix/haby/pull/123). Test evidence and manual rehearsal
limits must remain distinguishable; this list does not claim every failure was
manually rehearsed.

P1 deliberately does not include multi-Application admission, generation updates,
additional production Providers, role/access recovery, rotation, deprovisioning,
configuration delivery, HA or a stable public API. Those limits remain explicit,
not hidden unfinished foundation work.

## P2 - Application configuration

Status: active. Goal: a real application retrieves configuration and connects
with its generated credentials, without manual assembly of connection settings.

### Delivered

- [x] Immutable format-neutral configuration values and confidential references.
- [x] Typed deferred formatting nodes and exact formatter metadata validation.
- [x] Pure static-template/ready-fragment composition with deterministic collision refusal.
- [x] Deterministic exact-number JSON and separate whole-value redacted preview.
- [x] Ordinary serialization refuses unresolved confidential nodes.
- [x] Environment Profile defaults, Application/Component whole-Profile exceptions and pure in-process expansion: [issue #126](https://github.com/hamstix/haby/issues/126), [expansion contract](docs/architecture/configuration-profiles-v1.md).

Evidence: [issue #124](https://github.com/hamstix/haby/issues/124),
merged [PR #125](https://github.com/hamstix/haby/pull/125) and the
[composition contract](docs/architecture/configuration-composition-v1.md).
Stored operator preview is delivered by issue #146 below; scoped confidential
delivery is available in the closed pilot through issue #148. Operator-admin
authentication and explicit local issuance are implemented in issue #150 below.

### Next slices

- [x] Accept Profile/assignment JSON, explicit static secret projections and configurationSlot in the evolving current Application v1alpha1: [ADR-038](docs/adr/0038-profile-json-contracts-and-explicit-secret-projections.md).
- [x] Implement strict Profile/assignment JSON parsing and immutable mapping, retaining secret projection declarations without enabling secret delivery: [wire contract](docs/architecture/configuration-profile-json-v1alpha1.md).
- [x] Implement configurationSlot in the current Application schema/DTO/normalized snapshot model, with normalized Bindings as the single source for Profile slots: [issue #130 and reset requirements](docs/development/130-binding-configuration-slot.md).
- [x] Persist exact immutable Profile revisions with side-effect-free import/exact read, strict internal snapshots and PostgreSQL reload/concurrency/backup-restore evidence: [issue #132](docs/development/132-profile-revision-persistence.md).
- [x] Expose Profile validation/import/exact revision metadata through generated gRPC, the operator Host and Native AOT habyctl: [issue #134](docs/development/134-profile-operator-path.md).
- [x] Persist/administer scoped Profile assignments ([operator path and evidence](docs/development/136-scoped-profile-assignments.md)) under [ADR-039](docs/adr/0039-scoped-profile-assignment-administration.md), with exact Component validation versions and optimistic concurrency.
- [x] Return complete normalized portable Profile declarations for exact-read JSON, retaining original audit and declared secret references without resolution: [operator path](docs/development/134-profile-operator-path.md), issue #138 under ADR-039.
- [x] Integrate frozen structural layouts/options, exact scoped assignment inputs and provenance into durable Plans/generations, with coherent capture and atomic freshness checks: [operator path and catalog implications](docs/development/142-frozen-profile-planning.md). Renderer compatibility, defaults, materialization and delivery remain pending.
- [x] Accept the first PostgreSQL renderer/formatter and closed timeout/commandTimeout/sslMode contract under [ADR-040](docs/adr/0040-postgresql-connection-rendering-and-consumer-options.md).
- [x] Implement adapter-owned Resource-output rendering, frozen typed consumer settings and pure deferred formatting for the confirmed PostgreSQL application-login output; [implementation and catalog boundary](docs/development/144-postgresql-connection-rendering.md). Production material resolution/delivery remains separate.
- [x] Accept immutable document materialization and stored redacted preview with name-based operator selection and optional explicit generation under [ADR-041](docs/adr/0041-configuration-document-materialization-and-preview.md).
- [x] Implement atomic/idempotent unresolved ConfigurationDocumentRevisions, exact output provenance and strict bounded codecs; no secret material or completed connection strings: [issue #146](docs/development/146-configuration-materialization-preview.md).
- [x] Expose name-based configuration materialize/get/preview through the operator Host and habyctl, resolving current fully applied desired generation once and supporting explicit-generation automation: [operator runbook](docs/development/146-configuration-materialization-preview.md).
- [x] Accept exact-document static delivery grants (non-expiring unless explicitly limited), one shared configurable Host endpoint and transient bounded secret resolution/formatting under [ADR-042](docs/adr/0042-authorized-configuration-delivery.md).
- [x] Implement authorized exact-revision delivery, access grant administration and the real client connection rehearsal; ordinary operator get/preview stay redacted/metadata-only: [pilot boundary and runbook](docs/development/148-authorized-configuration-delivery.md).
- [x] Accept static operator-admin tokens, explicit Host issuance/recovery (including temporary tokens and policy-selected Kubernetes exec/Job), registry credentials and protected default-on reflection under [ADR-043](docs/adr/0043-operator-access-tokens-and-bootstrap.md).
- [x] Implement operator-admin authentication, explicit local issuance/recovery, selected registry credentials and protected reflection without OAuth or complex RBAC: [issue #150 runbook](docs/development/150-operator-access-tokens.md).
- [x] Complete the core manual operator-to-application rehearsal at main b03058e: bootstrap, five-step provisioning, exact document delivery, real DDL/DML, authorization/revocation and restart. Runtime timeout assertions remain unperformed; the full checklist was PARTIAL, not a production-readiness claim.
- [x] Accept ordered rehearsal follow-ups and explicit Application-wide materialization with per-document atomicity under [ADR-044](docs/adr/0044-p2-rehearsal-follow-ups-and-application-materialization.md).
- [x] Add safe receiving-client setting assertions and correct misleading supported-graph/Resource-use diagnostics: [issue #152](https://github.com/hamstix/haby/issues/152), merged PR #153. The earlier manual runtime assertion remains unperformed; subsequent implementation/test evidence is separate.
- [x] Add Profile discovery and exact Profile revision-history listing; preserve exact assignment/planning references: [issue #154](https://github.com/hamstix/haby/issues/154), [operator runbook](docs/development/134-profile-operator-path.md).
- [x] Add one-request materialization of all Component documents for one server-selected Application generation, preserving per-document atomicity, idempotency and a truthful committed prefix on failure: [issue #156 operator path and bounds](docs/development/146-configuration-materialization-preview.md#explicit-application-materialization). Invocation remains explicit; batch access issuance and automatic Workload credential distribution are deferred.

### Bounded adoption prerequisites before the next P3 slice

- [x] Support explicit PostgreSQL `public` for a newly managed database/login pair
  under the existing owner policy, preserving standard schema ownership and exact
  evidence ([issue #247](docs/development/247-postgresql-public-schema.md)). This
  requires a fresh development catalog; it does not adopt an existing installation.
- [ ] Complete the separately accepted #248 prerequisite after #247; no Redis or
  broader Provider/lifecycle scope is included in this slice.

### Limited Application updates before P3

After the current P2 runtime/diagnostic, Profile discovery and Application-wide
materialization slices, implement a bounded sequential update path between
Application generations before starting Workload implementation.
[ADR-045](docs/adr/0045-application-generation-updates-and-resource-continuity.md)
accepts the update/Resource-continuity contracts. The first unchanged-Resource
reuse slice is implemented in [issue #158](docs/development/158-resource-reuse-generation-updates.md);
the unsupported-mutation safety checkpoint is verified in [issue #172](docs/development/172-resource-mutation-refusal.md).
Issue #174 completed the required integrated operator-update checkpoints on main 8b8785a; its overall report remains PARTIAL solely because temporary private-file cleanup was blocked. That operational remainder is separate from functional acceptance. The naming adoption prerequisite below is newly accepted work before P3.

- [x] Support one Application per ChangeSet with ExpectedGeneration preconditions, exact new Plan inputs, atomic desired-state acceptance and explicit execution of a reuse-only next generation from a fully applied baseline. Deferred apply remains separate.
- [x] Preserve stable identities and confirmed management of unchanged database/login Resources with the exact same Provider configuration revision. Reuse the existing database, role and credential through original confirmed evidence, without repeating creation, generation or privilege work. Catalogs without management records are not backfilled.
- [x] Admit the bounded #160 mixed update: reuse the unchanged managed PostgreSQL pair and create one independent unused standalone database on the same exact Provider revision. Later reuse retains direct original sources (pair=1, added database=2), without replaying old work. [Runbook and completion boundary](docs/development/158-resource-reuse-generation-updates.md#mixed-reuse-and-create-160).
- [x] Admit one independent database/application-login pair per update, reusing every previous managed Resource with exact Resource-scoped producers, isolated credentials and direct original sources ([issue #168](docs/development/168-independent-database-login-pair.md)). General dependencies on reused outputs remain unsupported.
- [x] Support adding/removing Components and local Bindings in a new ManifestRevision with the unchanged managed Resource set and exact Provider revision ([issue #162](docs/development/162-component-binding-generation-updates.md)). New Components/Bindings receive configuration; removed ones do not participate in the new generation. Kubernetes object creation/removal remains P3 work.
- [x] Admit retained removal and compatible same-key reintroduction of the #160 independent standalone database while keeping the managed pair declared, with explicit immutable generation membership, stable management/original source, unchanged data/privileges and retained name ownership ([issue #164](docs/development/164-retained-resource-reintroduction.md)).
- [x] Extend retained removal and compatible same-key return to both Resources of one independent database-owner pair while another original pair remains declared, preserving original source/evidence, names, credentials and old exact grants ([issue #170](docs/development/170-retained-independent-pair.md)). Half-pair/last-pair/all-Resource removal, ownership transfer, destructive deprovisioning and retained-name release remain unsupported.
- [x] Admit application-version and supported configuration changes; materialize exact new-generation document revisions while previous documents and grants remain scoped to their original revisions until explicit revocation.
- [x] Support configuration-only generations for the same ManifestRevision when explicitly accepting changed supported Profile/default inputs through a new Plan and Commit. Assignment/default changes alone never mutate accepted generations. [Issue #166 operator path and bounded evidence](docs/development/166-configuration-only-generations.md). Do not couple logging-profile changes to an Application release or promise hot reload in a running process; configuration delivery and P3 restart/rollout are separate concerns.
- [x] Verify refusal of unsupported existing-Resource mutations, including database-name changes, Provider-instance changes and credential substitution, at their actual import, planning and original-evidence boundaries ([issue #172 evidence matrix](docs/development/172-resource-mutation-refusal.md)). Saved alias changes stale existing Plans; new planning cannot authorize rebinding. Only explicitly supported adapter changes are admitted; no automatic data migration, rotation, replacement or destructive deprovisioning.
- [x] Prove the bounded integrated operator-update path through published Native AOT CLI and production Host under issue #174 at main 8b8785a: six generations, actual consumer settings/data, pair addition/retention/return, same-revision configuration changes, repeats/no-op, refusal and restart. Required operator checkpoints passed; private-file teardown remains an explicit operational remainder. Standalone/fault/lock/restore evidence stays separately attributed to automated tests; those scenarios were not independently repeated in this operator run.

Implement the checkpoint sequentially: first reuse unchanged Resources across
generations, then Resource additions and Component/Binding changes with retained
removals, then configuration-only generations, and finally P3 image rollout and
credential/configuration transfer. Detailed contracts require accepted ADRs before
implementation. The current update profile admits unchanged managed pair
reuse plus one independent standalone database addition per update, and changed Component/local Binding sets over unchanged managed Resources,
with a new ManifestRevision, plus explicit same-revision acceptance of changed supported Profile/assignment/default inputs. Bounded standalone and complete independent-pair removal/reintroduction retain direct original provenance even through multiple absent generations. One new independent database/login pair per update is also supported; new logins on existing databases and general dependencies on reused outputs remain refused.

This checkpoint supplies the update foundation for P3 rollout; it does not claim
arbitrary Resource evolution, destructive Application/data deletion, automatic
credential rotation, multi-Application orchestration or production readiness.

Exit evidence:

- Two Components use the same application login/SecretRef with different permitted consumer settings.
- Environment Profiles provide shared layout, logging and telemetry without mandatory individual assignments.
- A scoped Profile revision replaces the inherited whole Profile; no implicit JSON overwrite or enumeration-order precedence.
- Saved document revisions use exact generation/output/Profile/renderer inputs and survive restart.
- Ordinary preview, diagnostics, logs and metadata contain no credential material or completed confidential connection string.
- An explicitly authorized client retrieves its exact configuration and connects to the real database.
- Rendering/retrieval does not provision, regenerate credentials, silently advance generations or change accepted inputs.
- Limited updates preserve existing data and credentials, create only admitted new Resources, retain external resources under explicit ownership when their declarations are removed, and accept configuration-only generations without changing ManifestRevision.

P2's first executable path remains static JSON/local Bindings. Profile parameters,
Liquid/parameter execution (priority to be reconsidered after P3), YAML/text execution, selectors, broad rollout and
cross-Application exports are later slices unless a concrete pilot requirement
explicitly changes that scope. Kubernetes Runtime Identity is not required to
prove the initial separately scoped retrieval route. The authenticated
operator API must never become an unrestricted configuration-reveal endpoint.

Authority: ADR-017/018/034/035/036/037.

### Shared-system naming adoption prerequisite before P3

- [x] Accept [ADR-048](docs/adr/0048-environment-scoped-resource-naming.md): logical Resource declarations, immutable exact/prefixed naming policies selected on Environment-to-Provider bindings, adapter-owned physical names, frozen Plan inputs and no implicit renaming or cross-catalog adoption.
- [x] Implement [issue #175](https://github.com/hamstix/haby/issues/175): neutral immutable policy administration and exact conditional binding selection, frozen planning/freshness inputs and PostgreSQL database/generated-login resolution. Literal exact mode remains available; unsupported existing/retained name changes refuse. No Liquid/parameter engine or Provider-specific catalog fields. [Contract and alpha catalog boundary](docs/architecture/resource-naming-v1alpha1.md).
- [x] Prove two independently bootstrapped authenticated Hosts/catalogs importing identical Application bytes against one disposable PostgreSQL server with staging_ and staging_shard_, Native AOT CLI administration/execution, actual delivery/database access, unchanged update/retention/return and safe overlapping-prefix/external-name conflicts. Reservations remain catalog-local; prefixes are not access control. [Evidence inventory and remaining boundaries](docs/development/175-environment-resource-naming.md); final-HEAD validation is recorded in draft PR #176.

This is a concrete P2 adoption extension, scheduled before Workload implementation;
it does not reopen general Resource relocation, rotation or distributed coordination.
The previous operator rehearsal did not establish this capability; issue #175 adds
the separate naming and shared-system evidence without claiming P3 rollout.

## P3 - Workload lifecycle

Status: in progress. Internal durable initial Deployment CREATE, runtime configuration
and exact readiness are implemented; authenticated operator Plan/Check/Commit admits
the two supported initial shapes. Bounded public submission is delivered by
[issue #229 / merged PR #230](https://github.com/hamstix/haby/pull/230) as of
2026-11-16. [Issue #233](docs/development/233-operator-readiness-observation.md)
adds bounded public readiness observation and renewal of expired observation
attempts. Bounded wait/batch and initial retirement-aware Applied completion are delivered
by [issue #237](docs/development/237-initial-retirement-applied.md). General workflow
resume, updates and nonempty retirement remain unavailable. Goal: deploy and operate an Application using
Haby-managed configuration and the same durable state/operation model.

The checklist separates implemented bounded slices from remaining lifecycle work.
Earlier slice guides retain their original exclusions and evidence; later completed
items below supersede those exclusions only for their explicitly implemented scope.

- [x] Accept the first Workload foundation packet under [ADR-046](docs/adr/0046-kubernetes-workload-foundation-and-rollout-stages.md): unified deployment planning/execution, Kubernetes-specific contracts with neutral core/storage, exact inputs, staged readiness and mandatory retirement cleanup. This is design acceptance, not implementation.
- [x] Accept Workload declaration and Placement selection rules under [ADR-047](docs/adr/0047-kubernetes-workload-declarations-and-placement.md): immutable Placement revisions, Environment defaults, Application/Component selection, explicit placementRef, bounded overrides, named ports and capability refusal. This is architecture acceptance; implemented contract, administration and bounded runtime slices are listed below.
- [x] Accept [ADR-054](docs/adr/0054-p3-workload-contracts-and-typed-images.md): image.name with exact ApplicationVersion/static tag or full reference, Placement registry/prefix, independent containerName default app, explicit defaults/document selection and distinct exact Provider resolutions.
- [x] Accept [ADR-055](docs/adr/0055-p3-implementation-closure-and-coordination.md): Environment-scoped Providers and alias-scoped Placement selection, namespace authority, explicit credentials/preflight, durable publication/revoke, configurable load defaults, distinct wait/resume, 200-member batch bound, retirement marker and coordinator discretion. Design acceptance only.
- [x] Implement [issue #177](https://github.com/hamstix/haby/issues/177): closed current Workload/Placement contracts, placementRef normalization/snapshot preservation, typed immutable models and pure scoped/default/override/name/image resolution with exact Provider inputs. Merged [PR #178](https://github.com/hamstix/haby/pull/178) delivered pure planning; bounded operator admission is now implemented below.
- [x] Specify closed JSON and pure reusable placement inheritance/overrides ([contract matrix](docs/architecture/kubernetes-workload-placement-v1alpha1.md); no executor): existing namespace (normally shared per Environment), container/image/ports/env/resources, replicas/strategy/rollout, startup/liveness/readiness probes, nodeSelector, imagePullSecrets and existing ConfigMap/Secret/PVC mounts with optional/readOnly/subPath semantics. Explicit Development Recreate and Production RollingUpdate settings are configuration choices, not Environment-name inference.
- [x] Add authenticated exact Kubernetes Provider JSON preparation, access-only preflight and alias CAS through Host/habyctl ([issue #204 runbook](docs/development/204-kubernetes-provider-administration.md)). This checks authentication without a selected namespace; Placement/capability admission and executable Workloads remain separate.
- [x] Complete issue #179 API-only Kubernetes qualification with the strict factory, pinned client/API compatibility checks and isolated evidence under PR #180. Qualification is not a production SDK adapter or executable Workload support.
- [x] Persist/administer exact immutable Placement revisions through authenticated validate/import/get, with Environment + Provider-alias scope, complete portable JSON, retry/concurrency/restart/restore and Native AOT operator paths: [issue #181 runbook](docs/development/181-placement-revisions.md). Preparatory import changes no selections or desired state.
- [x] Implement exact named/scoped Placement selections with conditional updates and operator exact reads: [issue #183 runbook](docs/development/183-placement-selections.md). Selection metadata changes no accepted Plan or generation.
- [x] Persist authoritative declaration-keyed exact Provider input sets in Plans/generations ([issue #185 contract](docs/development/185-durable-provider-input-sets.md)), preserving the explicit Resource-pilot restrictions. This slice alone enabled no Workload admission or effects; subsequent slices are listed below.
- [x] Persist evidence-bearing ResourcesReady checkpoints for admitted Resource-only generations through one completion boundary ([issue #187 contract](docs/development/187-resources-ready-checkpoints.md)). Preserve original provenance, audited recovery and confirmed effects; subsequent Workload-stage extensions are listed below.
- [x] Complete the evidence maintenance sequence: [PR #194](https://github.com/hamstix/haby/pull/194) hosted entrypoint, [PR #196](https://github.com/hamstix/haby/pull/196) fixture isolation, [PR #198](https://github.com/hamstix/haby/pull/198) explicit suites and [PR #199](https://github.com/hamstix/haby/pull/199) bounded failure evidence. The earlier #191 proposal/PR #192 was superseded and is not implemented on main. These changes do not implement Workload runtime stages.
- [x] Implement the [issue #200 bounded Deployment kernel](docs/development/200-deployment-kernel.md): strict explicit transport, closed intent/receipts, one-shot create, conditional keyed update and separate read-only uncertain observation. The stateless kernel alone grants no durable authority. Internal durable CREATE intent/dispatch linkage is delivered by [PR #203](https://github.com/hamstix/haby/pull/203) ([issue #202 guide](docs/development/202-durable-workload-intents.md)); durable UPDATE and Workload Applied completion remain unavailable.
- [x] Implement durable exact Placement capture, initial unchanged-Resource mixed CREATE and explicit empty ResourcesReady for initial Workload-only generations: merged [PR #203](https://github.com/hamstix/haby/pull/203) and [PR #207](https://github.com/hamstix/haby/pull/207) ([empty-stage guide](docs/development/206-workload-only-empty-resource-stage.md)). Internal Submitted/Ready checkpoints follow below; retirement/final completion remain separate.
- [x] Implement bounded initial Deployment CREATE, with and without selected runtime configuration: [PR #203](https://github.com/hamstix/haby/pull/203) and [PR #224](https://github.com/hamstix/haby/pull/224). This is an internal runtime boundary, not full supported-contract execution.
- [ ] Complete the accepted Deployment field contract and explicitly selected Service and headless Service (allowed together), plus individual Ingress controlled by placement. No compulsory bundle, arbitrary PodSpec/YAML or ownership of referenced objects; shared product routing remains separate.
- [x] Accept [ADR-049](docs/adr/0049-kubernetes-object-ownership-and-uncertain-writes.md): stable applicationId-componentId-workloadId names with optional nameOverride, exact UID/ownership, bounded managed fields, conditional JSON Patch, conflicts and evidence-based observation after uncertain writes. This is architecture acceptance, not implementation.
- [x] Implement bounded Deployment write/observation contracts and uncertain CREATE recovery, plus owned ServiceAccount/configured Deployment inventory: [PR #201](https://github.com/hamstix/haby/pull/201), [PR #203](https://github.com/hamstix/haby/pull/203), [PR #212](https://github.com/hamstix/haby/pull/212) and [PR #224](https://github.com/hamstix/haby/pull/224). The conditional UPDATE kernel is not durable Workload UPDATE admission.
- [ ] Complete ADR-049 inventory and closed effect contracts for the remaining supported fields/companion objects, durable updates and deletion. Validate kind-specific names and headless suffixes without truncation; no foreign-object adoption, implicit rename/recreation or companion-object cascade. Prove against a real Kubernetes API.
- [x] Implement internal pre-Applied exact document preparation under [issue #208](docs/development/208-preapplied-workload-configuration.md), with original Resource proof, stable recipient associations and unchanged public/executable guards. Merged [PR #210](https://github.com/hamstix/haby/pull/210) establishes this preparation boundary; subsequent identity/publication/CREATE slices are listed below. Static Profile configuration-secret execution under [ADR-057](docs/adr/0057-profile-configuration-secret-delivery.md) is delivered by [#209 / PR #242](https://github.com/hamstix/haby/pull/242), including exact frozen secret versions and authorized delivery.
- [x] Implement internal stable Workload identity and owned ServiceAccount/binding preparation after complete exact documents ([issue #211](docs/development/211-stable-workload-runtime-identity.md)). Merged [PR #212](https://github.com/hamstix/haby/pull/212). Binding alone grants no access; publication and configured CREATE are separate implemented slices below, while operator submission is delivered separately below and Applied completion remains unavailable.
- [ ] Complete the partially implemented Resource-ready -> exact configuration/access preparation -> Workload submission -> readiness -> retirement -> AppliedGeneration path. Internal preparation/CREATE/readiness are delivered below; bounded initial retirement/finalization is delivered by [#237](docs/development/237-initial-retirement-applied.md); full workflow resume, updates and nonempty retirement remain. Bounded exact observation and batch are delivered below. Preserve the Resource-only path and the existing ordinary fully-applied configuration selector.
- [x] Accept [ADR-051](docs/adr/0051-workload-readiness-wait-and-retirement.md): exact readiness, bounded server-side wait/resume, product-upgrade and CI batch waiting, mandatory planned retirement before AppliedGeneration, and recovery by observation after external repairs. Design acceptance, not implementation.
- [x] Expose bounded submission without waiting through authenticated Host/generated RPC and API-only habyctl for the two supported initial shapes: [#229 / merged PR #230](https://github.com/hamstix/haby/pull/230), [submission guide](docs/development/229-operator-workload-submission.md). Staged documents/identity/publication/CREATE retain durable partial progress, observation-only uncertainty and immutable principal attribution. Historical Submitted is distinct from current rights and readiness. Bounded wait/batch and initial Applied are delivered below; full workflow resume, UPDATE and nonempty retirement remain unavailable.
- [x] Expose exact-generation readiness observation, explicit expired-attempt renewal and bounded product/CI batch observation under ADR-051 ([single-generation guide](docs/development/233-operator-readiness-observation.md), [batch guide](docs/development/235-batch-readiness-wait.md)). CLI uses one Host RPC and never contacts Kubernetes; original rollout deadlines, immutable request history and current-rights checks remain effective. Full lifecycle resume and retirement-aware Applied completion remain separate work.
- [x] Accept [ADR-053](docs/adr/0053-forward-correction-without-rollback.md): forward correction through explicit desired generations, without automatic or dedicated rollback. Historical revisions/earlier images remain explicitly selectable under current admission rules, never as reversal of external effects. Keep the bounded ADR-052 incomplete-rollout path.
- [x] Accept [ADR-052](docs/adr/0052-corrective-generations-for-incomplete-workload-rollout.md): explicit corrective generation after confirmed ResourcesReady, with no uncertain writes and before retirement, atomic executor handoff, direct original Resource evidence and accumulated inventory. This is a narrow future P3 exception to ADR-045, not implemented P2 support.
- [ ] Implement the ADR-052 corrective intent/Plan/Commit and executor fencing, including failed first rollout, unchanged Resource/Workload identity sets, direct confirmed source reuse and safe eventual retirement. Ordinary updates still require fully applied baseline; no adoption, rollback or replay. Implement planned cleanup with exact inventory/UID/recipient checks.
- [x] Accept [ADR-050](docs/adr/0050-kubernetes-runtime-identity-and-configuration-publication.md): stable per-ManagedWorkload ServiceAccount/binding, service-account-bound TokenReview, separate exact-document publication/revocation and bounded mass startup. Old Pods may request newer explicitly published revisions; materialization alone grants no access. Design acceptance, not implementation.
- [x] Implement the bounded ADR-050 initial identity/publication/delivery/client/projected-token path through the completed slices below. Audience, restore/clone identity and operator boundaries are documented in the [mechanism guide](docs/architecture/kubernetes-runtime-configuration.md) and slice guides. Cross-generation rollout/retirement remains incomplete.
- [x] Persist friend-only exact runtime publication/revocation metadata with immutable intent receipts, conditional permission lifecycle and terminal binding revoke: [issue #213 guide](docs/development/213-runtime-configuration-publication.md). Merged [PR #214](https://github.com/hamstix/haby/pull/214); authentication, delivery and configured CREATE follow below, without conflating publication history with current authority.
- [x] Add internal exact-binding Kubernetes TokenReview authentication with original identity proof, a fresh binding lifecycle check and validated shared Host admission: [issue #215 guide](docs/development/215-runtime-tokenreview-authentication.md). Merged [PR #216](https://github.com/hamstix/haby/pull/216). Authentication grants no document permission; subsequent delivery and projected-file integration remain separately authorized.
- [x] Deliver exact published configuration through the independent runtime RPC with online identity authentication, fresh metadata-only publication authorization and bounded transient application-material resolution: [issue #219 guide](docs/development/219-runtime-configuration-delivery.md). Merged [PR #220](https://github.com/hamstix/haby/pull/220). P2 static delivery stays separate; startup, configured CREATE and readiness are separate slices below, and Applied advancement remains unavailable. Passing evidence must be attributed to its exact source; the post-P4 load rehearsal remains pending.
- [x] Fetch all exact mandatory documents before primary application startup, rereading projected credentials per attempt with bounded temporary retry/backoff/jitter and one total deadline: [issue #221 guide](docs/development/221-runtime-startup-client.md), merged [PR #222](https://github.com/hamstix/haby/pull/222). This does not promise a stable public SDK, hot reload or the deferred load capacity.
- [x] Implement bounded friend-only initial configured Deployment CREATE with frozen Placement endpoint/trust, exact non-secret bootstrap and complete owned token projection: [issue #223 guide](docs/development/223-configured-deployment-bootstrap.md). Merged [PR #224](https://github.com/hamstix/haby/pull/224). Operator submission is delivered separately by PR #230, observation/batch by #233/#235 and bounded initial retirement-aware Applied by #237; configured updates and nonempty retirement remain unavailable. Internal readiness follows below. Executable inventory is distinct from exact-source passing evidence.
- [x] Implement friend-only exact confirmed Deployment observation and audited expired-attempt resume: [issue #225 guide](docs/development/225-deployment-readiness.md). Merged [PR #226](https://github.com/hamstix/haby/pull/226). Per-Workload deadlines and truthful Submitted/Ready evidence persist; #233/#235 add bounded public observation/batch and #237 adds initial empty-retirement Applied completion. General workflow resume, updates and nonempty retirement remain separate. Executable coverage is distinct from exact-source passing evidence.
- [x] Admit the two supported initial Workload desired-state shapes through authenticated operator Plan/Check/Commit ([issue #227 guide](docs/development/227-operator-workload-admission.md)). Merged [PR #228](https://github.com/hamstix/haby/pull/228). These admission calls perform no submission; explicit submission is implemented separately by PR #230 above. Bounded public observation/expired-attempt renewal and batch are delivered by #233/#235; #237 adds initial retirement-aware Applied completion. General workflow resume, updates and nonempty retirement remain unavailable.
- [x] Add authenticated exact-generation readiness observation and explicit expired-observation renewal through Host/generated RPC and API-only CLI ([issue #233 guide](docs/development/233-operator-readiness-observation.md)). This staged boundary preserves historical/current authority and original deadlines; #235 adds bounded product/CI batch observation and #237 adds initial empty-retirement Applied completion. General workflow resume, updates and nonempty retirement remain separate. Exact-source passing evidence is recorded separately.
- [x] Carry typed CPU/memory requirements from exact Placement or whole Workload overrides through initial CREATE, confirmation, restart and readiness ([issue #243 guide](docs/development/243-deployment-resource-requirements.md)). Stateless conditional patch supports owned leaf changes/removals; durable UPDATE remains unsupported.
- [x] Execute existing named ports and startup/liveness/readiness probes in both initial Deployment shapes ([issue #245 guide](docs/development/245-deployment-ports-probes.md)), preserving exact frozen values, keyed ownership and real controller readiness. Durable/configured UPDATE and Service exposure remain separate.
- [x] Implement validated operator-configurable TokenReview concurrency/rate/queue/timeouts ([PR #216](https://github.com/hamstix/haby/pull/216), [Host settings](docs/development/215-runtime-tokenreview-authentication.md#host-settings)) and client startup/retry settings ([PR #222](https://github.com/hamstix/haby/pull/222), [client options](docs/development/221-runtime-startup-client.md#validated-startup-options-and-bounds)), including frozen Placement client settings in [PR #224](https://github.com/hamstix/haby/pull/224), under ADR-055. Ordinary CI covers fast deterministic correctness, cancellation/revocation/identity, token rotation and real API validation plus existing mandatory gates. Preserve bounded queue/memory/credential lifetimes, no positive cache and the total client startup deadline with backoff/jitter. Separate mass-start qualification is deferred until after P4; it does not gate P3 completion or current runtime-delivery acceptance.
- [x] Add independent whole-group named-port/probe defaults from exact KubernetesPlacement with authored absent/empty preservation, pure effective-reference validation and frozen initial CREATE/readiness inputs ([#248 guide](docs/development/248-placement-ports-probes-defaults.md)). Assignment changes require explicit new Plans; durable UPDATE remains unsupported.
- [ ] Support image/version updates through a new accepted desired generation, with observable completion and safe failure behavior.
- [ ] Separate desired replicas from suspend/resume and temporary operational overrides.
- [ ] Plan Application/Component removal and workload cleanup explicitly; apply Resource retain/orphan/delete policy only when that capability is implemented and authorized.
- [ ] Implement explicit cleanup for removed Components/Bindings: remove only owned obsolete Workload objects and revoke superseded document grants after the accepted rollout/retirement boundary. Track partial failure/retry and protect still-running recipients; do not delete retained databases/roles or infer cleanup from a P2 declaration removal. No automatic cleanup is claimed before this path is implemented.
- [ ] Prove install -> configure -> deploy -> update -> remove on a real disposable/test cluster, including restart and partial failure.

Exit evidence: an operator can deploy, inspect, update and remove the chosen
application through supported interfaces. Explicit object ownership prevents
accidental removal of unrelated objects. Workload removal does not implicitly
drop a still-declared database or rotate credentials.

Kubernetes is the first and only supported Workload runtime in this scope;
its declarations/implementation may be Kubernetes-specific. Shared deployment
logic and persistence stay independent of Kubernetes client types and fixed object
structure; additional runtime abstractions require actual demand. A Kubernetes
Provider/reconciler is an integration. Haby still uses PostgreSQL as its default
source of truth; Kubernetes is not a mandatory runtime for hosting Haby. A CRD
operator, leader election and Kubernetes-only storage mode are separate later
capabilities, not prerequisites for this milestone.

### Accepted P3 execution order

Queue clarification: issue #252 is followed immediately by the accepted docs-only
CI routing maintenance in #253, then the remaining operational ADR-058 Deployment
update slices. Public CLI composition follows the completed Deployment update track,
not before it, under [ADR-059](docs/adr/0059-public-cli-composition.md).

- [ ] Expose public Native AOT command composition and typed context resolution;
  keep upstream root commands and permit a downstream registry parent.
- [ ] Add config adapters preserving downstream fields and safe views, configurable
  paths (upstream sample ~/.haby/config.yaml), and a published external AOT example.
- [ ] Show contextual help for bare command groups and add explicitly installed
  Windows/Linux shell completion from the composed command tree.

Accepted next priority: after #248, implement bounded ApplicationVersion/image
updates under [ADR-058](docs/adr/0058-bounded-deployment-image-updates.md), before
Service, headless Service and individual Ingress. Those objects remain in P3.

- [ ] Admit and freeze image-only updates from Applied=Desired, with proven Resource
  reuse and exact prior/target inventory; no runtime UPDATE enabled by planning alone.
- [x] Implement internal generation-specific configuration, stable runtime identity
  reuse, exact publication and durable same-UID conditional PATCH under
  [issue #257](docs/development/257-durable-deployment-update-submission.md).
  Confirmed submission/history only; public UPDATE and its completion remain closed.
- [ ] Confirm target readiness and old recipients (including terminating Pods),
  retire obsolete runtime permissions and only then advance AppliedGeneration.
- [ ] Implement ADR-052 corrective generation for incomplete rollout before declaring
  the update workflow operationally ready; no rollback or automatic adoption.
- [ ] Rehearse the real update/correction lifecycle, stable Resources/credentials,
  configuration coexistence/retirement and restart/lost-response handling.

These are accepted future slices, not delivered capabilities. Changed Profile or
Placement inputs are refused by this bounded path. Static delivery grants remain
independent; load qualification remains post-P4.

Completed user-approved sequencing exception after #237 / PR #238: the narrow
[#239 trusted public Host composition boundary](docs/development/239-public-host-composition.md)
was delivered by [PR #240](https://github.com/hamstix/haby/pull/240) before #209 / PR #242. It permits downstream application service
registration and protected custom gRPC mapping with the existing Host. General
SDK/NuGet packaging, CLI command composition and Provider/catalog extension stay
deferred; this exception does not change remaining Workload lifecycle semantics.

**Historical stop-loss (accepted 2026-11-13):** the instruction was to stop new
qualification attempts for [#191](https://github.com/hamstix/haby/issues/191) and
[#188](https://github.com/hamstix/haby/issues/188), safely reconcile owned resources
and preserve completed, failed and unperformed evidence. Keep the issues and PRs
open and unmerged at that checkpoint, then start maintenance from main under
[ADR-056](docs/adr/0056-test-responsibilities-isolation-and-execution.md), without
requiring the unmerged local harness. That historical instruction is retained here
as context; the maintenance prerequisite is now complete:

- [x] Map every case/theory parameter and provide the documented runner/preflight: merged [PR #194](https://github.com/hamstix/haby/pull/194).
- [x] Separate catalog persistence from Provider effects with explicit database/server isolation and measured configurable concurrency: merged [PR #196](https://github.com/hamstix/haby/pull/196).
- [x] Separate functional/Host and infrastructure qualification execution with complete coverage mapping: merged [PR #198](https://github.com/hamstix/haby/pull/198).
- [x] Finish #188 through the new path with bounded row-associated failure evidence: merged [PR #199](https://github.com/hamstix/haby/pull/199). #191 / PR #192 was superseded, not implemented on main.

Functional P3 has resumed. Earlier failed runs and unresolved historical causes
remain separately attributed; maintenance completion does not erase that evidence.

GitHub-hosted Linux is the primary full integration gate; a separate Linux runner
can support diagnostics and periodic heavy qualification. Preserve Windows-specific
and published Native AOT checks and real external semantics. ADR-055 mass-start
qualification remains pending after P4 under the user-approved 2026-11-14
supersession; it does not block P3. The maintenance migration is complete; deferred
load qualification is not a waiver of current checks. Ordinary validation uses
source/build/results attribution rather than immutable Git-index or repeated forensic preparation requirements.

Under [ADR-055](docs/adr/0055-p3-implementation-closure-and-coordination.md):

1. Completed: #177 contracts and pure resolution ([PR #178](https://github.com/hamstix/haby/pull/178)).
2. Completed: Kubernetes technical qualification ([PR #180](https://github.com/hamstix/haby/pull/180)).
3. Completed: Placement persistence and administration ([PR #182](https://github.com/hamstix/haby/pull/182), [PR #184](https://github.com/hamstix/haby/pull/184)).
4. Completed for the initial shapes: durable exact Provider inputs and stage checkpoints, including Workload-only empty ResourcesReady ([PR #186](https://github.com/hamstix/haby/pull/186), [PR #189](https://github.com/hamstix/haby/pull/189), [PR #203](https://github.com/hamstix/haby/pull/203), [PR #207](https://github.com/hamstix/haby/pull/207)).
5. Partially completed: bounded conditional Deployment kernel and durable initial CREATE/uncertain observation are implemented; remaining supported fields/companion objects and durable UPDATE are not.
6. Completed for the bounded initial path: runtime identity/publication/delivery/client and configurable protections; separate mass-start qualification follows P4.
7. Partially completed: internal submission/exact readiness and operator desired-state admission are delivered. Operator submission is delivered by [PR #230](https://github.com/hamstix/haby/pull/230). Bounded public readiness/expired-attempt renewal and product/CI batch wait are delivered by PR #236; [#237](docs/development/237-initial-retirement-applied.md) adds retirement-aware Applied for the two admitted initial shapes. Complete the remaining supported contract, full workflow resume, image/configuration updates and nonempty retirement through dedicated slices.
8. Remaining: explicit retirement and removal.
9. Remaining: corrective generations with fenced execution and accumulated inventory.
10. Remaining: full operator lifecycle rehearsal. #239 / PR #240 delivers trusted Host composition; #209 / PR #242 delivers exact Profile configuration-secret execution under ADR-057. #243 extends the existing initial CREATE path with typed CPU/memory requirements; updates/removal remain separate.

Qualification may run alongside contract work; production slices are accepted
sequentially. The coordinator may split narrow issues and choose ordinary technical
details inside accepted semantics, but ownership/auth/lifecycle/scope/replay changes
return for architectural approval. This does not change review/gate or Git/merge
authorization rules. Contract acceptance never enables an incomplete executor.

## P4 - Standard Providers

Status: planned. Goal: cover the external systems needed by real Applications
through complete Provider scenarios, preserving the shared Provider-neutral model.

- [ ] Prioritize exact supported Resource/effect contracts from concrete adoption requirements before implementing each Provider.
- [ ] Add RabbitMQ credentials and the queue/exchange/binding capabilities required by the first application scenario.
- [ ] Add Redis access/configuration and supported ACL or logical-database capabilities appropriate to the target deployment.
- [ ] Add HTTP/auth configuration contracts, using configuration-only integration when no external provisioning effect is needed.
- [ ] Extend PostgreSQL beyond the accepted initial pilot only through separately agreed policy and lifecycle slices.
- [ ] For each Provider, supply its necessary credentials, confirmed typed outputs, configuration renderer/formatter and real-client integration evidence.
- [ ] Prove each external effect's idempotency, dispatch/uncertainty behavior, restart and safe failures without inventing universal recovery or database requirements.
- [ ] Keep shared persistence, operation infrastructure, Host/CLI metadata and configuration composition free of Provider-specific fields and branching.

Exit evidence: a real application can use each admitted integration through the
supported operator/provisioning/configuration path. An HTTP/auth integration may
need only configuration; a RabbitMQ credential scenario need not create a database.
Synthetic second-type tests alone are not production Provider support.

Needed Providers may be implemented before Workload lifecycle is complete.
Build-time trusted composition remains the supported model under ADR-003. Public
SDK/NuGet packaging, general downstream server extension and CLI composition are separate
later work (apart from the narrow #239 Host application-service boundary), informed by several real adapters rather than prerequisites for P4.
Dynamic managed DLL loading, runtime provider protocols and WebAssembly execution
are not introduced.

## Post-P4 - Deferred mass-start qualification

User-approved scheduling supersession, 2026-11-14: [issue #217](https://github.com/hamstix/haby/issues/217)
and this unchecked item track the deferred work; no duplicate implementation issue
is needed. This is not a gate for P3 completion or current runtime-delivery
acceptance. Capacity at this load remains unverified and must not be advertised as
demonstrated. Ordinary protection, correctness/security tests and mandatory CI
remain required; P3 is not declared complete. This timing also supersedes the
future load-gate wording in [issue #215](https://github.com/hamstix/haby/issues/215),
without excusing its functional failures or changing its authentication acceptance.

- [ ] After P4, run the separate real-cluster rehearsal with at least 150 simultaneous clients, two mandatory documents each. Require healthy-profile retrieval within the configured total startup deadline and a constrained-overload scenario proving queue saturation, bounded rate/concurrency/memory/credential lifetimes, cancellation and deadline expiry. Record exact SHA/settings, payload/fan-out, latency, retries/rejections and actual TokenReview calls, with failures and omissions. Run manually/periodically, then after material runtime-path changes; ordinary CI does not substitute for this evidence.

## Supporting capabilities and release gates

These are retained work, not implicitly completed by P1 or cancelled by the new
delivery sequence. Pull a narrow item forward when the next pilot needs it.
A public production release must explicitly state which capabilities and
operating profiles it supports; completing a pilot alone is insufficient.

- **Operational safety:** broader resource reconciliation, additional effect-specific recovery, secret/resource rotation, audit/observability, restore/upgrade runbooks and explicit lifecycle/deletion policies. Use ADR-053 forward correction; no dedicated historical-state rollback or general inverse-effect/compensation guarantee.
- **Security:** implement EnvelopeEncrypted storage/key management under ADR-006, scoped operator/runtime authorization, OIDC/API accounts and rate limits as required by the supported deployment. Plaintext remains a standard explicit storage mode, with documented backup/database-reader exposure.
- **Public extension SDK:** after several real Providers validate the boundaries, package versioned abstractions, explicit module/custom gRPC registration and Native AOT CLI command composition. Prove an external sample without core edits; keep the CLI API-only. This is separate from P4 standard Provider delivery.
- **Configuration evolution:** typed parameters, selectors/groups, authorized cross-Application exports, Profile rollout, YAML/text execution and optional publication channels. No silent latest resolution or unrestricted JSON merge.
- **Scale and availability:** multiple Applications per ChangeSet, broader Resource/deployment evolution beyond the limited P2 update checkpoint, and leases/workers/HA require separate accepted contracts and tests. The limited generation-update path is scheduled in P2 before P3; current execution remains sequential and single-instance.
- **Automated apply (deferred):** provide one `habyctl apply` command/request that performs Plan creation -> freshness verification -> desired-state Commit on the server for an explicit ChangeSet. Preserve separate immutable Plan and Commit records, exact planning inputs and atomic commit/precondition checks; keep the reviewable separate commands. The CLI must not orchestrate these steps locally. Execute remains an explicit separate operation; this item does not authorize automatic provisioning, materialization, access issuance or Workload rollout. Define retry/response-loss semantics before implementation.
- **Public API:** stable versioned REST/gRPC contracts and client/compatibility policy follow the mutable internal alpha operator surface.
- **UI:** MudBlazor administration after the useful CLI/API path; navigation Folders follow stable UI views. Folders never imply identity, configuration or permission inheritance.
- **Distribution:** versioned images/packages, installation and empty-environment rehearsals, supported-version matrix, vulnerability handling and documented upgrade/reset boundaries.
- **Later integrations:** external secret stores, developer delivery sessions, ConfigMap/Secret/HTTP/optional Consul publication and Kubernetes CRD operator mode remain future capabilities. None is silently implemented or newly required for P2.

Detailed historical capability lists remain in the
[archived roadmap](docs/history/roadmap-before-delivery-milestones.md).
Accepted ADRs govern conflicts; archive checkboxes and old stage-specific
statements must not be interpreted as current implementation status.

## Architecture and working rules

- Keep this public repository product-neutral. Downstream conventions belong outside the core.
- Domain/Application stay independent of hosting, EF Core and concrete adapters. The Host is the composition root.
- Shared persistence uses generic Provider-neutral entities/fields; adapters own typed versioned configuration, naming, evidence and effects under ADR-035.
- Import is side-effect-free; exact Plan/commit precedes external execution.
- Current-build restart, retry, uncertainty, history and backup/restore remain required. ADR-033 permits explicit breaking alpha/reset-required changes, never silent data reset.
- Secret material belongs in SecretStore, not ordinary configuration snapshots, errors or logs. Confidential delivery is a separate authorization boundary.
- Use source-generated metadata and explicit registration. Production habyctl remains Native AOT and API-only; the server remains CoreCLR while moving toward trimming/AOT-compatible boundaries.
- Use focused regression tests, independent persisted arrange/act/assert contexts and real disposable integration infrastructure for external effects.
- An accepted design is not an implemented capability. A passed synthetic seam test is not support for another production Provider.

See [CONTRIBUTING](CONTRIBUTING.md),
[agent workflow](docs/development/agent-coding-workflow.md),
[ADRs](docs/adr/) and the
[Application contract](docs/architecture/application-manifest-v1alpha1.md).

## Tracking and next implementation session

GitHub tracking follows the delivery sequence:

- [P1 - Resource provisioning pilot](https://github.com/hamstix/haby/milestone/2): closed, delivered within the documented pilot boundary.
- [P2 - Application configuration](https://github.com/hamstix/haby/milestone/3): open administratively; bounded configuration/updates and issue #175 naming are implemented. Issue #174 required operator checkpoints passed; its private-file teardown remainder stays separately documented. P3 is the current implementation focus.
- [P3 - Workload lifecycle](https://github.com/hamstix/haby/milestone/4): open; bounded internal CREATE/configuration/readiness and operator desired-state admission are delivered. [PR #230](https://github.com/hamstix/haby/pull/230) delivers initial submission; #233/#235 add bounded public observation/renewal/batch and #237 adds initial empty-retirement Applied completion. Full workflow resume, updates and nonempty retirement remain.
- [P4 - Standard Providers](https://github.com/hamstix/haby/milestone/5): open, planned; individual needed Providers may be pulled forward.

The completed [M2.1 - Manifest foundation](https://github.com/hamstix/haby/milestone/1)
is closed and retained for history. Closed issues keep their historical milestone
membership. ADR identifiers are not renumbered. Future issues belong to the
delivery milestone whose observable outcome they advance.

Issue #126 is delivered by PR #127. Issue #128 implements the Profile/assignment
JSON boundary accepted in ADR-038. Issue #130 implements the current-alpha
Application slot and snapshot changes. Secret declaration parsing does not authorize secret resolution or delivery.
After merged #233/#235/#237, #239 / PR #240 and #209 / PR #242, continue P3
with the remaining supported contract after [#243 resource requirements](docs/development/243-deployment-resource-requirements.md),
updates/removal/corrective generations and real lifecycle
rehearsal under ADR-046/049/050/051/052/055. This documentation reconciliation is
independent and must not block functional P3. Preserve P2 runtime guarantees;
Profile configuration-secret execution is delivered under ADR-057; the >=150-client load
rehearsal remains post-P4. Do not start a general SDK/UI/HA project.
