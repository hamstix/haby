# Haby OSS revival roadmap

This roadmap describes the work required to turn Haby from a working prototype into a maintainable open-source configuration and provisioning platform.

The immediate goal is to establish a stable OSS core that downstream distributions can extend without carrying permanent forks.

## Product boundaries

### Haby OSS

Haby OSS owns:

- folders, applications, components and environments;
- versioned application manifests, validation and portable release identity;
- environment-specific application deployments and exact deployment change sets;
- provider instances, resources, resource exports, bindings, workloads and lifecycle policies;
- configuration documents, rendering, revisions and publication;
- generated values, encrypted secret versions, observed resource state and references to secrets;
- provisioning and configuration publication abstractions;
- PostgreSQL persistence and database migrations;
- versioned REST and gRPC APIs;
- background operations, reconciliation and operation status;
- authentication and authorization extension points;
- the web administration UI;
- standard plugins for generally useful infrastructure;
- Docker, Helm and local development assets;
- import/export primitives and documented recovery procedures.

### Downstream distributions

Product-specific distributions should live in separate repositories and consume versioned Haby packages and images. Haby provides stable extension points for compatibility APIs, migration tooling, deployment conventions and private integrations without embedding downstream product details in the public repository.

Generic improvements should be proposed upstream. A downstream source mirror must not become a second writable source of truth.

## Engineering principles

- Lock existing behavior with focused tests before changing production code.
- Keep each pull request limited to one architectural step.
- Replace the unused prototype through small tested vertical slices; do not
  preserve legacy contracts that have no deployed consumers.
- Keep domain logic independent from ASP.NET Core, EF Core, UI and plugin implementations.
- Use stable persisted identities. Names, labels and folder paths are mutable metadata.
- Keep source declarations, operator overrides, generated state and rendered outputs separate.
- Keep folders organizational. Moving an application must not change its configuration or external resource identity.
- Prefer explicit resource bindings and document paths over implicit JSON merging or copying rendered configuration.
- Permit cross-application Resource consumption only through owner-controlled, versioned Resource exports.
- Keep manifest import side-effect free and apply only exact, planned ChangeSet membership.
- Treat provisioning as an idempotent, retryable operation, not as a side effect of a request handler.
- Never store plaintext secrets in logs, operation results or ordinary configuration documents.
- Store managed secret payloads encrypted in PostgreSQL by default, keep key-encryption keys outside the database and exchange only SecretRefs across domain boundaries.
- Keep PostgreSQL as the default source of truth. Kubernetes support is an integration and an optional deployment mode, not the only storage model.
- Use semantic versioning for public contracts and plugin abstractions.
- Use MudBlazor for standard UI components; implement only Haby-specific UX components locally.

## Target solution shape

ADR-013 fixes the first M2 core project names and dependency direction. Later
integration projects should extend this structure without reversing those
dependencies:

```text
Haby.Domain
  <- Haby.Manifest
       <- Haby.Application
            <- Haby.Persistence.PostgreSql
            <- Haby.Plugins.Abstractions
            <- Haby.Api.Contracts
            <- Haby.Server
            <- Haby.Web

Haby.Plugin.PostgreSql
Haby.Plugin.RabbitMQ
Haby.Plugin.Redis
Haby.Plugin.S3
Haby.Plugin.Kubernetes
```

The server is the composition root. Domain and application projects must not reference plugin implementations, EF Core, gRPC-generated server types or UI packages.

## Milestones

### M0 — Reproducible baseline

Objective: make the current repository safely buildable and testable before redesigning it.

- [x] Add an LTS `global.json` and document the required SDK.
- [x] Restore and build the existing solution in a clean checkout.
- [x] Record all warnings and distinguish existing defects from migration defects.
- [x] Add `Directory.Build.props` and central package management if it reduces duplication without changing behavior.
- [x] Create test projects for server/application logic and plugin contracts.
- [x] Add smoke tests for every existing gRPC service.
- [x] Add focused regression tests for configuration rendering and saved generated variables.
- [x] Add a regression test proving that variable deletion is scoped by configuration-unit ID.
- [x] Add CI for restore, build, test, formatting and `git diff --check`.
- [x] Add basic contribution and local-development documentation.

