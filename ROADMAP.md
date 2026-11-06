# Haby delivery roadmap

This roadmap tracks usable outcomes. The initial manifest foundation has been
delivered; the current delivery milestone is configuration generation and scoped
retrieval, not M2.1 foundation work.

The delivery stages below reorganize existing commitments. They do not change
accepted ADRs, stabilize alpha contracts or claim general production readiness.
There are no calendar estimates. Each stage is delivered through narrow issues
and independently reviewable PRs.

## Current position

| Delivery milestone | Status | Observable outcome |
| --- | --- | --- |
| P1 - Resource provisioning pilot | Delivered within the isolated PostgreSQL pilot | Prepare an Environment, import an Application, Plan/commit/execute, create its database and generated application login, and grant database-owner access. |
| P2 - Application configuration | In progress | Generate exact configuration documents from applied Resource outputs and shared Profiles; let an explicitly authorized application client retrieve and use them. |
| P3 - Workload lifecycle | Planned | Deploy an Application to Kubernetes, deliver its configuration, update it and safely remove its managed workload objects. |
| P4 - Standard Providers | Planned | Add RabbitMQ, Redis and HTTP/auth integrations and extend PostgreSQL through complete tested application scenarios. |

P1 is a working checkpoint, not a claim that configuration delivery, all
Providers, general recovery or a complete production release are finished.
P2 is the current focus. P3/P4 ordering is the main delivery sequence; needed
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
The in-process preview is not yet an operator endpoint or confidential delivery.

### Next slices

