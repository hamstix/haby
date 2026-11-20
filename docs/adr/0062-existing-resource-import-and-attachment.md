# ADR-062: Existing external Resource import and use-and-retain attachment

- Status: Accepted
- Date: 2026-11-18
- Delivery: Not implemented by this decision; release after TLS #261 and before public CLI composition/NuGet and lower-priority feature work
- Implementation: [issue #267](https://github.com/hamstix/haby/issues/267)
- Related: ADR-033, ADR-035, ADR-006, ADR-061 and [ADR-063](0063-resource-capabilities-rendering-and-provider-status.md)

## Context

Applications adopting Haby may already have databases, logins and data. Importing
an Application manifest or a Provider declaration does not attach those external
Resources. Existing CREATE evidence must never be fabricated to admit them.
This decision adds an explicit standard capability while preserving fresh
provisioning contracts and the shared Provider-neutral catalog.

## Decision

### Intent, capability and exact inputs

A separate operator import intent binds exact Environment, Application and
Resource identities to an accepted manifest. The manifest remains reusable for
creation or import. Use the existing ChangeSet -> Plan -> Commit -> Execute
pipeline and freeze the exact Provider revision and all accepted inputs.
Resolve operator names once to exact identities through the API-only CLI.

Support is explicit for each ResourceType and import mode, with a registered
handler and closed versioned input schema. Unsupported combinations return typed
ImportNotSupported; there is no CREATE fallback. The first bounded scenario is
one existing database/login pair; mixed import/create in that slice is excluded.

Adapters own typed external identities, input validation, observed-state
verification, evidence and ordinary output construction. Shared core code must
not interpret PostgreSQL semantics or introduce Provider-specific commands,
tables, columns or enums. Typed adapter payloads are not arbitrary property bags.
Use generic shared persistence and opaque reservation identities under ADR-035,
source-generated serialization, asynchronous cancellation-aware I/O and isolated
EF Core arrange/act/assert contexts.

### Provenance and lifecycle authority

Record Imported provenance, the actual import operation/evidence and exact
revision. Do not invent CREATE receipts, successful CREATE attempts or generated
credentials. Attachment, use and lifecycle authority are separate concepts.

The initial mode is use-and-retain. It authorizes use through confirmed outputs,
not ownership changes, grants, password rotation or external deletion. Enforce
these restrictions both during planning and execution, including subsequent
update, removal and deletion paths after persistence/restart. Retained association
does not imply shared access, transfer or general adoption authority.

### PostgreSQL existing database-owner compatibility

The first adapter path accepts an existing database owned by the selected
application login, with safe role attributes and the effective database/schema
permissions required by the declared contract. It does not require the public
schema owner to equal that login merely for import.

A supported legacy arrangement can have database owner app_login and public
schema owner postgres, with existing PUBLIC USAGE and CREATE, application-owned
tables/sequences, and a login without superuser, CREATEDB, CREATEROLE, replication
or BYPASSRLS. Another separately observed schema owner can qualify under the same
effective-policy criteria. Preserve and truthfully record actual owners and ACLs,
including PUBLIC grants. Do not claim tightened/private ACLs or application schema
ownership. Import must not ALTER SCHEMA OWNER, GRANT, rotate credentials or create
objects to make an existing installation fit.

Use a separate adapter verification path for imported database-owner/v1 state;
do not relax its fresh provisioning invariants or introduce a parallel downstream
output model. Non-database-owner login support remains a separate future decision.
Database ownership alone does not prove authority to ALTER arbitrary existing
objects. Verify relevant declared requirements and refuse unsupported object
ownership state without a blanket audit of every external object or DDL probes.
Application migration/DML after import is a separate consumer action, never a
hidden import effect.

### Protected credential and ordinary consumers

Accept the existing password through an explicit protected input and associate
it with a Resource-owned Secret of purpose ApplicationLogin and Imported
provenance. It is neither an Environment Configuration secret nor generated
material. Keep the material out of manifests, Plans, audit records, logs and
diagnostics; persist exact SecretVersion references. Pending protected input may
be stored privately for durable retry. Without confirmed association, publish no
usable outputs.

Generalize any GeneratedCredential-only consumer assumption to a confirmed
application credential, keeping provenance separate. Both creation and import
use the normal PostgreSQL outputs, renderers and configuration delivery. Adopt
one current alpha model under ADR-033, not compatibility-only adapters. Any
required fresh catalog transition must be explicit; never auto-reset, backfill
or erase existing external ownership.

### Acceptance, retry and external identity

Verify the coupled database/login/credential and required state before atomically
accepting the catalog association and confirmed outputs. Internally staged
credential records are allowed; failure cleanup affects only owned staged
records, never external Resources. Database verification/network I/O and the
catalog transaction are not one cross-system atomic operation.

Replay of the same accepted request reuses the association and SecretVersion.
Changed identity or credential conflicts without blind create/recovery.
Credential comparison must not expose material or a hash oracle. Recover lost
responses from durable state; revalidate where required before acceptance.
A confirmed receipt records historical verification, not a perpetual external
state guarantee.

Enforce uniqueness within a catalog and logical ProviderInstance using exact
typed external identity and opaque generic reservation identity. Do not claim
global exclusivity across independent catalogs or duplicate ProviderInstances.
Names alone are insufficient: require adapter observation, preserve observable
identity and disclose its limits; refuse inadequate identity evidence.
Relevant later operations revalidate state and report typed conflicts after
external drift rather than silently normalizing it. Import remains subject to
these exclusivity/conflict rules; shared Reference semantics are a distinct
explicit capability under ADR-063, not a bypass of Import reservations.

## Accepted extension: common acquisition, rendering and status boundaries

[ADR-063](0063-resource-capabilities-rendering-and-provider-status.md) accepts one
shared Resource capability foundation: explicit Provision, Import and Reference
modes with exact per-type/mode support and closed typed inputs. Reference may
confirm use of an existing service without external creation or lifecycle
authority. Reuse #267's ChangeSet/Plan/Commit/Execute, durable receipt, exact
Provider/SecretVersion inputs and atomic internal confirmation/output boundary;
never fabricate CREATE or add a second import framework. No unsupported mode
falls back to CREATE. Multiple Resources/Applications may share a Reference only
under explicit adapter semantics, without weakening this ADR's Import conflicts.

This extension preserves the complete initial PostgreSQL import decision above:
Imported provenance, Resource-owned ApplicationLogin credentials, effective
owner/schema/ACL compatibility, observational verification and no external
repair, ownership/grant/password mutation or deletion. Reference grants no
mutation/rotation/delete/ownership authority; removal affects only internal
association and delivery authorization. Any stronger lifecycle transition needs
explicit accepted authority, not an implicit declaration change.

Equivalent capabilities reuse typed ordinary output/renderer contracts across
supported acquisition modes, with provenance/authority independent of consumer
shape. Safe output values and exact SecretRefs never contain plaintext. An
Environment-scoped shared application Secret reference requires explicit delivery
purpose and authorized consumers; system/administrator access is not implicit
application authority. Physical secret duplication is not required, and no
existing immutable-purpose boundary is silently widened. PostgreSQL import's
Resource-owned ApplicationLogin rule remains unchanged.

Renderer defaults < exact Profile binding-rule options < Component Binding
options use whole top-level property replacement, closed typed validation and
Plan-frozen effective values/origins; no recursive or arbitrary static JSON patch.
Provider endpoint/credential and mandatory TLS cannot be overridden by Binding.
StaticContributions/secretProjections retain their independent ADR-057 purpose.

Reference delivery is tracked by [#275](https://github.com/hamstix/haby/issues/275).
Separate [Provider status #274](https://github.com/hamstix/haby/issues/274) observes
endpoint/TLS plus selected SYSTEM-credential login only. It proves no management
privileges, Resource/dbNum/workload state or application delivery authority and
cannot substitute for stronger alias-switch preflight or mint its receipt.
Attachment/consumer verification may separately check effective Resource options.
No current upstream Reference/status implementation is claimed. Upstream Redis
implementation is excluded; its private adapter illustration in ADR-063 does not
register a public type/renderer or authorize fake CREATE/secret delivery workarounds.
The accepted #263 -> TLS #261 -> bounded PostgreSQL import order remains unchanged.

## Delivery and evidence

Deliver bounded sequential slices, each independently reviewed:

1. Generic intent, per-type/mode capability, provenance and authority, including
   persistence and normal planning/execution refusal paths.
2. PostgreSQL existing pair verification and protected application credential,
   ordinary outputs/rendering, atomic internal acceptance and safe retries.
3. API-only operator path, real bounded rehearsal, restart/lost-response recovery
   and delivered configuration use.

The first real fixture includes an application-owned database with public schema
owned by an administrator, relevant existing ACLs/data and an existing password.
Import itself performs only external verification. Assert unchanged external
identities, data, password, owners and ACLs on success, failure and retry, and
assert absence of CREATE, ALTER OWNER, GRANT, rotation and deletion effects.
Cover conflicting/concurrent imports, staged credentials, incomplete association,
restart, lost responses, authority restrictions and unchanged fresh creation.
Use real TLS and private disposable PostgreSQL where relevant, without a broad
new matrix/load campaign or secret dumps.

Use focused regression/red-green evidence and ADR-061 iteration once installed,
then all applicable exact-SHA final qualification. This decision authorizes no
runtime validation or implementation before #261 completes. Existing Kubernetes
Deployment adoption is excluded. This documentation does not complete P3.