Exit criteria:

- a clean clone builds with one documented command;
- tests run without external PostgreSQL unless explicitly marked as integration tests;
- CI executes the same commands as local development;
- current behavior is captured sufficiently to begin the .NET migration.

### M1 — .NET 10 and OSS dependency cleanup

Objective: move to a supported platform and remove organization-specific dependencies from the public product.

- [x] Migrate all projects to .NET 10.
- [x] Switch CI to the .NET 10 runtime only and remove the temporary .NET 7 SDK installation.
- [x] Upgrade gRPC, EF Core, Npgsql, Fluid and Kubernetes client dependencies.
- [x] Replace externally owned field-mask helpers with OSS-owned contracts and utilities.
- [x] Replace private web, hosting and HTTP-client infrastructure with ASP.NET Core and OpenTelemetry equivalents owned by Haby or the platform.
- [x] Replace obsolete IdentityServer4 integration with a neutral plugin boundary; keep product-specific implementations outside the public repository.
- [x] Enable nullable and analyzer warnings consistently.
- [x] Add dependency and license scanning.

Exit criteria:

- no organization-specific or private package is required to build or run Haby OSS;
- all projects target .NET 10;
- baseline and regression tests pass.

### M2 — Stable domain, application manifest and plugin SDK

Objective: establish the application and resource model before replacing the current configurator and make extensions a supported public surface.

