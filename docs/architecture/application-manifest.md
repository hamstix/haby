# Application manifest and domain model proposal

- Status: Proposed
- Target milestone: M2

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
| Folder | An optional organizational container for folders and applications | Organization unit |
| Application | An independently versioned software and ownership boundary, commonly represented by one source repository | Configuration unit, modular microservice |
| Component | A separately runnable part of an application, such as an API, worker, scheduler or compiler | Configuration key when it was used as a nanoservice |
| Workload | An environment-specific deployment of a component, such as a Kubernetes Deployment, StatefulSet or Job | Kubernetes data embedded in a configuration key |
| Configuration document | A named rendered document consumed by a component, such as `appsettings.json` | Configuration key when it was used as an output document |
| Provider instance | A configured external system that Haby can manage, such as one PostgreSQL cluster or RabbitMQ broker | Service |
| Resource | A desired and observed object managed through a provider instance, such as a database, principal, queue or Kubernetes workload | Configuration-unit-at-service association |
| Binding | An explicit relationship through which a component consumes a resource and may expose selected resource outputs in a configuration document | `fromKey` and implicit JSON copying |
| Parameter definition | A typed input declared by an application manifest, including an optional default | Template parameter |
| Parameter override | An operator-supplied value stored independently from the manifest default | A template parameter value overwritten during every update |
| Application set | A named static membership or label selector used to target several applications without changing folder containment | Module-like operational grouping |
| Configuration profile | A reusable, versioned configuration fragment such as common observability or logging defaults | Common configuration copied between hierarchy levels |
| Profile assignment | An explicit relationship that applies a profile revision to an environment, application set or target selector | Implicit folder-based inheritance |
| Manifest revision | An immutable imported revision of an application declaration with source and version metadata | The mutable template and previous-version pair |

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
Folder
  `- Application
       |- Manifest revision
       |- Component
       |    |- Workload
       |    |- Resource binding
       |    `- Configuration document
       `- Resource
            `- Provider instance

