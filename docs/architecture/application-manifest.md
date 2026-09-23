# Application manifest and domain model

- Status: Accepted
- Target milestone: M2
- Decision records: [ADR-009](../adr/0009-application-manifest-domain-model.md),
  [ADR-010](../adr/0010-manifest-revisions-and-deployment-change-sets.md),
  [ADR-013](../adr/0013-m2-manifest-foundation-boundaries-and-identifiers.md),
  [ADR-014](../adr/0014-manifest-validation-diagnostics.md),
  [ADR-015](../adr/0015-normalized-manifest-equality-and-immutable-version-imports.md)

## Purpose

Haby manages more than configuration files. It describes an application, creates
or adopts resources that the application depends on, renders consumer-facing
configuration, and can publish or deploy the resulting artifacts.

The original model uses a configuration key as several concepts at once:

- a separately runnable part of an application;
- an output document name;
- the ownership scope for generated values;
- the identity used to share an external resource;
- and, for Kubernetes, the description of deployment resources.

That coupling makes rename, sharing, overrides, reconciliation and deletion hard
to reason about. The target model separates these concepts while retaining a
simple manifest for the common case.

## Terminology

| Term | Meaning | Replaces or clarifies |
| --- | --- | --- |
| Folder | A future optional organizational container for folders and applications, scheduled for M5.1 | Organization unit |
| Application | An independently versioned software and ownership boundary, commonly represented by one source repository | Configuration unit, modular microservice |
| Component | A separately runnable part of an application, such as an API, worker, scheduler or compiler | Configuration key when it was used as a nanoservice |
| Workload | A portable declaration of how a Component should run | Kubernetes data embedded in a configuration key |
| ManagedWorkload | Environment-specific desired and observed state produced from one Workload declaration | A portable Workload declaration or an individual replica |
| RuntimeInstance | A concrete running replica, task or process such as one Kubernetes Pod | A Workload declaration or persisted ManagedWorkload |
| Configuration document | A named rendered document consumed by a component, such as `appsettings.json` | Configuration key when it was used as an output document |
| Provider instance | A configured external system that Haby can manage, such as one PostgreSQL cluster or RabbitMQ broker | Service |
| Resource | A desired and observed dependency managed through a provider instance, such as a database, principal, queue, bucket or network endpoint | Configuration-unit-at-service association |
| Resource export | A stable, owner-controlled contract that permits another application to consume selected capabilities of a resource without exposing its internal declaration | Direct references to another application's configuration key or resource |
| Binding | An explicit relationship through which a component consumes a local resource or another application's resource export and may project selected outputs into a configuration document | `fromKey` and implicit JSON copying |
| Document projection | A format-neutral mapping that renders Binding outputs into a structured Configuration document at an explicit path | Implicit insertion of provider JSON into a configuration key |
| Parameter definition | A typed input declared by an application manifest, including an optional default | Template parameter |
| Parameter override | An operator-supplied value stored independently from the manifest default | A template parameter value overwritten during every update |
| Application set | A named static membership or label selector used to target several applications without changing folder containment | Module-like operational grouping |
| Configuration profile | A reusable, versioned configuration fragment such as common observability or logging defaults | Common configuration copied between hierarchy levels |
| Profile assignment | An explicit relationship that applies a profile revision to an environment, application set or target selector | Implicit folder-based inheritance |
| Manifest revision | An immutable imported revision of an application declaration with source and version metadata | The mutable template and previous-version pair |
| Application deployment | The desired and applied state of one Application in one Environment, tracked by generation | Implicit installation state mixed into a configuration unit |
| Deployment change set | An exact, planned group of Application-deployment changes for one Environment | Sequential best-effort application updates selected at execution time |

`Component` is the recommended replacement for *nanoservice*. It describes the
source-level relationship without claiming that every part is an independently
owned microservice. `Workload` is reserved for a deployed runtime representation
of a component.

This follows common software-catalog terminology, where a component is an
individual software piece and a resource is infrastructure used by software. It
also keeps the Kubernetes meaning of workload limited to objects that manage
runtime execution.

## Model overview

The model has independent organizational, software, runtime and delivery axes:

```text
Folder (optional, M5.1)
  `- Application
       |- Manifest revision
       |- Component
       |    |- Resource binding
       |    |- Configuration document
       |    `- Workload declaration
       |- Resource
       |    `- Provider instance
       `- Resource export -> Resource

Environment
  |- Provider instance aliases
  |- Parameter overrides
  |- Operational overrides
  |- Application deployment -> desired/applied Manifest revision
  `- Deployment change set -> exact deployment/revision membership

Application set
  `- Static application membership or label selector

Configuration profile
  `- Profile assignment -> environment/application set/selector

Distribution (optional, future)
  `- Versioned application-revision membership
```

A folder path is not an application identity or configuration scope. Moving an
application between folders must not rename external resources or change its
effective configuration.

An environment is a deployment and override boundary. The first implementation
may expose one default environment per Haby installation, but persisted state
must not assume that folder paths represent environments.

## Identity and rename rules