- [x] Accept the [application-manifest proposal](docs/architecture/application-manifest.md) and record its domain terminology and invariants in [ADR-009](docs/adr/0009-application-manifest-domain-model.md).
- [ ] Replace `ConfigurationUnit` and overloaded configuration-key terminology with Application, Component and Configuration document concepts; do not carry `OrganizationUnit` into the M2 core.
- [ ] Introduce Provider instance, Resource and Binding as distinct concepts.
- [ ] Introduce Resource export as the only supported cross-application Resource-consumption boundary.
- [ ] Introduce Workload as a portable declaration nested in exactly one Component and ManagedWorkload as its Environment-specific persisted state.
- [ ] Introduce immutable persisted IDs and optimistic concurrency tokens; keep names and labels mutable.
- [ ] Introduce immutable manifest revisions with a portable Application version and import audit metadata.
- [x] Accept [ADR-010](docs/adr/0010-manifest-revisions-and-deployment-change-sets.md) for immutable imports, Environment deployments, generations and grouped apply.
- [x] Accept [ADR-013](docs/adr/0013-m2-manifest-foundation-boundaries-and-identifiers.md) for M2.1 project boundaries, DTO/domain separation, portable Application IDs, UUIDv7 persisted IDs and local revision numbers.
- [x] Accept [ADR-014](docs/adr/0014-manifest-validation-diagnostics.md) for transport-neutral validation reports, stable diagnostics and immutable historical import warnings.
- [x] Accept [ADR-015](docs/adr/0015-normalized-manifest-equality-and-immutable-version-imports.md) for normalized equality, internal fingerprints, version-content conflicts and the prohibition on replacing immutable Application versions.
- [x] Accept [ADR-016](docs/adr/0016-deployment-change-sets-plans-and-generations.md) for immutable deployment generations, ChangeSet intent, Plans, stale detection and atomic desired-state commits.
- [ ] Introduce ApplicationDeployment as the desired/applied state boundary for one Application in one Environment.
- [ ] Introduce DeploymentChangeSet with exact revision membership, expected generations and a complete cross-Application dependency plan.
- [ ] Keep ChangeSet planning non-mutating and atomically commit all target deployment generations before provider side effects.
- [ ] Keep import, plan and apply as separate use cases while allowing one high-level CLI command to orchestrate them.
- [ ] Persist only valid Manifest revisions and make `ApplicationId + ApplicationVersion` identify exactly one immutable declaration.
- [ ] Define the versioned `haby.json` application manifest and publish its JSON Schema.
- [ ] Implement metadata-based `System.Text.Json` source-generated manifest parsing with explicit discriminators and no reflection-based type discovery.
- [ ] Separate Component identity from Configuration document names and delivery paths.
- [ ] Define a format-neutral structured Configuration tree with SecretRef placeholders, JSON Pointer composition and deterministic JSON and YAML serializers.
- [ ] Keep raw text documents separate from structured documents and reject Document projections for raw text.
- [ ] Introduce Application sets with static membership or label selectors for reusable cross-cutting targeting.
- [ ] Introduce versioned Configuration profiles and explicit Profile assignments for global and group configuration.
- [ ] Represent Profile assignment references as structured `{ profileId, revision }` values pinned to exact immutable revisions.
- [ ] Define deterministic profile precedence, JSON Merge Patch and JSON Patch behavior, collision detection and per-path provenance.
- [ ] Model typed parameter definitions and defaults separately from scoped operator overrides and their provenance.
- [ ] Model generated values, secret references, external IDs and observed state separately from rendered documents.
- [ ] Replace `fromKey` configuration copying with explicit bindings from multiple components to one resource.
- [ ] Require explicit bindings before resource outputs can contribute to a component's configuration document.
- [ ] Require typed `local-resource` or `application-export` Binding targets and reject direct cross-application Resource references.
- [ ] Track Resource-export consumers and block incompatible owner-side deletion until an impact-visible plan is approved.
- [ ] Keep Resource lifecycle declaration-driven: Component removal must not implicitly remove a Resource that remains in `spec.resources`.
- [ ] Warn when a change removes the last known consumer from a still-declared Resource rather than deleting it.
- [ ] Separate domain models from EF Core entities and transport models where necessary.
- [ ] Split plugin responsibilities into resource provisioning, workload reconciliation, configuration rendering, publication, provider health checks and resource-scoped commands.
- [x] Accept the [Runtime Identity proposal](docs/architecture/runtime-identity.md) and record its terminology, trust boundaries and milestone split in [ADR-012](docs/adr/0012-runtime-identity-authentication.md).
- [ ] Define a transport-neutral `IRuntimeIdentityAuthenticator` contract.
- [ ] Define trusted authentication context, transient identity evidence, normalized authenticated runtime subjects and stable redacted failure codes without hosting, persistence or Kubernetes dependencies.
- [ ] Register Runtime Identity authenticators explicitly at build time and select them through trusted provider configuration.
- [ ] Implement a minimal Kubernetes TokenReview adapter and real-cluster contract test without adding delivery endpoints or ManagedWorkload reconciliation.
- [ ] Add plugin metadata: ID, version, compatible Haby API range, capabilities, resource types and versioned schemas.
- [ ] Support plugin settings validation and secret-field declarations.
- [ ] Define Secret, immutable SecretVersion, SecretRef, ownership and sensitivity independently from generated values and rendered documents.
- [ ] Define transport- and persistence-neutral `ISecretStore` and `IKeyEncryptionProvider` contracts with cancellation and redaction invariants.
- [ ] Define `IHabyModule` or an equivalent compile-time module registration API for DI and optional REST/gRPC endpoints.
- [ ] Package contracts and abstractions as versioned NuGet packages.
- [ ] Keep trusted plugins composed at build time initially; postpone arbitrary runtime DLL loading.

Exit criteria:

