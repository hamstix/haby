# ADR-024: Provider instances and immutable configuration revisions

- Status: Proposed
- Date: 2026-10-26
- Scope: One-instance active pilot; design only

## Context

The shadow pilot evaluates an accepted ManifestRevision against hypothetical,
request-scoped Provider selections. The first active pilot must instead plan
against operator-owned state that survives a restart. ADR-016 requires every
accepted generation to retain exact planning inputs; ADR-018 defines
Environment-local Provider aliases; ADR-022 requires execution to load the
pinned Provider configuration without following a changed alias.

The current `ProviderResolution` and alias catalog are in-memory checkpoint
types, not persisted Provider aggregates. This proposal defines their durable
identity and selection semantics before introducing tables or operator APIs.
Secret storage follows the standard modes in ADR-006 rather than a pilot-only
exception.

## Proposed decision

### Environment-owned Provider instances

A ProviderInstance belongs to exactly one Environment. It has a Haby-issued,
typed UUIDv7 `ProviderInstanceId` and cannot be moved to another Environment.
The instance represents one operator-recognized external Provider target, not
one adapter binary or one set of credentials. Its metadata identifies that
target without containing secret material. An operator may prepare an instance
before assigning an alias.

For the first active pilot, at most one alias may refer to a ProviderInstance.
An alias is unique within its Environment. More than one alias for an instance
is deferred until a concrete use case justifies it. Alias reassignment between
instances is explicit and audited; it never rewrites historical resolutions.

The identity of the external target must not be silently changed by a new
configuration revision. A revision that would select an unrelated target
requires a different ProviderInstance. The exact pilot rule for recognizing
the same PostgreSQL target must be specified before implementation; a
successful connection test alone does not establish target identity.

### Immutable configuration revisions

Each ProviderInstance has positive, monotonically increasing local
configuration-revision numbers, starting at 1. The exact reference is
`(ProviderInstanceId, ConfigurationRevisionNumber)`. Revision numbers need
not be gapless. A created revision is immutable; editing settings or changing
the selected adapter creates another revision. There is no replacement or
overwrite operation.

A revision records its exact `ImplementationId`, a versioned configuration
shape, validated non-secret settings and exact `SecretRef` values for secret
fields. It does not contain a plaintext administrative connection string.
The configured implementation must be registered in the build-time catalog
and support the required Resource contract. `ImplementationId` denotes a
compatible implementation, not a particular binary hash. Adapter authors
must preserve the promised semantics when updating code under the same ID.

Changing `ImplementationId` may create another revision of the same
ProviderInstance when the external Provider target remains the same. If an
older implementation is no longer registered after a Haby upgrade, execution
of a generation pinned to it stops with an explicit compatibility error;
Haby must not silently substitute another implementation.

### Alias points to one exact revision

For this pilot an Environment-local alias binding points directly to
`(ProviderInstanceId, ConfigurationRevisionNumber)`. There is no separate
mutable `ActiveConfigurationRevision` pointer on ProviderInstance. At most
one alias points to an instance, and a revision may exist without being
selected by an alias.

The operator workflow is:

1. Create immutable revision 4 while the alias still selects revision 3.
2. Validate revision 4 without provisioning any Resource or revealing its
   credentials. Check the configuration schema, availability of its exact
   SecretRefs, connectivity, authentication and the permissions that can be
   checked without a `CREATE DATABASE` side effect.
3. Switch the alias from the expected old binding to revision 4. The server
   repeats validation of the exact target revision as a mandatory condition
   of this command, then atomically compares and changes the alias binding.
   A failed validation or changed old binding leaves the alias unchanged.
4. New Plans resolve the alias to revision 4. Existing Plans and accepted
   generations retain their exact prior references.

Validation is a point-in-time preflight, not a reservation or proof that a
future `CREATE DATABASE` will succeed. Server conditions and privileges may
change between validation and execution. Only the execution result confirms
the external effect. The validation step must not create a database or expose
the credential in results, diagnostics or logs. Network validation runs
outside the alias-update database transaction; the final alias change uses an
atomic compare-and-swap on its expected old binding/concurrency state.

### Exact planning and execution inputs