- Persisted aggregates receive aggregate-specific UUIDv7 value types. Portable
  declaration identities remain separate from those internal persisted IDs.
- `metadata.id` in the manifest is a stable, opaque logical `ApplicationId`. It
  is unique within one Haby installation and is not derived from a Git repository,
  folder, organization or product name.
- An `ApplicationId` is lowercase ASCII, has at most 128 characters and matches
  `^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$`. Both kebab-case and reverse-DNS values
  are valid; reverse-DNS is optional publisher guidance rather than a validation
  requirement.
- Object keys such as `api`, `worker` and `main-database` are stable
  manifest-local IDs. All manifest-local IDs use lowercase kebab-case.
  Human-facing names are separate and mutable.
- A Workload local ID is unique within its containing Component. Its logical
  declaration identity is Application ID, Component local ID and Workload local
  ID; each Environment-specific ManagedWorkload receives its own immutable
  persisted ID. Moving a Workload declaration between Components is replacement
  unless an explicit migration associates it with existing state.
- A resource is owned by the Application whose `spec.resources` collection
  declares it. The manifest does not contain `ownerRef`; ownership is structural
  and cannot be redirected to a Component or another Application.
- Resource and Workload declarations are portable. Their managed instances are
  scoped by Environment and receive separate immutable persisted IDs.
- `metadata.id` is the stable Application identity, and `applicationVersion`
  identifies one immutable declaration within that Application. Repository URL,
  commit, branch, pipeline and source path are private producer provenance, not
  portable Haby identity.
- Generated external names and external object IDs are persisted as resource
  state. Renaming or moving an application does not regenerate them.
- Changing a stable local ID means replacement unless an explicit migration or
  `movedFrom` operation associates it with existing state.
- Destructive external renames are always planned operations and are never an
  implicit consequence of editing display metadata.

These rules make ordinary rename and folder moves safe while keeping resource
replacement explicit. Changing `ApplicationId` is an explicit identity migration
rather than an ordinary rename and is outside M2.1. The complete project,
serialization and persisted-ID boundaries are recorded in
[ADR-013](../adr/0013-m2-manifest-foundation-boundaries-and-identifiers.md).

## Manifest name and envelope

The recommended repository file name is **`haby.json`**. The file is an
application manifest, not a rendering template. The short product-specific name
is easy to discover and avoids collisions with generic files named
`template.json` or `manifest.json`.

The manifest contains a schema version and a stable application ID. The
Application version is supplied by the producer as import metadata so that the
manifest does not duplicate a version maintained elsewhere. Repository, commit,
pipeline and source-path details are private producer provenance and are not part
of the portable import contract delivered to an installation.

A CI import conceptually contains:

```json
{
  "applicationVersion": "3.4.6-rev.1042",
  "manifest": {}
}
```

The Application ID is read from `manifest.metadata.id` and is not duplicated in
the import envelope. The manifest is a structured JSON value rather than an
opaque copy of the original file bytes. The operation is an idempotent import of
a valid immutable Manifest revision. Applying the revision is a separate
operation so installations can choose automatic apply, approval, scheduling or
plan-only behavior.

Within one Application, each `applicationVersion` identifies exactly one
immutable declaration. Reimporting the same version and an equivalent normalized
declaration returns the existing revision. Reusing the same version with a
different declaration is a conflict. A new version creates a new Haby revision
even if its declaration is semantically equivalent. Development pipelines can
enforce this rule with versions such as `3.4.6-rev.1042`, while customer release
bundles pin the exact Application versions selected for delivery.

Haby may compute an internal deterministic fingerprint of the normalized
declaration for efficient comparison, but the client does not supply a manifest
digest and the fingerprint is not the revision identity. SHA-256 values for
physical manifest files belong to a future release-bundle format, not to this
import envelope.

### Normalized declaration equality

[ADR-015](../adr/0015-normalized-manifest-equality-and-immutable-version-imports.md)
defines equality independently from source bytes and serializer output. Object
order, JSON formatting, equivalent number spellings and omitted core defaults do
not change the normalized declaration. Array order, strings, raw text, labels and
all other meaningful declaration content remain significant unless a versioned
field contract explicitly says otherwise.

Normalization semantics are selected by manifest `apiVersion`. An internal
versioned SHA-256 fingerprint may accelerate comparison, but matching fingerprints
are always followed by authoritative normalized-content comparison.

Haby never replaces or overwrites an existing
`ApplicationId + ApplicationVersion`. Equal reimports return the first accepted
revision and its historical warnings; different content returns a safe
version-content conflict containing changed paths but no values. Re-executing
provider work is retry, resume or reconciliation against existing desired state,
not manifest replacement.

## Revision and deployment lifecycle

The declaration and its `ApplicationId + ApplicationVersion` reference are
portable, while the ManifestRevision ID and monotonic revision number are local
to one Haby installation. Import has no Environment side effects. A revision is
created only after core, semantic and plugin-schema validation succeeds. Invalid
imports return stable error codes and JSON paths but are not stored as revisions.