- `haby.json` can represent several components, several documents per component, shared resources and resources bound to only one component;
- local IDs use kebab-case and all polymorphic manifest alternatives deserialize through explicit source-generated contracts;
- one Component can consume an authorized export from another Application without acquiring lifecycle control over the exported Resource;
- removing a Component preserves every Resource that remains explicitly declared;
- Workload declarations are structurally contained by one Component while their Environment-specific ManagedWorkloads have independent persisted identity and state;
- removing a Component plans removal of its nested Workloads while preserving Resources that remain declared;
- one Application can target different Manifest revisions in different Environments;
- a ChangeSet freezes exact membership and becomes stale when a target deployment generation changes;
- a stale ChangeSet performs no desired-state or provider mutation;
- a stale Plan can be replaced for the same still-valid ChangeSet, while changed
  deployment preconditions require a new ChangeSet;
- every member is validated and planned before the ChangeSet starts external side effects;
- successful-import warnings are stored with the immutable ManifestRevision and
  are never changed by implicit or explicit revalidation;
- an existing `ApplicationId + ApplicationVersion` can never be replaced or
  overwritten; retry, resume and reconciliation reuse its desired revision;
- application rename preserves persisted identity and resource ownership;
- changing a manifest default does not overwrite an operator override;
- one profile revision can apply globally or to several overlapping Application sets without copying its content;
- component configuration can override a shared profile with deterministic, inspectable provenance;
- the same renderer and Document projection can produce semantically equivalent JSON and YAML documents;
- resources not explicitly bound to a document cannot leak into its rendered output;
- an external sample plugin can be developed without referencing Haby.Server;
- plugin compatibility failures are detected during startup;
- plugin and application configuration can be validated before execution;
- Runtime Identity can be authenticated through a fake adapter and a minimal real-cluster TokenReview adapter without exposing credentials, persisting RuntimeInstances or granting application permissions;
- plugins can produce and consume SecretRefs without returning plaintext through generated-value or diagnostic contracts.

### M3 — Reliable operations and reconciliation

Objective: prevent request failures from leaving unknown external state.

- [ ] Model provisioning as persisted operations with explicit states.
- [ ] Execute DeploymentChangeSets as durable, resumable operations with per-member progress.
- [ ] Support dependency-ordered stop-on-failure execution and make partial application explicit.
- [ ] Reconcile Resource desired state against observed state through the owning plugin.
- [ ] Reconcile ManagedWorkload desired state through its dedicated reconciler while sharing the durable operation infrastructure.
- [ ] Add idempotency keys, retries, timeouts and cancellation.
- [ ] Add leases so multiple Haby replicas cannot execute the same operation concurrently.
- [ ] Persist structured per-step results without leaking secrets.
- [ ] Implement the default encrypted PostgreSQL secret store with per-version DEKs, AES-256-GCM payload encryption and externally supplied versioned KEKs.
- [ ] Add durable KEK rotation and DEK rewrap operations that retain old keys until verification completes.
- [ ] Model secret rotation separately from key rotation and reconcile affected resources and workloads.
- [ ] Audit secret writes, resolutions, rotations and deletions without recording values.
- [ ] Add a generated-value store with stable ownership and get-or-create semantics outside Liquid rendering.
- [ ] Plan and apply Configuration-profile revision rollouts using the same operation history as application revisions.
- [ ] Add targeted operational overrides with reason, author, expiration and automatic rollback.
- [ ] Add `validate`, `plan`, `apply`, `reconcile` and `rollback` workflows.
- [ ] Define deletion policies: retain, orphan or deprovision external resources.
- [ ] Apply removal of applications, components and resources through plans instead of database cascades alone.
- [ ] Order Component and Resource removal so Workloads stop, consumer access is revoked and exports are checked before a Resource deletion policy runs.
- [ ] Separate desired replica count from temporary workload suspension and operational scaling overrides.
- [ ] Add compensation where safe and reconciliation where compensation is impossible.
- [ ] Add an operation history and audit log.
- [ ] Add OpenTelemetry traces, metrics, health and readiness checks.

Exit criteria:

