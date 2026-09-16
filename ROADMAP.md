# Haby OSS revival roadmap

This roadmap describes the work required to turn Haby from a working prototype into a maintainable open-source configuration and provisioning platform.

The immediate goal is to establish a stable OSS core that downstream distributions can extend without carrying permanent forks.

## Product boundaries

### Haby OSS

Haby OSS owns:

- configuration units, applications, environments and external services;
- configuration templates, rendering, validation and revisions;
- generated values and references to secrets;
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
- Prefer migrations and compatibility adapters over flag-day rewrites.
- Keep domain logic independent from ASP.NET Core, EF Core, UI and plugin implementations.
- Treat provisioning as an idempotent, retryable operation, not as a side effect of a request handler.
- Never store plaintext secrets in logs, operation results or ordinary configuration documents.
- Keep PostgreSQL as the default source of truth. Kubernetes support is an integration and an optional deployment mode, not the only storage model.
- Use semantic versioning for public contracts and plugin abstractions.
- Use MudBlazor for standard UI components; implement only Haby-specific UX components locally.

## Target solution shape

The exact project names may change through ADRs, but dependencies should move toward this structure:

```text
Haby.Domain
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

- [ ] Migrate all projects to .NET 10.
- [ ] Upgrade gRPC, EF Core, Npgsql, Fluid and Kubernetes client dependencies.
- [ ] Replace externally owned field-mask helpers with OSS-owned contracts and utilities.
- [ ] Replace private web, hosting and HTTP-client infrastructure with ASP.NET Core and OpenTelemetry equivalents owned by Haby or the platform.
- [ ] Replace obsolete IdentityServer4 integration with a neutral plugin boundary; keep product-specific implementations outside the public repository.
- [ ] Enable nullable and analyzer warnings consistently.
- [ ] Add dependency and license scanning.

Exit criteria:

- no organization-specific or private package is required to build or run Haby OSS;
- all projects target .NET 10;
- baseline and regression tests pass.

### M2 — Stable domain and plugin SDK

Objective: separate Haby's domain from hosting and make extensions a supported public surface.

- [ ] Define domain terminology and invariants in an ADR.
- [ ] Separate domain models from EF Core entities and transport models where necessary.
- [ ] Introduce stable IDs and optimistic concurrency tokens.
- [ ] Split plugin responsibilities into provisioning, validation, publishing and optional commands.
- [ ] Add plugin metadata: ID, version, compatible Haby API range, capabilities and configuration schema.
- [ ] Support plugin settings validation and secret-field declarations.
- [ ] Define `IHabyModule` or an equivalent compile-time module registration API for DI and optional REST/gRPC endpoints.
- [ ] Package contracts and abstractions as versioned NuGet packages.
- [ ] Keep trusted plugins composed at build time initially; postpone arbitrary runtime DLL loading.

Exit criteria:

- an external sample plugin can be developed without referencing Haby.Server;
- plugin compatibility failures are detected during startup;
- plugin configuration can be validated before execution.

### M3 — Reliable operations and reconciliation

Objective: prevent request failures from leaving unknown external state.

- [ ] Model provisioning as persisted operations with explicit states.
- [ ] Add idempotency keys, retries, timeouts and cancellation.
- [ ] Add leases so multiple Haby replicas cannot execute the same operation concurrently.
- [ ] Persist structured per-step results without leaking secrets.
- [ ] Add `validate`, `plan`, `apply`, `reconcile` and `rollback` workflows.
- [ ] Define deletion policies: retain, orphan or deprovision external resources.
- [ ] Add compensation where safe and reconciliation where compensation is impossible.
- [ ] Add an operation history and audit log.
- [ ] Add OpenTelemetry traces, metrics, health and readiness checks.

Exit criteria:

- restarting Haby does not lose in-progress operations;
- repeated application of the same desired state is safe;
- partial external failures are visible and recoverable.

### M4 — Public API and security

Objective: provide stable automation contracts suitable for third-party clients.

- [ ] Define versioned REST and gRPC APIs from the same application use cases.
- [ ] Add pagination, filtering, sorting and cancellation.
- [ ] Generate and publish API clients.
- [ ] Add OIDC authentication.
- [ ] Add service-account/API-key authentication for automation.
- [ ] Add role- and scope-based authorization.
- [ ] Add rate limits and request-size limits.
- [ ] Add structured validation errors and stable error codes.
- [ ] Add export/import and supported break-glass patch/rollback commands.
- [ ] Add contract and backward-compatibility tests.

Exit criteria:

- administrative and read-only access can be separated;
- clients do not depend on database structure;
- public contract changes are checked in CI.

### M5 — Administration UI

Objective: replace the prototype Bootstrap UI with a maintainable administration experience.

- [ ] Upgrade the client to the selected .NET 10 hosting model.
- [ ] Introduce MudBlazor and a Haby theme.
- [ ] Replace the navigation shell, forms, tables, dialogs and notifications.
- [ ] Add reusable domain components: page header, entity table, status badge, form actions and destructive-action confirmation.
- [ ] Integrate a code editor for JSON and Liquid templates with formatting, validation and diff.
- [ ] Add plan/apply and operation-status pages.
- [ ] Add configuration revision history and rollback UX.
- [ ] Add keyboard navigation and accessibility checks.
- [ ] Add bUnit tests for Haby-owned components and critical flows.

Do not wrap every MudBlazor primitive. Create wrappers only for recurring Haby domain semantics.

Exit criteria:

- no locally maintained generic table, modal or toast implementation remains;
- dangerous operations require explicit confirmation and show their scope;
- template errors are visible before apply.

### M6 — Standard plugins and delivery channels

Objective: provide a useful product-neutral distribution.

- [ ] PostgreSQL provisioning.
- [ ] RabbitMQ provisioning.
- [ ] Redis ACL provisioning.
- [ ] S3-compatible configuration.
- [ ] Kubernetes resource publication.
- [ ] ConfigMap and Secret publication.
- [ ] Generic HTTP/webhook publication.
- [ ] Optional Consul publisher only if it can remain product-neutral.
- [ ] Integration-test containers for supported external systems.
- [ ] Plugin compatibility and upgrade documentation.

Exit criteria:

- every supported plugin has idempotency and integration tests;
- failures are represented through the common operation model;
- plugin-specific secrets are redacted consistently.

### M7 — Kubernetes operator mode

Objective: add Kubernetes-native reconciliation without making Kubernetes the mandatory database.

- [ ] Define a small CRD representing desired application/configuration state.
- [ ] Keep credentials in Kubernetes Secrets or an external secret store and reference them from the CRD.
- [ ] Use `.spec`, `.status`, conditions, observed generation and finalizers correctly.
- [ ] Add leader election and scoped RBAC.
- [ ] Add controller reconciliation tests.
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

## First work package

The first implementation session should complete only a narrow baseline package. Do not start the plugin redesign or UI migration in the same change.

1. Inspect the clean Git status, SDK selection and current package restore state.
2. Reproduce the existing build using a writable, clean artifacts directory.
3. Add `global.json` for .NET 10 only after confirming the installed stable SDK.
4. Add initial test projects without changing production behavior.
5. Lock configuration rendering and generated-variable persistence with focused tests.
6. Add a failing regression test for cross-CU variable deletion.
7. Fix that defect with the smallest scoped production change.
8. Run build, tests and `git diff --check`.
9. Record remaining baseline failures separately; do not hide them inside the migration.

Definition of done for the first work package:

- focused regression tests pass;
- the solution has a reproducible build command;
- no unrelated production refactoring is included;
- all remaining warnings or blocked checks are explicitly documented.

## Decisions to record as ADRs

- ADR-001: Public core and downstream distribution boundary.
- ADR-002: PostgreSQL as the default source of truth.
- ADR-003: Compile-time trusted plugin composition before dynamic loading.
- ADR-004: REST and gRPC public contract strategy.
- ADR-005: Background operation and reconciliation model.
- ADR-006: Secret storage and redaction model.
- ADR-007: MudBlazor as the UI component foundation.
- ADR-008: Scope and ownership of Kubernetes operator mode.

## Out of scope for the first milestones

- Exact compatibility with product-specific administration clients.
- A writable long-lived downstream fork of the OSS repository.
- Dynamic installation of untrusted plugin assemblies.
- Using Kubernetes/etcd as the only Haby database.
- Reproducing every legacy Registry service provider before the core operation model is reliable.
- A visual redesign before build, tests and domain boundaries are stable.

## Prompt for the next development session

Use the following request to start the first implementation session:

> Continue the Haby OSS revival using `ROADMAP.md`. Implement only the **First work package / M0 reproducible baseline**. Inspect the real repository state before editing. First reproduce the current build and add focused tests for configuration rendering, generated-variable persistence and the cross-CU `DeleteVariable` regression. Make the smallest production fix required by those tests. Do not begin the plugin redesign, MudBlazor migration or downstream compatibility layers. Validate with build, focused tests and `git diff --check`, and report any environmental blocker separately from code failures.
