# Environment Resource naming contract

Issue #175 implements ADR-048 for the current internal alpha API. Resource
declarations and normalized ManifestRevision identity remain portable. Logical
Resource IDs and references never receive a physical prefix.

## Declaration and exact identity

The closed declaration is described by
[resource-naming-policy.v1alpha1.json](../../schemas/resource-naming-policy.v1alpha1.json).
It contains only `mode` and, for prefixed mode, `prefix`:

```json
{"mode":"exact"}
```

```json
{"mode":"prefixed","prefix":"staging_"}
```

Unknown/duplicate members, nulls, missing mode, unsupported mode, an exact-mode
prefix (including null), and absent/empty prefixed-mode prefix refuse. Input is
bounded to 1,024 characters; the CLI additionally bounds source bytes to 1,024.
The common prefix bound is 128 characters without control characters. The
consuming adapter imposes its own smaller syntax/length limits at planning.
This is a naming contract, not a generic settings or expression framework.

Operation context supplies the existing Environment name, stable policy `name`
(1–64 lowercase kebab-case ASCII characters), and exact positive int64 `revision`.
Policies are Environment-owned immutable imports. Equivalent repeats return
Unchanged with original actor/time; different content under the same exact
reference returns RevisionConflict. Revisions need not be consecutive and are
never selected implicitly. No delete or overwrite operation is supplied.

The Environment-local Provider alias selects an exact policy revision through a
separate binding extension. Selection cannot change ProviderInstance,
configuration revision, implementation or credentials. Existing Provider alias
changes still require mandatory preflight and their exact binding CAS.

Selection requires both the exact expected Provider binding and expected naming
absence or a current opaque UUID token. Check preconditions before equality.
Changed issues a fresh token and actor/time; Unchanged preserves them. A stale
token refuses even when content is equal. After response loss, read the selection:
matching state proves current state, not which request wrote it. There is no
automatic retry or fabricated receipt. The CLI obtains current preconditions
once unless `--if-absent` or `--if-match` explicitly supplies the naming condition.

## Frozen inputs and lifetime

Absence freezes `ResourceNamingSnapshot(1, null, exact)`; it is a canonical value,
not an execution-time fallback. Explicit selection freezes policy name/revision,
mode/prefix and selection token. Policy import alone changes no binding or Plan.
Relevant selection changes, including absence to explicit exact and A to B to A,
stale existing Plans. Unrelated Environment, alias or policy revision edits do not.

The neutral Plan and generation `naming_json` captures the canonical snapshot.
Planning snapshot format 3 and `haby-operation-plan/v3` fingerprints include it.
The adapter freezes actual names in typed operation inputs, including the login
derivation contract `postgresql.owner-sha256/v1`. Graph comparisons, action input
validation, generated slots, reservations, confirmed outputs, reuse provenance,
materialization and delivery load saved inputs. Execution never reads today's
naming selection. Formatting consumes confirmed physical names without prefixing.
Plan/get reports saved creation targets through neutral `targetName` metadata.
Reused targets retain direct original generation/output provenance.

Plan/Check/Commit acquire a shared selection-key transaction lock; administration
acquires the exclusive key. This covers an absent row. Commit orders exact alias,
naming, configuration and deployment preconditions within its transaction. Naming
selection uses only its Environment/alias key, not a catalog-wide settings lock.
Provider alias CAS joins that same key after preflight, using its existing write
authority. Plan/Check read it with advisory locks and SELECT authority; reviewing
a Plan does not require UPDATE privileges on the Provider alias table.
No external objects, reservations or generated material are created by planning
or desired-state Commit.

Existing/retained Resources are compared against original frozen adapter inputs.
Different physical names refuse UnsupportedShape before accepting new work;
policy changes never rename, recreate, rotate or adopt. Compatible selection
identity changes are significant inputs even when the resolved names are equal:
explicit acceptance can create a generation with zero new external operations.
Provider configuration rebinding remains unsupported for managed reuse.

## PostgreSQL algorithm and bounds

Declared database names retain the existing `[a-z][a-z0-9_]{0,62}` syntax.
Exact uses the declared name. Prefixed uses the complete prefix followed by the
complete declared name, with no separator insertion, guessing, deduplication,
case conversion or Liquid evaluation. The final name must also fit 63 ASCII
bytes. Explicit names are never truncated.

The PostgreSQL prefix is 1–24 ASCII bytes matching `[a-z][a-z0-9_]*`; the reserved
role-name prefix `pg_` is refused. The maximum
is shared by database and generated-login interpretation, even for a standalone
database. A 25-byte prefix refuses rather than discarding distinguishing content.

For `postgresql.owner-sha256/v1`, serialize this canonical compact JSON array as
UTF-8, with both UUIDs in lowercase D format:

```text
[EnvironmentRecordId, ApplicationRecordId, ResourceLocalId]
```

Hash with SHA-256 and encode lowercase hexadecimal. Exact mode is `haby_a_` plus
the first 48 hex digits (192 hash bits, 55 bytes total), preserving the bootstrap
owner algorithm. Prefixed mode is the complete prefix plus `haby_a_` plus the
first 32 hex digits (128 hash bits); 24 + 7 + 32 = 63 bytes. No generation,
ApplicationVersion, display name, time, random retry value or secret contributes.
Independent catalogs already allocate distinct durable Environment/Application
record UUIDs; no installation registry is introduced. Restore preserves the owner.
A catalog clone is not a new independent authority over the same external data.

Unknown derivation data and unavailable naming handlers refuse safely. The
synthetic queue test accepts `contour/`, demonstrating that SQL identifier rules
belong to PostgreSQL rather than common persistence, Host or CLI.

## Storage and collision boundary

`resource_naming_policies` uses `(Environment, Name, Revision)`, a canonical typed
versioned snapshot and original audit. `resource_naming_selections` uses
`(Environment, Alias)`, a same-Environment policy foreign key, Provider-alias
foreign key, token and last-change audit. No Provider-specific catalog columns,
EAV properties, workflow functions or triggers are added.

This is a breaking alpha planning/input change. Existing catalogs containing
Plans or generations from before AddResourceNaming must be explicitly replaced
with fresh catalogs; startup refuses them. Empty planning catalogs can receive
the additive schema. No inferred-name backfill, history rewrite, silent reset or
external-resource cleanup occurs. Current-build restart and dump/restore retain
exact naming snapshots, selections, owners, material and old document grants.

Reservations remain local to one catalog. Prefixes neither prove global
uniqueness nor grant access. In particular:

```text
staging_ + shard_orders == staging_shard_ + orders
```

Operators must choose non-overlapping external namespaces. External occupied-name
checks and definitive PostgreSQL duplicate responses still refuse Conflict;
uncertainty remains action-specific and non-replayable. There is no adoption,
password reset, owner transfer, random fallback, endpoint identity merging,
distributed registry or prefix-wide grant. PostgreSQL privileges establish access.