- restarting Haby does not lose in-progress operations;
- repeated application of the same desired state is safe;
- partial external failures are visible and recoverable;
- a PostgreSQL backup does not disclose secret plaintext without the separately managed key ring;
- key rotation can resume safely after interruption.

### M4 — Public API and security

Objective: provide stable automation contracts suitable for third-party clients.

- [ ] Define versioned REST and gRPC APIs from the same application use cases.
- [ ] Add pagination, filtering, sorting and cancellation.
- [ ] Generate and publish API clients.
- [ ] Add OIDC authentication.
- [ ] Add service-account/API-key authentication for automation.
- [ ] Map authenticated runtime subjects through Runtime Identity bindings to persisted ManagedWorkloads and authorize exact Configuration-document revisions.
- [ ] Define immutable ConfigurationSnapshots that pin exact Configuration-document revisions to one ManagedWorkload generation.
- [ ] Add a single-request runtime configuration pull endpoint with authentication, authorization, secret materialization, audit, rate limiting and replay-aware policies.
- [ ] Define short-lived user-delegated DeveloperDeliverySessions and a separately disableable developer configuration endpoint without treating local processes as RuntimeInstances.
- [ ] Require explicit remote mode and target Environment for developer access; local Development mode alone must never connect to a remote Haby instance.
- [ ] Add optional hashed, one-time Delivery grants only if the pull protocol requires a second request.
- [ ] Add role- and scope-based authorization.
- [ ] Authorize cross-application Resource-export consumption independently from export declaration.
- [ ] Add write-only secret mutation APIs and separate permissions for create, replace, rotate and delete operations.
- [ ] Ensure list, read, export and diagnostic APIs return secret metadata and references but never plaintext.
- [ ] Add separate permissions for shared-profile changes, broad target selectors and break-glass overrides.
- [ ] Add authorization, confirmation and audit rules for plugin resource commands.
- [ ] Add rate limits and request-size limits.
- [ ] Map transport-neutral validation reports and stable error codes to
  versioned REST and gRPC DTOs and error details.
- [ ] Add export/import and supported break-glass patch/rollback commands.
- [ ] Add contract and backward-compatibility tests.

Exit criteria:

- administrative and read-only access can be separated;
- clients do not depend on database structure;
- public contract changes are checked in CI;
- ordinary API clients cannot retrieve secret plaintext or accidentally overwrite an unchanged secret.

### M5 — Administration UI

Objective: replace the prototype Bootstrap UI with a maintainable administration experience.

- [ ] Upgrade the client to the selected .NET 10 hosting model.
- [ ] Introduce MudBlazor and a Haby theme.
- [ ] Replace the navigation shell, forms, tables, dialogs and notifications.
- [ ] Add reusable domain components: page header, entity table, status badge, form actions and destructive-action confirmation.
- [ ] Integrate a code editor for JSON, YAML and Liquid templates with formatting, validation and diff.
- [ ] Add plan/apply and operation-status pages.
- [ ] Show manifest defaults, operator overrides, effective values and provenance separately.
- [ ] Add profile impact preview showing every affected application, component, document and JSON path.
- [ ] Add temporary multi-target diagnostic overrides with visible expiration.
- [ ] Show components, managed resources, bindings, workloads and configuration documents as distinct views.
- [ ] Add suspend/resume controls that preserve the desired workload scale.
- [ ] Add configuration revision history and rollback UX.
- [ ] Add write-only secret inputs that preserve existing values when unrelated settings change.
- [ ] Show secret presence, backend, version, ownership, provenance and rotation status without returning the value to the browser.
- [ ] Add explicit replace and rotate workflows with impact preview and confirmation.
- [ ] Add keyboard navigation and accessibility checks.
- [ ] Add bUnit tests for Haby-owned components and critical flows.

Do not wrap every MudBlazor primitive. Create wrappers only for recurring Haby domain semantics.

Exit criteria:

