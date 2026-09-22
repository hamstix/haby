# ADR-010: Manifest revisions and deployment change sets

- Status: Accepted
- Date: 2026-09-22

## Context

Haby imports versioned Application declarations and applies them to one or more
Environments. A typical operator workflow selects several Applications from a
release bundle by labels, validates them together, previews their combined impact
and applies them to one Environment with one CLI command. The producer may build
those declarations from a private source-control system, but repository details
are not part of the portable contract delivered to an installation.

Importing source declarations, selecting desired state and executing external
changes have different failure and consistency boundaries. Haby cannot provide a
single ACID transaction across PostgreSQL, Kubernetes, RabbitMQ and other
providers. It must nevertheless prevent a late validation error from starting a
partially known rollout, preserve exact provenance and make interrupted work
visible and resumable.

The current prototype has never been deployed. Haby therefore does not need data,
API or behavior compatibility with its legacy ConfigurationUnit model. Work will
remain incremental to keep each change reviewable and the repository buildable,
not to preserve the old model as a supported public contract.

## Decision

### Application identity and imported revisions are separate

`Application` is the stable catalog identity identified logically by
`metadata.id`. Display metadata may change without replacing the Application.

The first successful import of an Application version creates an immutable
`ManifestRevision` containing at least:

- the Application identity and an Application-local monotonic revision number;
- the manifest API version and Application version supplied by the producer;
- the accepted structured declaration and its validated normalized
  representation;
- import time and actor metadata.

For one Application identity, an `ApplicationVersion` identifies exactly one
immutable declaration. Development builds therefore use unique versions such as
`3.4.6-rev.1042`; a producer must publish a new version whenever the declaration
changes. Haby does not require a particular versioning scheme or derive ordering
from the version string.

The Application version is the portable release identity. The monotonic revision
number is local to one Haby installation and is never used as a portable bundle
identity. Several product releases may reference the same Application version
without creating additional Manifest revisions.

A portable Application-version reference is structured:

```json
{
  "applicationId": "com.example.automation",
  "applicationVersion": "3.4.6-rev.1042"
}
```

After import, Haby resolves that reference to its local exact revision reference:

```json
{
  "applicationId": "com.example.automation",
  "revision": 7
}
```

Haby does not persist `latest` or version ranges as deployment inputs. A release
bundle, UI or CLI may select an Application version, but the ChangeSet resolves
that choice to a local revision before persistence.

### Import is side-effect free

Import validates and stores source declarations. It does not select an
Environment, provision Resources, reconcile Workloads or publish Configuration
documents.

The import request contains `applicationVersion` metadata and a structured JSON
`manifest` value. It does not require the original file bytes, a client-computed
digest or source-repository metadata. The import pipeline is:

1. decode UTF-8 JSON and reject duplicate properties;
2. validate the core JSON Schema and root contract version;
3. deserialize through generated `System.Text.Json` metadata;
4. validate IDs, local references and manifest invariants;
5. validate known plugin type identifiers and plugin-owned property schemas;
6. store an immutable revision only after every import-time check succeeds.

An invalid import returns stable error codes and JSON paths but does not create a
`ManifestRevision`. Draft editing, if later required, is a separate concept and
does not weaken revision invariants.

Provider aliases, cross-Application exports, authorization and current external
state are Environment-specific and are resolved during planning rather than
import.

### Version immutability and idempotency are explicit

`ApplicationId + ApplicationVersion` is unique. Repeating an import with the same
Application identity, version and equivalent normalized declaration returns the
existing revision. Reusing that identity and version with a different
declaration fails with a stable version-content conflict and creates no revision.
A new Application version creates a new ManifestRevision even when its
declaration is equivalent to an earlier version.

Haby may maintain an internal `ManifestFingerprint` of the normalized declaration
to optimize equality checks and diagnostics. It is server-generated and is not a
required import field or a portable identity. Its deterministic representation
and algorithm must be versioned before the fingerprint becomes a public
contract; semantic equality remains authoritative.

Private producer provenance such as repository URI, commit, pipeline and source
path remains on the producer side of the delivery boundary. It must not be
required by a customer installation or disclosed in a portable Application
manifest. Future release metadata may identify a publisher, release bundle and
signature without changing ManifestRevision identity.