`ApplicationDeployment` relates one Application to one Environment. It records
the desired Manifest revision, a desired generation, the last successfully
applied generation and optimistic concurrency state. The same Application can
therefore run different revisions in different Environments.

Changing the desired manifest or another effective input increments generation.
A plan records the exact generation and input fingerprint; apply rejects a stale
plan if any target deployment changes before execution.

A `DeploymentChangeSet` freezes a group update for exactly one Environment. A
release bundle identifies portable `ApplicationId + ApplicationVersion` pairs;
after import, Haby resolves them to exact local Manifest revision references and
expected deployment generations. The persisted ChangeSet contains those local
references, not a dynamic label selector. A CLI or release tool resolves a
selector against the incoming bundle before creating the ChangeSet.

Creating and planning a ChangeSet do not mutate ApplicationDeployment desired
state. When apply starts, Haby checks every expected generation (or expected
absence for a new deployment) and atomically commits all target desired revisions
and new generations in its own PostgreSQL transaction before external side
effects. If that check fails, the plan is stale and no provider work starts.
After the desired-state commit, partial external failure remains visible and the
operation can resume toward the committed target.

The normal high-level workflow is:

```text
resolve bundle membership
  -> validate and import every Manifest revision
  -> create DeploymentChangeSet
  -> resolve Environment dependencies and plan the complete set
  -> show impact and request approval
  -> apply through a durable operation
```

All members are validated and the cross-Application dependency graph is planned
before external side effects. Apply is resumable and idempotent but is not an
ACID transaction across providers; partial progress remains visible after a
failure. See [ADR-010](../adr/0010-manifest-revisions-and-deployment-change-sets.md)
for state ownership, rollback and idempotency rules.

## Manifest invariants

The parser and semantic validator enforce these rules before persistence or
plugin execution:

- the document has exactly one Application identity in `metadata.id`;
- every manifest-local collection key matches
  `^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$`;
- every local reference resolves within the same manifest and has the expected
  target kind;
- every Resource and Workload `type` includes an explicit contract version;
- every Resource is owned by the declaring Application and has no overridable
  `ownerRef`;
- a Binding target has exactly one registered discriminator: `local-resource`
  or `application-export`;
- direct cross-application Resource references are invalid;
- a Workload is declared inside exactly one Component and has no redundant
  `componentRef`;
- a Document projection references an existing Configuration document owned by the
  consuming Component;
- document declaration and document delivery remain separate concerns.

Import is side-effect free. Schema and semantic errors are returned together
where practical and use stable JSON paths and error codes.

### Validation diagnostics

Validation produces the transport-neutral report defined by
[ADR-014](../adr/0014-manifest-validation-diagnostics.md). Diagnostics use stable
Haby codes and JSON Pointers rooted at `haby.json`; syntax errors may additionally
carry a one-based source location. Errors block import, while warnings allow a
revision to be accepted.

Warnings produced by a successful import are persisted unchanged with its
immutable ManifestRevision. Reading a revision never revalidates it implicitly.
Explicit revalidation creates a separate report under the current rules and does
not modify the original revision or its historical warnings.

Diagnostics are deterministically ordered, bounded and redacted. M2 type-specific
validators map failures to standard Haby codes rather than introducing arbitrary
plugin code namespaces. Version conflicts, authorization and concurrency failures
remain application errors rather than manifest diagnostics.

## Application manifest shape

The initial schema remains JSON and retains Liquid for explicit value templates.
JSON Schema validation occurs before semantic validation by plugins.

```json
{
  "$schema": "https://haby.dev/schemas/application-manifest.v1alpha1.json",
  "apiVersion": "haby.dev/v1alpha1",
  "kind": "Application",
  "metadata": {
    "id": "automation-processor-service",
    "displayName": "Automation",
    "labels": {
      "team": "platform"
    }
  },
  "spec": {
    "parameters": {
      "query-prefix": {
        "type": "string",
        "description": "Prefix used by application queries",
        "default": "{{ environment.name }}_"
      }
    },
    "resources": {
      "main-database": {
        "type": "postgresql.database/v1alpha1",
        "providerRef": "postgresql.primary",
        "properties": {},
        "lifecycle": {
          "deletionPolicy": "retain"
        }
      },
      "worker-database": {
        "type": "postgresql.database/v1alpha1",
        "providerRef": "postgresql.primary",
        "properties": {},
        "lifecycle": {
          "deletionPolicy": "delete"
        }
      }
    },
    "exports": {
      "reporting-database": {
        "resourceRef": "main-database",
        "contract": "postgresql.connection/v1alpha1",
        "accessProfile": "read-only"
      }
    },
    "components": {
      "api": {
        "displayName": "API",
        "bindings": {
          "database": {
            "target": {
              "kind": "local-resource",
              "resourceRef": "main-database"
            },
            "documentProjections": [
              {
                "documentRef": "settings",
                "path": "/PostgreSql",
                "rendererRef": "npgsql-connection/v1alpha1"
              }
            ]
          }
        },
        "documents": {
          "settings": {
            "fileName": "appsettings.json",
            "format": "json",
            "template": {
              "QueryPrefix": "{{ parameters['query-prefix'] }}"
            }
          }
        },
        "workloads": {
          "main": {
            "type": "kubernetes.workload/v1alpha1",
            "providerRef": "kubernetes.primary",
            "profile": "grpc-service",
            "properties": {
              "replicas": 2,
              "suspended": false
            }
          }
        }
      },
      "worker": {
        "displayName": "Worker",
        "bindings": {
          "shared-database": {
            "target": {
              "kind": "local-resource",
              "resourceRef": "main-database"
            },
            "documentProjections": [
              {
                "documentRef": "settings",
                "path": "/PostgreSql/Shared",
                "rendererRef": "npgsql-connection/v1alpha1"
              }
            ]
          },
          "private-database": {
            "target": {
              "kind": "local-resource",
              "resourceRef": "worker-database"
            },
            "documentProjections": [
              {
                "documentRef": "settings",
                "path": "/PostgreSql/Worker",
                "rendererRef": "npgsql-connection/v1alpha1"
              }
            ]
          }
        },
        "documents": {
          "settings": {
            "fileName": "appsettings-worker.json",
            "format": "json",
            "template": {}
          }
        },
        "workloads": {
          "main": {
            "type": "kubernetes.workload/v1alpha1",
            "providerRef": "kubernetes.primary",
            "profile": "worker",
            "properties": {
              "replicas": 1,
              "suspended": false
            }
          }
        }
      }
    }
  }
}
```