- [x] Accept Profile/assignment JSON, explicit static secret projections and configurationSlot in the evolving current Application v1alpha1: [ADR-038](docs/adr/0038-profile-json-contracts-and-explicit-secret-projections.md).
- [x] Implement strict Profile/assignment JSON parsing and immutable mapping, retaining secret projection declarations without enabling secret delivery: [wire contract](docs/architecture/configuration-profile-json-v1alpha1.md).
- [x] Implement configurationSlot in the current Application schema/DTO/normalized snapshot model, with normalized Bindings as the single source for Profile slots: [issue #130 and reset requirements](docs/development/130-binding-configuration-slot.md).
- [x] Persist exact immutable Profile revisions with side-effect-free import/exact read, strict internal snapshots and PostgreSQL reload/concurrency/backup-restore evidence: [issue #132](docs/development/132-profile-revision-persistence.md).
- [x] Expose Profile validation/import/exact revision metadata through generated gRPC, the operator Host and Native AOT habyctl: [issue #134](docs/development/134-profile-operator-path.md).
- [ ] Persist/administer scoped Profile assignments under [ADR-039](docs/adr/0039-scoped-profile-assignment-administration.md), with exact Component validation versions and optimistic concurrency.
- [ ] Extend Profile exact-read JSON to return the full normalized declaration without secret resolution, in a separate slice under ADR-039.
- [ ] Integrate frozen effective layouts/options and assignment inputs into planning.
- [ ] Define and implement adapter-owned Resource-output renderers, typed consumer option contracts and confidential formatters; start with the confirmed PostgreSQL application-login output.
- [ ] Materialize immutable unresolved ConfigurationDocumentRevisions from exact applied-generation inputs, with idempotency, provenance and no secret-bearing strings in ordinary storage/results.
- [ ] Expose redacted preview through the operator Host and habyctl.
- [ ] Agree exact document selection, minimal authorization, transport and response-handling policy for confidential retrieval.
- [ ] Implement scoped retrieval and deferred secret resolution/formatting through that separately authorized boundary.
- [ ] Run the full operator-to-application rehearsal: bootstrap -> provisioning -> configuration -> client authentication and application use.

Exit evidence:

- Two Components use the same application login/SecretRef with different permitted consumer settings.
- Environment Profiles provide shared layout, logging and telemetry without mandatory individual assignments.
- A scoped Profile revision replaces the inherited whole Profile; no implicit JSON overwrite or enumeration-order precedence.
- Saved document revisions use exact generation/output/Profile/renderer inputs and survive restart.
- Ordinary preview, diagnostics, logs and metadata contain no credential material or completed confidential connection string.
- An explicitly authorized client retrieves its exact configuration and connects to the real database.
- Rendering/retrieval does not provision, regenerate credentials, silently advance generations or change accepted inputs.

P2's first executable path remains static JSON/local Bindings. Profile parameters,
Liquid/parameter execution, YAML/text execution, selectors, broad rollout and
cross-Application exports are later slices unless a concrete pilot requirement
explicitly changes that scope. Kubernetes Runtime Identity is not required to
prove the initial separately scoped retrieval route. The unauthenticated alpha
operator API must never become an unrestricted configuration-reveal endpoint.

Authority: ADR-017/018/034/035/036/037.

## P3 - Workload lifecycle

Status: planned. Goal: deploy and operate an application using Haby-managed
configuration and the same durable state/operation model.

- [ ] Agree the first Kubernetes Workload contract, reconciliation ownership, exact inputs and update semantics.
- [ ] Reconcile explicitly requested Deployment, Service, headless Service and Ingress objects. Resource selection is configurable; no compulsory bundle.
- [ ] Connect exact configuration revisions to workload startup and define rollout/update behavior.
- [ ] Implement Kubernetes Runtime Identity and scoped configuration retrieval using the accepted projected-token/TokenReview model.
- [ ] Support image/version updates through a new accepted desired generation, with observable completion and safe failure behavior.
- [ ] Separate desired replicas from suspend/resume and temporary operational overrides.
- [ ] Plan Application/Component removal and workload cleanup explicitly; apply Resource retain/orphan/delete policy only when that capability is implemented and authorized.
- [ ] Prove install -> configure -> deploy -> update -> remove on a real disposable/test cluster, including restart and partial failure.

Exit evidence: an operator can deploy, inspect, update and remove the chosen
application through supported interfaces. Explicit object ownership prevents
accidental removal of unrelated objects. Workload removal does not implicitly
drop a still-declared database or rotate credentials.

A Kubernetes Provider/reconciler is an integration. Haby still uses PostgreSQL
as its default source of truth; Kubernetes is not a mandatory runtime. A CRD
operator, leader election and Kubernetes-only storage mode are separate later
capabilities, not prerequisites for this milestone.

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
SDK/NuGet packaging and documented downstream server/CLI extension are separate
later work, informed by several real adapters rather than prerequisites for P4.
Dynamic managed DLL loading, runtime provider protocols and WebAssembly execution
are not introduced.

## Supporting capabilities and release gates

These are retained work, not implicitly completed by P1 or cancelled by the new
delivery sequence. Pull a narrow item forward when the next pilot needs it.
A public production release must explicitly state which capabilities and
operating profiles it supports; completing a pilot alone is insufficient.

- **Operational safety:** broader resource reconciliation, additional effect-specific recovery, secret/resource rotation, audit/observability, restore/upgrade runbooks and explicit lifecycle/deletion policies. No unqualified promise of universal rollback or safe compensation.
- **Security:** implement EnvelopeEncrypted storage/key management under ADR-006, scoped operator/runtime authorization, OIDC/API accounts and rate limits as required by the supported deployment. Plaintext remains a standard explicit storage mode, with documented backup/database-reader exposure.
- **Public extension SDK:** after several real Providers validate the boundaries, package versioned abstractions, explicit module/custom gRPC registration and Native AOT CLI command composition. Prove an external sample without core edits; keep the CLI API-only. This is separate from P4 standard Provider delivery.
- **Configuration evolution:** typed parameters, selectors/groups, authorized cross-Application exports, Profile rollout, YAML/text execution and optional publication channels. No silent latest resolution or unrestricted JSON merge.
- **Scale and availability:** multiple Applications per ChangeSet, deployment updates beyond the first pilot, leases/workers/HA only after their accepted contracts and tests. Sequential single-instance execution remains the current supported profile.
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
- [P2 - Application configuration](https://github.com/hamstix/haby/milestone/3): open, current work including issue #126.
- [P3 - Workload lifecycle](https://github.com/hamstix/haby/milestone/4): open, planned.
- [P4 - Standard Providers](https://github.com/hamstix/haby/milestone/5): open, planned; individual needed Providers may be pulled forward.

The completed [M2.1 - Manifest foundation](https://github.com/hamstix/haby/milestone/1)
is closed and retained for history. Closed issues keep their historical milestone
membership. ADR identifiers are not renumbered. Future issues belong to the
delivery milestone whose observable outcome they advance.

Issue #126 is delivered by PR #127. Issue #128 implements the Profile/assignment
JSON boundary accepted in ADR-038. Issue #130 implements the current-alpha
Application slot and snapshot changes. Secret declaration parsing does not authorize secret resolution or delivery.
Continue P2 toward real output rendering,
saved unresolved documents and scoped retrieval; do not repeat foundation work
or start a general SDK/UI/HA project.