Exact-byte SHA-256 values belong to a future physical bundle format. A bundle
index may authenticate or verify its individual files, but those artifact
digests are distinct from a logical Manifest fingerprint and from a ChangeSet
plan-input fingerprint.

### ApplicationDeployment owns desired state in one Environment

`ApplicationDeployment` is the stable relationship between one Application and
one Environment. It contains at least:

- the desired ManifestRevision;
- a monotonically increasing desired generation;
- the last successfully applied generation and ManifestRevision;
- an optimistic concurrency token;
- summarized status and references to detailed plans and operations.

The same Application may target different revisions in different Environments.
Changing a manifest revision, relevant override, Profile assignment or another
desired input increments the deployment generation.

A plan is pinned to the deployment generation and exact input references. If any
target generation changes before apply, the plan is stale and must be recomputed.

### Declaration identity and Environment instances are distinct

Manifest declarations are portable across Environments. Managed instances are
Environment-scoped:

```text
Resource declaration
  = Application ID + Resource local ID

Managed Resource instance
  = Environment ID + Application ID + Resource local ID

Workload declaration
  = Application ID + Component local ID + Workload local ID

Managed Workload instance
  = Environment ID + Application ID + Component local ID + Workload local ID
```

Persisted instances additionally receive immutable internal IDs. Provider
outputs, external IDs, SecretRefs and observed state belong to those
Environment-scoped instances, not to the portable ManifestRevision.

### DeploymentChangeSet is the batch planning boundary

A `DeploymentChangeSet` describes an exact group of ApplicationDeployment
changes for one Environment. It contains:

- the target Environment;
- exact Application revision references;
- the expected generation of each existing ApplicationDeployment;
- an expected-absence precondition for each new ApplicationDeployment;
- the resolved membership snapshot;
- dependency and impact information;
- a fingerprint of all planning inputs;
- references to its plan and execution operation.

Selectors such as `module=processing` are authoring or CLI conveniences. They
are resolved against the incoming bundle before the ChangeSet is stored. A
persisted ChangeSet never retains a dynamic selector whose membership could
change between plan and apply.

Planning validates the complete ChangeSet before external side effects. It
resolves Provider aliases, exact Profile revisions, overrides, Resource exports
and authorization, then builds a dependency graph across Applications. This
allows an exporting Application to be planned before consumers in the same
batch.

Creating and planning a ChangeSet do not mutate ApplicationDeployment desired
state. Starting apply first verifies all expected generations and expected
absences, then atomically commits the target desired Manifest revisions and new
generations for every member in Haby's PostgreSQL transaction. Failure of this
concurrency check makes the plan stale and starts no external work.

After this desired-state commit, external execution is intentionally durable. A
provider failure does not restore the previous desired revisions; the operation
remains resumable toward the committed target. Returning to an earlier target
requires a new rollback ChangeSet.

### One CLI command orchestrates separate operations

The common UX is one high-level command that accepts a release bundle and an
optional label selector. It performs:

```text
resolve membership
  -> validate and import revisions
  -> create DeploymentChangeSet
  -> plan
  -> present impact and request approval
  -> apply and optionally wait
```

The CLI context may provide a default server, Environment and authentication
profile, but every mutating server request identifies the Environment
explicitly. Credentials are obtained from an appropriate protected source rather
than stored as plaintext in ordinary CLI configuration.

Haby also exposes lower-level validate, import, plan, apply and operation-status
use cases for CI and diagnostics. A downstream release tool may orchestrate the
same generic API without introducing product or module concepts into Haby OSS.

### Apply is durable but not globally atomic

All manifests and Environment-specific dependencies are validated before apply
starts. Apply follows the ChangeSet dependency graph and records durable progress
per Application, Resource and Workload.

Haby does not claim all-or-nothing rollback across external providers. A failure
can leave a visible partially applied ChangeSet. The execution model must support
safe retry and resume through idempotent operations. The default policy stops
new dependent work after failure; continuing independent branches may be added
as an explicit policy.

Execution state and leases belong to the operation model finalized in M3. This
ADR establishes that the operation is pinned to the planned ChangeSet and input
fingerprint and is not implemented as one long-running request.

### Rollback creates new desired state

Rollback selects an earlier ManifestRevision in a new deployment generation and
uses the normal plan and apply workflow. It never mutates or copies an old
revision. Configuration rollback does not imply that an external credential or
deleted Resource can be restored; the plan reports those limitations.