The alpha contract may still evolve through explicit schema revisions, but the
separation between resources, exports, bindings, documents and workloads is an
invariant. A Configuration document declaration describes materialization; its
delivery is configured separately, so the manifest does not use a `publish`
Boolean.

`providerRef` is an environment-local alias. The same manifest can therefore use
`postgresql.primary` in several environments while each environment resolves the
alias to a different provider instance and administrator secret.

### Serialization contract

The manifest DTOs are designed for `System.Text.Json` source generation and
Native AOT-friendly metadata generation. Core contract alternatives use an
explicit discriminator and closed derived-type set instead of accepting several
unrelated JSON shapes in one property.

For example, `BindingTarget` has two forms:

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(LocalResourceBindingTarget), "local-resource")]
[JsonDerivedType(typeof(ApplicationExportBindingTarget), "application-export")]
public abstract record BindingTarget;

public sealed record LocalResourceBindingTarget(
    string ResourceRef) : BindingTarget;

public sealed record ApplicationExportBindingTarget(
    string ApplicationRef,
    string ExportRef) : BindingTarget;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ApplicationManifest))]
internal partial class ApplicationManifestJsonContext : JsonSerializerContext;
```

Metadata-based source generation is selected because `System.Text.Json`
polymorphism is not supported by fast-path generation. Every derived type is
registered explicitly. Unknown discriminator values, missing required members
and unexpected properties are validation errors; the parser does not silently
fall back to a base type.

The same discriminated-contract approach applies to typed parameter definitions.
`DocumentProjection` itself is a simple closed DTO containing `documentRef`,
`path` and `rendererRef`; its target format comes from the referenced document,
so it needs no discriminator. Manifest DTOs use concrete closed collections such
as `Dictionary<string, T>` and `List<T>`, generic string-enum conversion, and no
reflection-based type discovery or handwritten shape-guessing converters.

Configuration document definitions are discriminated by `format`. The `json`
and `yaml` variants contain a structured template, while the `text` variant
contains a raw string template. This keeps their CLR contracts source-generated
and prevents a raw text document from accidentally accepting structured-only
features.

Arbitrary JSON is limited to intentional extension boundaries:

- `Resource.properties` and `Workload.properties`, validated against the versioned
  plugin schema selected by `type`;
- a structured Configuration document's `template`;
- typed parameter defaults whose JSON token kind is checked against the
  parameter definition.

These values use `JsonElement`, not `object`, and are never used to choose a CLR
type. The JSON Schema mirrors the same discriminators with `oneOf` branches so
schema validation and deserialization agree.

### Resource sharing

A resource is declared once and can be bound to multiple components. Components
that bind `main-database` receive the same managed resource. Declaring
`worker-database` creates a separate resource and lifecycle boundary. Both are
owned by the containing Application; a resource used by only one Component does
not acquire Component ownership. Removing a Component therefore removes its
bindings but does not implicitly delete a still-declared Resource.

No component needs to guess a generated database name or copy a rendered JSON
fragment from another component. Shared infrastructure does not imply shared
credentials: a provider may create a separate principal, grant and SecretRef for
each Binding while retaining one physical database.

Resource types may be atomic or provide a convenience aggregate. For example, a
PostgreSQL plugin can model database, principal and grant as separate dependent
resources when two components share one database but require different
credentials. A standard database-with-owner profile can retain a short manifest
for the common case without making that shortcut a core invariant.

### Resource lifecycle and Component removal

Resource lifecycle is declaration-driven. A Resource remains desired while its
stable local ID remains in `spec.resources`, regardless of how many Components
currently bind it. Removing a Component therefore never implicitly removes,
orphans or deprovisions a Resource.

Resource lifecycle does not support `ownerRef`, `lifecycle.componentRef`,
`deleteWith` or another shortcut that makes a Resource disappear when a
Component disappears. Such a shortcut would conflict with the still-present
Resource declaration and could unexpectedly affect shared or externally
exported Resources.

When a Component is removed:

- its nested Bindings are removed and consumer-specific access can be revoked;
- its nested Workloads are included explicitly in the removal plan and are
  stopped and deprovisioned before the Component record is removed;
- Resources that remain in `spec.resources` continue to be reconciled;
- when the change removes the last known Binding or export from a still-declared
  Resource, the plan produces an inspectable unused-resource warning rather than
  an implicit deletion.

To remove a Component-specific Resource, the author removes both the Component
and the Resource declaration explicitly. The plan orders the resulting work:

1. stop and remove affected Workloads;
2. remove Bindings and revoke consumer-specific access and secrets;
3. verify that no Resource exports or other consumers remain;
4. apply the Resource's `lifecycle.deletionPolicy`.

The deletion policy answers only what happens after explicit Resource removal:
`delete` deprovisions the external object, `orphan` leaves it externally and
forgets Haby management after confirmation, and `retain` preserves the object
and its managed record for an explicit later decision. It does not decide when
the Resource leaves desired state.

### Cross-application resource bindings

A foreign Application cannot bind directly to another Application's local
`resourceRef`. The owner must publish a stable Resource export. The export names
the local Resource, a versioned output contract and an optional provider-defined
access profile. It is the public compatibility boundary; the owner may replace
or rename an internal Resource while retaining the same export contract through
an explicit migration.

A consuming Application uses a typed target:

```json
{
  "target": {
    "kind": "application-export",
    "applicationRef": "shared-storage-service",
    "exportRef": "reporting-database"
  },
  "documentProjections": [
    {
      "documentRef": "settings",
      "path": "/ReportingDatabase",
      "rendererRef": "npgsql-connection/v1alpha1"
    }
  ]
}
```

Cross-application consumption is read-only at the Haby control-plane level. The
consumer may use the permitted export outputs but cannot change the Resource's
desired state, provider, lifecycle, reconciliation or commands. This guarantee
is implicit in `application-export`; it is not represented by an ambiguous
`readOnly` Boolean.

The export's provider-defined access profile describes permissions in the
external system, such as a PostgreSQL read-only principal. Those permissions are
separate from Haby control-plane rights. A consumer cannot request a stronger
profile than the owner exported.

Knowing an Application and export ID is not authorization. The environment's
authorization policy must permit the consumer to use that export. Consumer
allow-lists are installation policy rather than repository manifest content, so
the same application declaration remains portable between installations.

The owner Application remains the only lifecycle owner. Removing a consumer or
its Binding revokes and removes only consumer-specific access state and secrets.
Removing or incompatibly changing an export with active consumers is a planned,
impact-visible operation. Removing the owning Resource or Application is blocked
until those dependencies are resolved or an explicitly authorized destructive
plan is approved.

### Configuration exposure

A resource contributes data to an application document only through an explicit
binding configuration. A Kubernetes workload with no such binding remains
control-plane state and is never included in `appsettings.json`.

The renderer profile belongs to a plugin or downstream distribution and converts
typed resource outputs into a consumer-specific, format-neutral structured
fragment. The final document is assembled at an explicit JSON Pointer path and
validated. Two fragments targeting the same path are a validation error unless
the document explicitly selects a versioned conflict policy. Haby must not
discover visibility by merging every configured service into every output
document.

### Structured JSON and YAML documents

`json` and `yaml` Configuration documents share one structured configuration
tree consisting of mappings with string keys, sequences, typed scalar values,
null and internal typed SecretRef placeholders. A renderer returns this tree
rather than serialized JSON or YAML text.
Haby applies templates, profiles, overrides and Document projections to the tree
and invokes the document-format serializer only after composition is complete.

The `template` is represented with JSON syntax inside `haby.json` even when the
output document is YAML. For example:

```json
{
  "fileName": "appsettings.yaml",
  "format": "yaml",
  "template": {
    "QueryPrefix": "{{ parameters['query-prefix'] }}"
  }
}
```

The same Document projection used for a JSON document can target this YAML
document:

```json
{
  "documentRef": "settings",
  "path": "/PostgreSql",
  "rendererRef": "npgsql-connection/v1alpha1"
}
```

`path` uses JSON Pointer semantics over the shared tree; it does not address
serialized text. The output serializer can therefore produce:

```yaml
QueryPrefix: production_
PostgreSql:
  DefaultConnection:
    ConnectionString: "..."