- no locally maintained generic table, modal or toast implementation remains;
- dangerous operations require explicit confirmation and show their scope;
- template errors are visible before apply;
- existing secrets never round-trip through UI forms as plaintext.

### M5.1 — Folder organization

Objective: add optional navigation-oriented organization after the core
administration UI and its Application views are stable.

- [ ] Introduce Folder as an operator-owned aggregate outside `haby.json`.
- [ ] Support an arbitrary Folder tree and optional single-parent Application
  placement without requiring every Application to belong to a Folder.
- [ ] Add persistence and API operations for creating, renaming, moving and
  deleting empty Folders and for moving Applications between them.
- [ ] Add Folder navigation, breadcrumbs and Application move workflows to the
  administration UI.
- [ ] Keep Folder paths out of Application, Resource and external object
  identity.
- [ ] Keep configuration profiles, authorization and policy assignment explicit;
  Folder containment does not imply inheritance.
- [ ] Keep labels, Application sets and search independent from Folder
  containment.

Exit criteria:

- an Application can remain unfiled or be moved between Folders without changing
  its identity, effective configuration, managed Resources or Workloads;
- renaming or moving a Folder changes navigation only;
- deleting a non-empty Folder is rejected or requires explicit relocation of its
  children and Applications;
- no configuration, authorization or lifecycle behavior is inferred from a
  Folder path.

### M6 — Standard resource providers and delivery channels

Objective: provide a useful product-neutral distribution.

- [ ] PostgreSQL database and principal resources, configuration renderers and scoped commands.
- [ ] RabbitMQ principal, queue, exchange and binding resources plus operational commands.
- [ ] Redis ACL and logical database resources where the target supports them.
- [ ] S3-compatible bucket and credential resources.
- [ ] Kubernetes workload and networking resources with standard profiles.
- [ ] Productionize the Kubernetes TokenReview Runtime Identity adapter with audience, Pod-bound identity, binding rotation and operational diagnostics.
- [ ] Kubernetes application and init-container pull with explicit projected ServiceAccount token files, Runtime Identity bindings and pinned ConfigurationSnapshots.
- [ ] Add a CLI-assisted local development workflow and configuration client using protected DeveloperDeliverySession credentials rather than committed settings or ordinary environment variables.
- [ ] ConfigMap and Secret publication.
- [ ] Generic HTTP/webhook publication.
- [ ] Optional Consul publisher only if it can remain product-neutral.
- [ ] Add external `ISecretStore` and `IKeyEncryptionProvider` adapters only when deployment requirements justify them, with OpenBao/Vault KV and Transit as initial candidates.
- [ ] Keep dynamic leased credentials behind a separate capability if a supported provider requires them.
- [ ] Integration-test containers for supported external systems.
- [ ] Plugin compatibility and upgrade documentation.

Exit criteria:

- every supported plugin has idempotency and integration tests;
- every resource type exposes versioned desired-state and output schemas;
- failures are represented through the common operation model;
- plugin-specific secrets are redacted consistently;
- the same SecretRef model works with encrypted PostgreSQL and at least one external adapter without changing domain contracts.

### M7 — Kubernetes operator mode

Objective: add Kubernetes-native reconciliation without making Kubernetes the mandatory database.

- [ ] Define a small CRD representing desired application/configuration state.
- [ ] Keep credentials in Kubernetes Secrets or an external secret store and reference them from the CRD.
- [ ] Use `.spec`, `.status`, conditions, observed generation and finalizers correctly.
- [ ] Add leader election and scoped RBAC.
- [ ] Add controller reconciliation tests.
- [ ] Reuse the Application, Resource, Binding and operation semantics instead of introducing a second Kubernetes-only domain model.
- [ ] Document GitOps ownership and drift behavior.
- [ ] Add export/import and disaster-recovery procedures.
- [ ] Evaluate a limited PostgreSQL-free single-cluster profile only after the operator is stable.

Exit criteria:

- the controller is idempotent and restart-safe;
- CRD status clearly reports applied and failed generations;
- Kubernetes mode does not weaken the standalone/PostgreSQL deployment.

### M8 — OSS release readiness

Objective: publish a supportable first modern Haby release.

- [ ] Versioned Docker images, NuGet packages and Helm charts.
- [ ] Release notes and upgrade guides.
- [ ] Supported-version and compatibility matrix.
- [ ] Security policy and vulnerability reporting process.
- [ ] Backup, restore and upgrade runbooks.
- [ ] Example deployment and sample plugin.
- [ ] Public roadmap, issue templates and contribution guide.
- [ ] End-to-end installation test from an empty environment.

## Next architecture work package

The first M2 implementation is **M2.1 — Manifest foundation**. It establishes a
side-effect-free vertical slice and does not combine manifest parsing with
plugin execution, Runtime Identity authentication or secret storage.

1. Introduce transport- and persistence-independent project boundaries for Domain, Manifest and Application code.
2. Define IDs and records for Application, ManifestRevision, ApplicationDeployment, DeploymentChangeSet, Component, Resource, Resource export, Binding, Configuration document and Workload.
3. Define source-generated import-envelope and `haby.json` DTOs plus the first checked-in JSON Schema.
4. Implement deterministic parsing and the import-time validation reports
   accepted in [ADR-014](docs/adr/0014-manifest-validation-diagnostics.md), with
   stable error codes, JSON Pointer paths and persisted historical warnings.
5. Add semantic tests for IDs, references, Resource lifecycle, nested Workloads, exact Profile revision references and JSON/YAML Document projections.
6. Define portable Application-version references and local revision references,
   then implement the normalized-declaration equality, conflict and no-replacement
   rules accepted in
   [ADR-015](docs/adr/0015-normalized-manifest-equality-and-immutable-version-imports.md)
   through in-memory repositories and focused tests.
7. Implement the immutable generation snapshots, ChangeSet membership, Plans,
   distinct stale errors and atomic in-memory commit contracts accepted in
   [ADR-016](docs/adr/0016-deployment-change-sets-plans-and-generations.md)
   without executing plugin side effects.
8. Remove legacy types only when their new vertical slice is covered; do not build permanent compatibility adapters or prototype-data migrations.

Definition of done for this work package:

- domain naming no longer depends on Consul keys or Kubernetes objects;
- the manifest parser is deterministic and side-effect free;
- validation diagnostics are bounded, deterministically ordered, redacted and
  use only the standard Haby code catalog;
- manifest parsing uses explicit `System.Text.Json` source-generated metadata and does not rely on shape-guessing converters or runtime type discovery;
- invalid imports do not create Manifest revisions, repeated equivalent imports of one Application version return the same revision, and changed content under that version is rejected;
- equality uses the manifest API's normalization profile, exact number semantics
  and authoritative normalized-content comparison rather than source bytes or a
  fingerprint alone;
- no import or apply contract exposes replace, overwrite or force-import
  semantics for an existing Application version;
- declaration identities remain portable while managed Resource and ManagedWorkload identities include Environment scope;
- ChangeSets store exact revision membership rather than dynamic selectors;
- ChangeSets bind to exact deployment generation or absence preconditions, Plans
  bind to exact planning inputs, and each exposes its distinct stable stale
  failure;
- every accepted desired generation has an immutable reproducible snapshot, and
  apply retry returns the existing ChangeSetCommit rather than incrementing it;
- Workloads are accepted only inside Components and need no `componentRef` in the manifest contract;
- direct cross-application Resource references and deletion of actively exported Resources are rejected before side effects;
- removing a Component does not remove a still-declared Resource, and an unused declared Resource produces a warning;
- focused tests and `git diff --check` pass.

## Decisions to record as ADRs