### State ownership remains explicit

| State | Owner | Behavior |
| --- | --- | --- |
| Application logical ID | Haby catalog/source contract | Stable |
| Manifest metadata, declarations and defaults | ManifestRevision | Immutable |
| Application version | Producer/import envelope | Immutable and unique within an Application |
| Private Git repository, commit, path and pipeline | Producer CI/CD | Not part of the portable Haby contract |
| Release/bundle membership | Release producer | May reference an existing Application version from multiple releases |
| Folder placement | Operator | Mutable without changing identity |
| Provider instances and aliases | Environment operator | Mutable and versioned |
| Desired ManifestRevision | ApplicationDeployment | Mutable; increments generation |
| Parameter and operational overrides | Operator | Separate, versioned inputs |
| Profile revisions | Profile source | Immutable |
| Profile assignments | Operator or policy | Separate, versioned inputs |
| Generated values, external IDs and observed state | Managed instance/reconciler | Persisted separately |
| Secret versions | Secret store | Immutable versions |
| Configuration-document revisions | Haby composition pipeline | Immutable derived output |
| Plans, operations and audit events | Haby | Immutable or append-only |

### Legacy compatibility is not required

The old ConfigurationUnit, ConfigurationKey, OrganizationUnit, plugin strategy,
protobuf and database contracts may be removed after their new vertical slice is
covered by focused tests. No migration from the prototype database is required,
and persistence may start with a new initial schema.

Implementation remains incremental: introduce and test a new slice, switch the
composition root, then delete the replaced legacy slice. Temporary coexistence
inside the repository does not create a public compatibility promise or require
permanent adapters.

## Consequences

### Positive

- CI can validate and import without changing infrastructure.
- Installations receive portable Application versions without private Git
  repository metadata.
- Reimporting an Application version is idempotent, while changing its
  declaration without changing the version is rejected.
- One operator command can safely coordinate a group of Applications.
- Cross-Application dependencies are planned as one exact snapshot.
- The same Application can progress independently across Environments.
- Plans are reproducible and cannot silently apply after desired inputs change.
- Planning is non-mutating, while apply commits the complete desired-state batch
  before any provider side effect.
- Partial external failures remain visible and resumable.
- The unused prototype does not impose permanent schema or API migration cost.

### Negative

- Haby must model ApplicationDeployment and DeploymentChangeSet in addition to
  ManifestRevision.
- Batch apply cannot promise global atomic rollback.
- Environment/provider/profile revisions contribute to plan fingerprints and
  require concurrency checks.
- Revision history can contain semantically identical declarations under
  different Application versions.
- Producers must assign a new Application version to every declaration change,
  including manifest-only development builds.

## Rejected alternatives

### Import and provision in one request handler

This prevents safe validation, approval, multi-Environment rollout and durable
recovery when external work outlives the request.

### Apply Applications one by one without a ChangeSet

A later invalid manifest could be discovered after earlier Applications changed.
Cross-Application exports could be applied in the wrong order, and the operator
would have no stable view of the release-wide impact.

### Persist a dynamic selector as apply scope

Labels can change after planning. ChangeSets therefore freeze exact revision
membership and expected deployment generations.

### Identify revisions by Git source or exact manifest-file bytes

Private repository coordinates are not portable across the delivery boundary,
and Haby may receive a declaration constructed by CI, a CLI, a UI or another API
without an original file. Haby therefore imports structured declarations and
uses Application identity plus Application version for portable idempotency.
Raw file digests remain an artifact/bundle concern.

### Treat ChangeSet apply as an ACID transaction

Independent external systems cannot participate in one reliable database
transaction. Durable progress, idempotency and reconciliation are used instead.

### Preserve legacy contracts through permanent adapters

The prototype has no deployed consumers or data. Permanent compatibility layers
would increase complexity without protecting a real user.

## Follow-up work

- define source-generated import, Application-version reference, local
  revision-reference and validation-error DTOs;
- define deterministic normalized-declaration equality and the stable
  version-content conflict contract;
- define plan input fingerprinting and stale-plan error contracts;
- specify DeploymentChangeSet and operation status contracts in ADR-005;
- decide versioning of ProviderInstance configuration and other planning inputs;
- add CLI/API design after the application use cases are stable.