```

Structured YAML is intentionally limited to the shared data model. It uses one
YAML document, requires string mapping keys, rejects duplicate keys and custom
tags, and does not preserve comments, anchors, aliases or source formatting.
Serialization must preserve scalar types and quote ambiguous strings
deterministically. SecretRef placeholders are Haby's internal revision data, not
YAML tags; an authorized delivery operation resolves them during final
materialization.

A `text` Configuration document is a raw Liquid template rather than a
structured tree. It is not eligible for `documentProjections`, JSON Pointer
insertion, structured profile composition or JSON Merge Patch. Additional
structured formats require an explicit serializer and compatibility decision;
they are not inferred from a file extension.

### Parameters and overrides

The manifest defines type, validation rules, description and default value.
Operator values are separate override records with scope and provenance.

Effective values are resolved from explicit layers:

1. schema and plugin defaults;
2. manifest values;
3. environment overrides;
4. application or component operator overrides.

A new manifest default affects only values without an operator override. The UI
must show the effective value, its source and a reset-to-default action. Updating
a manifest never silently converts a default into an operator value or overwrites
an existing override.

Generated values and resource outputs are not another merge layer. They are
persisted state referenced by resource bindings.

## Shared configuration profiles

Cross-cutting settings such as OpenTelemetry exporters, logging defaults and
common HTTP policies belong in reusable Configuration profiles. A profile is a
versioned configuration fragment; it is not a parent document that applications
must fetch and merge correctly at runtime.

Profile assignments target applications explicitly through one or more of:

- the current environment;
- an Application set with static membership;
- an Application set backed by a label selector;
- explicit application or component IDs;
- component labels or types.

An Application set is independent from Folder containment. It can represent a
module, team, capability or any other operational group, and one application may
belong to several sets. The same profile revision can be assigned to several
sets, so changing a shared logging profile does not require editing duplicate
configuration in every group.

Conceptually, one profile and assignment can look like this:

```json
{
  "apiVersion": "haby.dev/v1alpha1",
  "kind": "ConfigurationProfile",
  "metadata": {
    "id": "observability-defaults"
  },
  "spec": {
    "format": "json",
    "patch": {
      "OpenTelemetry": {
        "Exporter": "otlp"
      },
      "Serilog": {
        "MinimumLevel": {
          "Default": "Information"
        }
      }
    }
  }
}
```

```json
{
  "apiVersion": "haby.dev/v1alpha1",
  "kind": "ProfileAssignment",
  "metadata": {
    "id": "observability-for-processing"
  },
  "spec": {
    "profileRef": {
      "profileId": "observability-defaults",
      "revision": 3
    },
    "priority": 100,
    "targets": {
      "applicationSets": [
        "processing",
        "analytics"
      ]
    }
  }
}
```

`profileRef` is a structured reference to one exact immutable profile revision.
It maps directly to a source-generated `ProfileRevisionRef` contract with a
string `profileId` and a positive integer `revision`; it is not parsed from a
delimiter-based string such as `observability-defaults@3`.

Assignments do not persist `latest`, ranges or other time-dependent selectors.
An API or UI may help an operator choose the latest revision, but it resolves
that choice to an exact revision before validation and persistence. This keeps
planning, rollback and effective-configuration fingerprints reproducible.

An application document can then override only the required nested value, such
as `Serilog.MinimumLevel.Default`, while retaining the inherited exporter and
other logging settings.

For JSON documents, effective configuration is assembled in a deterministic
pipeline:

1. environment baseline profiles;
2. matching profile assignments in explicit priority order;
3. the application and component document template;
4. resource-binding fragments inserted at their declared JSON Pointer paths;
5. authorized operational overrides.

The default profile composition rule is JSON Merge Patch semantics: objects are
merged recursively and arrays are replaced, never implicitly unioned. Advanced
cases may select JSON Patch. Assignments with the same priority that write
different values to the same path are rejected instead of depending on database
or enumeration order.

Resource bindings are not ordinary merge layers. A collision at a resource-owned
path is an error unless the binding selects an explicit, versioned conflict
policy. Operational overrides of resource-owned or secret paths require a
separate break-glass permission.

Haby materializes and versions the final effective Configuration document. A
publisher may additionally expose separate layers when a delivery backend
supports them, but application correctness must not depend on a particular
client library reproducing Haby's merge algorithm.

Every effective JSON path retains provenance: profile and revision, manifest
revision, resource binding or operational override. The UI can therefore explain
why a value is present and show which applications and components will change
before applying a new profile revision.

Temporary diagnostics use an operational override with a target selector,
reason, author and expiration time. One override can enable detailed logging for
several Application sets and automatically expire, without modifying the shared
baseline profile or every application manifest.

### Generated values and secrets

Generation is performed before rendering through a get-or-create value store.
Liquid rendering is pure and cannot create or mutate persisted values as a side
effect.

Generated non-secret values, external IDs and observed metadata belong to the
managed resource state. Under
[ADR-006](../adr/0006-secret-storage-and-encryption.md), passwords and tokens are
stored as immutable Secret versions and represented outside the secret service
only by SecretRefs. They are redacted from plans, logs and ordinary snapshots.

Stored Configuration-document revisions retain SecretRefs and the exact secret
version identities used by their effective revision. Plaintext is resolved only
while an authorized provisioner or delivery operation materializes the value;
it is never persisted in a rendered revision.

## Folder and product organization

Folder is an accepted organizational concept but is not part of the M2 or M2.1
implementation. It is scheduled after the core administration UI as M5.1 because
its primary purpose is navigation and operator organization. No M2 contract,
persistence model or use case should require a Folder to exist.

Folders provide an arbitrary tree for navigation, for example:

```text
platform/
  ingestion/
  automation/