Environment
  |- Provider instance aliases
  |- Parameter overrides
  |- Operational overrides
  `- Applied application revision

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

- Applications, components, resources, documents and provider instances receive
  immutable persisted IDs.
- `metadata.id` in the manifest is a stable logical application ID and is not
  derived from a Git repository name.
- Object keys such as `api`, `worker` and `mainDatabase` are stable manifest-local
  IDs. Human-facing names are separate and mutable.
- Repository URL, commit, branch and package version are source metadata, not
  identity.
- Generated external names and external object IDs are persisted as resource
  state. Renaming or moving an application does not regenerate them.
- Changing a stable local ID means replacement unless an explicit migration or
  `movedFrom` operation associates it with existing state.
- Destructive external renames are always planned operations and are never an
  implicit consequence of editing display metadata.

These rules make ordinary rename and folder moves safe while keeping resource
replacement explicit.

## Manifest name and envelope

The recommended repository file name is **`haby.json`**. The file is an
application manifest, not a rendering template. The short product-specific name
is easy to discover and avoids collisions with generic files named
`template.json` or `manifest.json`.

The manifest contains a schema version and a stable application ID. Version and
source revision are supplied by CI as import metadata so that the manifest does
not duplicate a version maintained elsewhere.

A CI import conceptually contains:

```json
{
  "applicationId": "com.example.automation",
  "version": "2.4.0",
  "source": {
    "repository": "https://example.invalid/platform/automation.git",
    "revision": "0123456789abcdef"
  },
  "manifest": {}
}
```

The operation is an idempotent import of a manifest revision. Applying the
revision is a separate policy or operation so installations can choose automatic
apply, approval, scheduling or plan-only behavior.

## Proposed manifest shape

The initial schema remains JSON and retains Liquid for explicit value templates.
JSON Schema validation occurs before semantic validation by plugins.

```json
{
  "$schema": "https://haby.dev/schemas/application-manifest.v1alpha1.json",
  "apiVersion": "haby.dev/v1alpha1",
  "kind": "Application",
  "metadata": {
    "id": "com.example.automation",
    "displayName": "Automation",
    "labels": {
      "team": "platform"
    }
  },
  "spec": {
    "parameters": {
      "queryPrefix": {
        "type": "string",
        "description": "Prefix used by application queries",
        "default": "{{ environment.name }}_"
      }
    },
    "resources": {
      "mainDatabase": {
        "type": "postgresql.database",
        "providerRef": "postgresql.primary",
        "ownerRef": "application",
        "desired": {},
        "lifecycle": {
          "deletionPolicy": "retain"
        }
      },
      "workerDatabase": {
        "type": "postgresql.database",
        "providerRef": "postgresql.primary",
        "ownerRef": "component:worker",
        "desired": {},
        "lifecycle": {
          "deletionPolicy": "delete"
        }
      }
    },
    "components": {
      "api": {
        "displayName": "API",
        "bindings": {
          "database": {
            "resourceRef": "mainDatabase",
            "configuration": {
              "documentRef": "settings",
              "path": "/PostgreSql",
              "profile": "npgsql"
            }
          }
        },
        "documents": {
          "settings": {
            "fileName": "appsettings.json",
            "format": "json",
            "template": {
              "QueryPrefix": "{{ parameters.queryPrefix }}"
            },
            "publish": true
          }
        },
        "workloads": {
          "main": {
            "type": "kubernetes.workload",
            "providerRef": "kubernetes.primary",
            "profile": "grpc-service",
            "desired": {
              "replicas": 2,
              "suspended": false
            }
          }
        }
      },
      "worker": {
        "displayName": "Worker",
        "bindings": {
          "sharedDatabase": {
            "resourceRef": "mainDatabase",
            "configuration": {
              "documentRef": "settings",
              "path": "/PostgreSql/Shared",
              "profile": "npgsql"
            }
          },
          "privateDatabase": {
            "resourceRef": "workerDatabase",
            "configuration": {
              "documentRef": "settings",
              "path": "/PostgreSql/Worker",
              "profile": "npgsql"
            }
          }
        },
        "documents": {
          "settings": {
            "fileName": "appsettings-worker.json",
            "format": "json",
            "template": {},
            "publish": true
          }
        },
        "workloads": {
          "main": {
            "type": "kubernetes.workload",
            "providerRef": "kubernetes.primary",
            "profile": "worker",
            "desired": {
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

The exact property names remain subject to implementation feedback, but the
separation between resources, bindings, documents and workloads is an invariant.

`providerRef` is an environment-local alias. The same manifest can therefore use
`postgresql.primary` in several environments while each environment resolves the
alias to a different provider instance and administrator secret.

### Resource sharing

A resource is declared once and can be bound to multiple components. Components
that bind `mainDatabase` receive the same managed database and credentials.
Declaring `workerDatabase` creates a separate ownership and lifecycle boundary.
No component needs to guess a generated database name or copy a rendered JSON
fragment from another component.

Resource types may be atomic or provide a convenience aggregate. For example, a
PostgreSQL plugin can model database, principal and grant as separate dependent
resources when two components share one database but require different
credentials. A standard database-with-owner profile can retain a short manifest
for the common case without making that shortcut a core invariant.

### Configuration exposure

A resource contributes data to an application document only through an explicit
binding configuration. A Kubernetes workload with no such binding remains
control-plane state and is never included in `appsettings.json`.

The renderer profile belongs to a plugin or downstream distribution and converts
typed resource outputs into a consumer-specific fragment. The final document is
assembled at an explicit JSON Pointer path and validated. Two fragments targeting
the same path are a validation error unless the document explicitly selects a
versioned conflict policy. Haby must not discover visibility by merging every
configured service into every output document.

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
    "profileRef": "observability-defaults@3",
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
managed resource state. Passwords and tokens are stored as secret values or
secret references and are redacted from plans, logs and ordinary snapshots.
Rendered configuration revisions may contain resolved secrets only when a
publisher explicitly requires them and its storage policy permits it.

## Folder and product organization

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

Kubernetes is a provider of workload and related resource types, not a special
configuration-document merge mode.

`workloads` is concise manifest syntax owned by a component. Applied workloads
are persisted and reconciled through the same Resource and operation model as
databases, principals and queues.

The Kubernetes plugin should offer versioned profiles such as `grpc-service`,
`http-service`, `worker` and `scheduled-job`. A profile expands a concise desired
workload into separately planned Deployment, Service, Ingress or Job resources.
The plan and observed state still expose those concrete resources.

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

Registers metadata, resource types, schemas, renderer profiles, commands and the
implementations selected at build time. Registration is explicit and compatible
with trimming.

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

### Configuration renderer

Purely converts typed resource outputs and binding options into a configuration
fragment. It has no permission to create resources or generate new persisted
values during rendering.

### Configuration publisher

Publishes selected document revisions to a delivery channel. Consul, files,
Kubernetes ConfigMaps and Secrets, and generic HTTP endpoints are publishers,
not implicit responsibilities of every provisioner.

Configuration publication is separate from workload authentication. The
[workload-identity proposal](workload-identity.md) defines the transport-neutral
authentication boundary selected for M2 and Kubernetes TokenReview as its first
implementation. An authenticator returns a verified external subject; it never
selects the Application, Component or Configuration document that subject may
access.

### Workload identity authenticator

Validates transient platform identity evidence and returns a normalized,
transport-neutral authenticated workload subject. Haby core owns authorization
and maps that subject to a persisted Workload and its allowed operations.

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

Kubernetes workload support uses the same resource provisioner contract. It does
not require a Kubernetes-specific branch in the Haby application layer.

## Persisted state boundaries

The database keeps the following records separate:

- application identity and mutable display metadata;
- immutable manifest revisions and source provenance;
- parameter and operational overrides with provenance;
- application sets, configuration-profile revisions and profile assignments;
- provider instances and secret references;
- desired resources and component bindings;
- generated values, external IDs and observed resource state;
- rendered configuration-document revisions;
- publication records;
- operations, plans, diagnostics and audit events.

These records may use JSON for plugin-owned, schema-versioned payloads, but they
must not be collapsed into one semi-final JSON document.

## Reconciliation and deletion

Importing a manifest computes a difference against the last applied revision.
Changes are represented as a plan before side effects occur. Apply is idempotent
and persists progress per resource.

Removing a component, binding or resource is not equivalent to deleting a row.
The resource lifecycle policy determines whether Haby should:

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
| Different components cannot cleanly request different databases | Each database is a separately identified resource with its own owner and lifecycle |
| Defaults overwrite administrator changes | Definitions and defaults are separate from persisted overrides with provenance |
| Common configuration is copied per module | One versioned profile can target global, overlapping or multi-module Application sets |
| Temporary detailed logging is difficult to roll out safely | A targeted operational override has impact preview, audit metadata and expiration |
| Generated credentials are lost during JSON merging | Generated values and secret references are resource state, not editable JSON |
| Kubernetes requires one large mixed template | Workload profiles and individually planned resources replace the composite merge |
| Scale-to-zero forgets the previous replica count | Desired replicas and suspension are separate fields |
| Some provider output must not reach the application | Only explicit bindings contribute to configuration documents |
| Registry behavior is hard to debug | Immutable revisions, plans, operation history and separated state make each transition inspectable |

## Deferred decisions

- exact JSON property names and the final v1 schema;
- whether YAML is accepted as an additional serialization after the JSON schema
  and semantics stabilize;
- a first-class Product or Distribution model;
- multi-environment management in one Haby installation;
- remote provider protocols;
- publisher-specific secret materialization policies;
- the advanced Kubernetes raw-manifest escape hatch.