- ADR-001: Public core and downstream distribution boundary.
- ADR-002: PostgreSQL as the default source of truth.
- ADR-003: Plugin composition and runtime strategy.
- ADR-004: REST and gRPC public contract strategy.
- ADR-005: Background operation and reconciliation model.
- [ADR-006](docs/adr/0006-secret-storage-and-encryption.md): Secret storage, envelope encryption and external provider boundary.
- ADR-007: MudBlazor as the UI component foundation.
- ADR-008: Scope and ownership of Kubernetes operator mode.
- [ADR-009](docs/adr/0009-application-manifest-domain-model.md): Application, Component, Resource, Resource export, Binding, Workload and Configuration-document model.
- [ADR-010](docs/adr/0010-manifest-revisions-and-deployment-change-sets.md): Immutable Manifest revisions, Application deployments, state ownership and Deployment change sets.
- ADR-011: Reusable configuration profiles, target sets and deterministic composition.
- [ADR-012](docs/adr/0012-runtime-identity-authentication.md): Runtime Identity authentication and Kubernetes TokenReview.
- [ADR-013](docs/adr/0013-m2-manifest-foundation-boundaries-and-identifiers.md): M2.1 project boundaries, DTO/domain separation and identifier rules.
- [ADR-014](docs/adr/0014-manifest-validation-diagnostics.md): Transport-neutral manifest validation reports, diagnostics and historical warnings.
- [ADR-015](docs/adr/0015-normalized-manifest-equality-and-immutable-version-imports.md): Normalized manifest equality, immutable version imports and operational retry separation.
- [ADR-016](docs/adr/0016-deployment-change-sets-plans-and-generations.md): Environment deployments, immutable generations, ChangeSet intent, Plans and atomic desired-state commits.

## Out of scope for the first milestones

- Exact compatibility with product-specific administration clients.
- Compatibility with the unused prototype database, protobuf API or configurator contracts.
- Migration of prototype data or preservation of its EF migration history.
- A writable long-lived downstream fork of the OSS repository.
- Dynamic installation of untrusted plugin assemblies.
- External secret-store implementations before the encrypted PostgreSQL model and provider-neutral contracts are validated.
- Dynamic credential leasing and renewal before durable secret and operation lifecycles are established.
- Runtime provider protocols and out-of-process plugin lifecycle management.
- Configuration publisher implementations and the complete pull-delivery endpoint in the first M2 contract change.
- DeveloperDeliverySession contracts, local configuration clients and remote-development access during M2.
- Implicit configuration inheritance based on folder paths.
- A first-class product or release aggregate before a many-to-many grouping use case is validated.
- Using Kubernetes/etcd as the only Haby database.
- Reproducing every legacy Registry service provider before the core operation model is reliable.
- A visual redesign before build, tests and domain boundaries are stable.

## Prompt for the next development session

Use the following request to start the next implementation session:

> Continue the Haby OSS revival using `ROADMAP.md`, `docs/architecture/application-manifest.md`, `docs/adr/0009-application-manifest-domain-model.md`, `docs/adr/0010-manifest-revisions-and-deployment-change-sets.md`, `docs/adr/0013-m2-manifest-foundation-boundaries-and-identifiers.md`, `docs/adr/0014-manifest-validation-diagnostics.md`, `docs/adr/0015-normalized-manifest-equality-and-immutable-version-imports.md` and `docs/adr/0016-deployment-change-sets-plans-and-generations.md`. Implement only **M2.1 — Manifest foundation** from the Next architecture work package. Add side-effect-free Domain, Manifest and Application contracts, source-generated DTOs, the checked-in JSON Schema, deterministic validation, immutable revision import semantics and exact ChangeSet membership/generation contracts with focused tests. Do not add persistence, execute plugins, implement Runtime Identity or secret stores, migrate the UI, preserve legacy API compatibility or add remote providers in the same change. Remove legacy code only when its replacement is covered, run focused and solution validation plus `git diff --check`, and report environmental blockers separately from code failures.