shared/
```

Folders deliberately have no built-in meaning such as product, module,
environment or ownership. Labels, Application sets and search provide orthogonal
classification. This allows installations to represent a product/module
hierarchy without embedding that hierarchy in the Haby containment model.

A first-class Product entity is deferred. Product membership is frequently
many-to-many because shared applications can participate in several products,
whereas a folder provides single-parent containment. An Application set supports
group targeting without claiming release semantics. If release coordination
later requires an exact, versioned set of application revisions, Haby can add a
`Distribution` aggregate without changing application identity.

## Workloads and Kubernetes

`Workload` is a separate portable domain declaration, not a subtype of Resource
and not a configuration-document key. It describes how one Component should run.
A Component can declare zero, one or several Workloads. Applying a declaration
to an Environment produces a `ManagedWorkload` with its own persisted identity,
desired scale, suspension, provider association, observed state and operation
history.

For authoring convenience, Workload declarations are nested in their Component.
Containment makes the single-parent relationship structural, removes a
redundant `componentRef` and prevents dangling references. It does not collapse
Workload into the Component in the manifest or domain model, and it does not
collapse ManagedWorkload into the Component in persistence, API or operations.
The Workload local ID is scoped to that Component, so several Components may each
declare a Workload named `main`. Moving a declaration to another Component is a
replacement by default because its logical manifest identity changes.

A Workload has no meaning without its Component and cannot be shared or exported.
Removing a Component therefore plans removal of all its Workloads. This cascade
is safe for Workloads and deliberately does not apply to Application-owned
Resources, which remain desired while declared in `spec.resources`.

Kubernetes is one Workload provider, not a special configuration-document merge
mode. Future Workload providers do not require changing the Application,
Component, Resource or Binding contracts.

The Kubernetes plugin should offer versioned profiles such as `grpc-service`,
`http-service`, `worker` and `scheduled-job`. A profile expands a concise desired
Workload into separately planned Deployment, Service, Ingress or Job artifacts.
The plan and observed Workload state expose those concrete provider objects
without reclassifying the Workload as a Resource or exposing them implicitly to
application configuration.

Profiles can be configured per provider instance and extended by downstream
distributions. Advanced users may use validated patches or a raw-manifest escape
hatch, but the common path must not require embedding three full Kubernetes
objects in each application manifest.

Replica intent and suspension are separate:

```json
{
  "replicas": 3,
  "suspended": true
}
```

Suspension applies zero replicas while preserving the desired replica count.
Resuming restores three replicas. Operational overrides are persisted separately
from manifest defaults, so a later manifest import does not unexpectedly erase a
temporary scale decision.

## Plugin architecture

[ADR-003](../adr/0003-plugin-composition-and-runtime-strategy.md) remains in
force: trusted plugins are composed at build time and M2 does not implement
runtime provider processes.

The plugin SDK is divided by capability instead of one strategy receiving an
unstructured merged JSON object:

### Plugin module

Registers metadata, resource and workload types, schemas, renderer profiles,
commands and the implementations selected at build time. Registration is
explicit and compatible with trimming.

### Resource provisioner

Owns one or more resource types and supports:

- definition validation;
- plan;
- apply and reconcile;
- observe/read;
- delete according to lifecycle policy.

The provisioner receives stable application, component, environment and resource
identities, provider-instance configuration, desired properties and current
resource state. It returns structured outputs, secret references, external IDs,
diagnostics and observed state.

### Workload reconciler

Owns one or more versioned Workload types and supports validation, planning,
apply, observe, reconcile and deletion. It receives stable Application,
Component, Workload and Environment identities and returns provider-object
status without treating deployment artifacts as configuration outputs.

Resource provisioners and Workload reconcilers share operation, cancellation,
idempotency, retry, diagnostics and secret-redaction rules, but their domain
contracts remain distinct.

### Configuration renderer

Purely converts typed resource outputs and Binding options into a format-neutral
structured configuration fragment. JSON and YAML use the same renderer output;
the referenced Configuration document selects the final serializer. A renderer
has no permission to create resources, serialize the final document or generate
new persisted values during rendering.

### Configuration publisher

Publishes selected document revisions to a delivery channel. Consul, files,
Kubernetes ConfigMaps and Secrets, and generic HTTP endpoints are publishers,
not implicit responsibilities of every provisioner.

Configuration publication is separate from Runtime Identity authentication. The
[Runtime Identity proposal](runtime-identity.md) defines the transport-neutral
authentication boundary selected for M2 and Kubernetes TokenReview as its first
implementation. An authenticator returns a verified external runtime subject; it
never selects the Application, Component, Workload or Configuration document
that subject may access.

### Runtime Identity authenticator

Validates transient platform identity evidence and returns a normalized,
transport-neutral authenticated runtime subject. Haby core owns authorization
and maps that subject through a Runtime Identity binding to a persisted
ManagedWorkload and its allowed operations.

The contract has no dependency on ASP.NET Core, gRPC, EF Core or Kubernetes
client models. Kubernetes TokenReview is the first adapter. Publisher
implementations and a complete pull-delivery endpoint are not required for the
initial M2 contract.

### Resource command handler

Exposes explicitly described commands for a managed resource. A command has a
stable ID, supported resource type, input and output schemas, required permission,
risk classification, timeout and audit policy.

For example, a PostgreSQL plugin may expose an `execute-sql` command scoped to one
managed database. Haby supplies that resource's credential reference rather than
an unrestricted provider-administrator credential. Commands that mutate managed
state must trigger observation or reconciliation and must not silently replace
the desired declaration.

### Provider health check

Validates provider-instance configuration and reports connectivity and
capability status without mutating application resources.

Workload providers use a dedicated, transport-neutral workload reconciler
contract and the same durable plan/operation infrastructure as Resource
provisioners. Kubernetes support does not require a Kubernetes-specific branch
in the Haby application layer.

## Persisted state boundaries

The database keeps the following records separate:

- application identity and operator-owned folder placement;
- immutable manifest revisions, Application versions and import audit metadata;
- Application deployments with desired/applied generations and concurrency;
- Deployment change sets with exact membership, plans and input fingerprints;
- parameter and operational overrides with provenance;
- application sets, configuration-profile revisions and profile assignments;
- provider instances, Secret metadata, encrypted Secret versions and SecretRefs;
- desired resources, Resource exports and component bindings;
- ManagedWorkloads with their Workload declaration reference, Component
  relationship, desired and observed state;
- generated values, external IDs and observed resource state;
- rendered configuration-document revisions;
- publication records;
- operations, plans, diagnostics and audit events.

These records may use JSON for plugin-owned, schema-versioned payloads, but they
must not be collapsed into one semi-final JSON document.

## Reconciliation and deletion

Importing a manifest only validates and stores an immutable revision. Planning a
DeploymentChangeSet computes differences against the applied generations and
current observed state. Apply is idempotent and persists progress per Resource,
Workload and Application deployment.

Removing a component, Workload, Binding, Resource export or Resource is not
equivalent to deleting a row. Active cross-application consumers are included in
the plan and can block an incompatible export or Resource removal.

Removing a Component does not imply Resource removal. Any Resource that remains
declared in `spec.resources` remains desired and continues to reconcile. A
Component-specific Resource is removed only when its own declaration is removed;
the plan then revokes consumers before applying its deletion policy.

The Resource lifecycle policy determines whether Haby should:

- retain the external resource under Haby management;
- orphan it and forget management after confirmation;
- or deprovision it through the owning plugin.

Deleting an application follows the same planned workflow. Cascading database
deletion must never bypass plugin deprovisioning and lifecycle policy.

## Requirement coverage

| Problem | Model response |
| --- | --- |
| Application cannot be renamed | Stable application identity is independent from repository, display name and folder path |
| Shared resources require copying configuration | Multiple component bindings reference one resource |
| Different components cannot cleanly request different databases | Each database is a separately identified Application-owned Resource with its own lifecycle |
| Removing a Component can accidentally delete its database | Resource existence is declaration-driven; Component removal never implicitly removes a Resource |
| A component needs a Resource owned by another Application | The owner publishes a versioned Resource export and the consumer binds through a typed `application-export` target |
| Defaults overwrite administrator changes | Definitions and defaults are separate from persisted overrides with provenance |
| Common configuration is copied per module | One versioned profile can target global, overlapping or multi-module Application sets |
| Temporary detailed logging is difficult to roll out safely | A targeted operational override has impact preview, audit metadata and expiration |
| Generated credentials are lost during JSON merging | Generated values and secret references are resource state, not editable JSON |
| Kubernetes requires one large mixed template | Workload profiles and individually planned resources replace the composite merge |
| Runtime deployment is confused with infrastructure dependencies | Workload is a portable declaration structurally contained by one Component; ManagedWorkload owns Environment-specific desired and observed state |
| Scale-to-zero forgets the previous replica count | Desired replicas and suspension are separate fields |
| Some provider output must not reach the application | Only explicit bindings contribute to configuration documents |
| Registry behavior is hard to debug | Immutable revisions, plans, operation history and separated state make each transition inspectable |
| A release updates several dependent applications sequentially | One Deployment change set freezes exact membership, plans cross-Application dependencies and starts side effects only after complete validation |

## Deferred decisions

- promotion of the alpha contract to a stable v1 schema after implementation
  feedback and compatibility tests;
- additional structured document formats beyond JSON and YAML;
- a first-class Product or Distribution model;
- multi-environment management in one Haby installation;
- remote provider protocols;
- publisher-specific secret materialization policies;
- the advanced Kubernetes raw-manifest escape hatch.