Planning resolves `EnvironmentRecordId + ProviderAlias` against server-owned
state to an exact ProviderInstance, configuration revision and
`ImplementationId`. The immutable Plan and accepted generation retain those
typed identities plus the alias used to select them for audit. The alias is
not an execution-time lookup. Plans and generations contain neither plaintext
secrets nor the full Provider configuration payload.

Alias reassignment and configuration-revision creation do not mutate an
accepted generation. A pending Plan whose exact resolution no longer matches
the current alias binding is stale under ADR-016 and must be replanned; the
ChangeSet intent remains unchanged if its deployment preconditions still
hold. Execution loads the pinned configuration revision and exact SecretRefs.
A missing, deleted, wrong-Environment or incompatible pinned input fails
closed before Provider work; it never falls back to a newer revision or the
alias's current target.

For example, with non-secret illustrative identities:

```text
Environment: test
ProviderInstance: pg-main (ProviderInstanceId: <uuidv7>)
Configuration revisions: 3, 4
Alias before switch: postgresql.primary -> (pg-main, 3)
Alias after switch:  postgresql.primary -> (pg-main, 4)
Existing generation: (pg-main, 3, implementation-A)
New Plan:            (pg-main, 4, implementation-B)
```

The human-readable `pg-main` label is not the persisted identity. Both
implementations must explicitly support the requested exact Resource
contract; a changed adapter does not imply a changed external PostgreSQL
instance.

### Retention and deletion

Configuration revisions needed by accepted generation history or unfinished
operations remain available, together with their required SecretVersions.
An alias target is also protected. A valid open Plan may temporarily retain
its exact inputs, but an uncommitted Plan must not block cleanup forever:
Plans require explicit closure or an expiry policy, after which applying them
is rejected and they no longer retain executable inputs. Audit metadata may
remain without making a closed Plan executable again.

An unreferenced preparatory revision that was never selected or accepted may
be deleted. Deletion must atomically check the alias, Plan, generation and
operation references that protect it. The exact retention period, expiry
policy and operation-state integration belong to the durable-state design;
this proposal does not authorize unsafe garbage collection in advance.

## Secret-storage dependency

Provider administrative credentials use exact `SecretRef` identities under
ADR-006. Its PostgreSQL SecretStore may use the standard `Plaintext` or
`EnvelopeEncrypted` protection mode; this Provider proposal selects neither
the deployment's write mode nor its implementation order. `Plaintext` exposes
secret material to readers of the relevant database rows and backups. Prefer
`EnvelopeEncrypted` when that exposure is unacceptable. Neither mode permits
plaintext in Provider configuration revisions, Plans, generations, diagnostics
or ordinary read APIs. Active-pilot execution requires an implemented storage
mode and an explicit operational decision about its threat and backup boundary.

## Implementation sequence after acceptance

1. Implement an accepted standard PostgreSQL secret-storage mode and its exact
   SecretRef resolution boundary.
2. Add typed Provider identities, immutable revision storage and Environment-
   scoped alias bindings with PostgreSQL uniqueness and compare-and-swap
   constraints. Preserve the existing shadow-preview API as hypothetical.
3. Implement side-effect-free revision validation and mandatory revalidation
   in alias switching, with bounded redacted results.
4. Make planning resolve durable aliases to exact references and persist those
   inputs under ADR-016. Implement the separate durable execution/recovery
   decision before enabling Provider effects.

Focused tests must cover uniqueness, immutability, wrong-Environment and
missing-SecretRef rejection, failed validation leaving the alias untouched,
concurrent alias switches, stale Plans, adapter unavailability and retention
of pinned revisions. Real-PostgreSQL tests must prove transaction and restart
behavior with separate `DbContext` instances for arrange, act and assert.

## Consequences and open decisions

The pilot has one mutable selection pointer per alias and no instance-level
active-revision pointer. Revision preparation cannot disturb the selected
configuration. The price is that each switch requires a server-side preflight
and a durable, auditable alias compare-and-swap.

Before implementation, resolve the exact external-target identity rule for a
PostgreSQL ProviderInstance, the versioned representation of non-secret
configuration settings, Plan closure/expiry policy, and the operational choice
of secret-storage mode. The durable operation/recovery policy remains a
separate decision and is not specified here.
