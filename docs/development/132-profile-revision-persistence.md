# Immutable Profile revision persistence

Issue #132 implements the persistence portion of P2 under ADR-006/020/023/026/
033/035/036/037/038. A Profile is installation-scoped reusable declaration
content. Its exact identity is `ProfileId + positive Int32 revision`; it has no
Environment, Provider, Application or folder owner.

## Application boundary

`ImportConfigurationProfile.ExecuteAsync` accepts standalone UTF-8 Profile JSON,
a trusted technical actor and cancellation. It uses the existing strict
`ProfilePayloadParser` and immutable Domain representation. It needs only
`IConfigurationProfileImportStore` and `TimeProvider`. It never resolves a secret,
document, slot, Environment, Provider or output, and never executes a renderer.

Closed outcomes are:

- `Succeeded/Created`: the exact accepted declaration/reference and first-import audit.
- `Succeeded/ExistingEquivalent`: the original accepted declaration and audit.
- `InvalidDeclaration`: the existing bounded, value-free Profile wire report;
  no storage operation is invoked.
- `RevisionContentConflict`: the safe exact reference; no content diff or overwrite.

Equality uses the complete immutable semantic declaration, including all named
secret selectors and projection metadata. Map/projection enumeration order and
omitted versus explicit empty defaults do not change it. Another explicit revision
is independent; there is no next/latest allocation. Collections and value trees
are copied immutable snapshots. Invalid trusted actor shapes throw before storage:
the actor must contain 1-256 characters with no control characters, matching
Application import conventions. JSON cannot supply audit metadata. UTC import time
comes from the injected clock and is stored at PostgreSQL microsecond precision;
the first returned audit uses that same precision.

`IConfigurationProfileRevisionQuery.FindAsync` returns only the requested exact
`AcceptedConfigurationProfile` or null when that identity is absent. Both ports
are asynchronous, accept cancellation, and expose no EF or transport types.
Compose the importer with the registered store and a trusted clock in process.
`AddPostgreSqlManifestCatalog` registers the store/query ports.
[Issue #134](134-profile-operator-path.md) exposes validation/import/exact safe
metadata through the operator Host and habyctl.

## Atomic persistence and cancellation

`configuration_profile_revisions` stores typed Profile identity/revision,
model/format versions, normalized snapshot text, actor and timestamp in one row.
Its composite primary key is the final concurrent uniqueness guard. Revision,
identifier and actor constraints are declarative; C#/EF owns workflow. No new SQL
function, trigger, advisory lock, role separation or Provider-specific column is
introduced. The supported API only inserts/reads; privileged direct edits retain
ADR-023's existing catalog trust boundary.

The store first reads the exact identity using a fresh context. If absent, an EF
transaction inserts the full row. A concurrent insert that loses the precise
primary-key constraint rolls back and reads the winning row through another
context. Other database errors propagate, rather than becoming content conflicts.
Equal contenders converge; unequal contenders preserve the winning complete
declaration/audit and receive conflicts. No caller updates the winner.

Cancellation before the commit boundary rolls back the row and its audit.
Immediately before commit the token is checked; once commit starts the store
waits without caller cancellation for confirmation. Neither it nor the importer
checks cancellation after confirmed commit. An unconfirmed/lost commit response
can still throw; retry the same exact identity/content to retrieve the durable
winner. There is no receipt allocation or automatic new revision.

## Snapshot and schema versions

`ProfileSnapshotCodec` has independent internal **format 1 / model 1** constants.
Application normalized-model/snapshot **2** is unchanged. Internal Profile snapshots
are explicit JSON trees, independently decoded into Domain objects, without the
external Profile API parser, source DTOs, reflective serialization or discovery.

The closed root stores format/model, ID/revision, and required documents,
contributions and rules maps, including their explicit empty forms. Documents
retain file/format/template; contributions retain IDs/document/path/value and
required secret arrays; each secret retains path/format/name/positive version.
Rules retain IDs/slot/renderer/options and exact document/path projections.
Ordinary JSON values remain ordinary, including magic-looking objects and nulls.
Numbers use the existing arbitrary-precision coefficient/exponent representation,
without double/decimal conversion. Named secrets remain unresolved declarations,
never material, resolved SecretRefs, completed connection strings or material hashes.

Reads reject unsupported/inconsistent versions, identity mismatches, unknown or
missing members, duplicate members even inside ordinary values, invalid shapes,
bounds, identifiers, paths, projection duplicates and invalid secret insertions.
Failures throw fixed value-free `InvalidDataException` messages with no unsafe
inner exception. Corruption never becomes not-found and incoming imports cannot
repair/overwrite it. A privileged edit to another valid value is outside this
detection guarantee; snapshots are not signed or checksummed.

Migration `20261105151707_PersistConfigurationProfileRevisions` adds this table.
Startup accepts the complete PR #131 catalog migration prefix ending in
`20261103084056_AddProviderResourceReservations` and applies the additive migration.
No additional catalog reset is required. Superseded alpha schemas, unknown or
inconsistent histories remain refused; startup never resets data. This does not
remove earlier reset requirements for superseded Application snapshots.

## Evidence and current limits

Fast Application/codec tests exercise normalized repeats, all supported content
fields, immutable audit/collections, safe refusal, strict decoding, secret metadata,
exact numbers and pre/post-acceptance cancellation.

`ConfigurationProfilePersistenceTests` runs against disposable real PostgreSQL:

- Fresh service composition reload, exact equivalent/conflicting retry, safe
  corrupt reads and absence of unrelated catalog writes.
- Eight insert candidates synchronized before saving for equal and unequal
  races, independent winner/audit verification and direct uniqueness enforcement.
- Pre-save/post-save cancellation and failure rollback, cancellation at commit
  start/after commit, lost response and same-identity retry.
- Saved ordinary Profiles and a separately saved Application with parsed Binding
  slots feed the actual exact catalog/expander. Environment defaults and a
  Component whole-revision replacement produce different effective logging and
  consumer options; two typed synthetic output families feed ready fragments into
  the actual composer and redacted preview. No real renderer is claimed.
- A not-yet-created secret imports without lookup. Current expansion still
  returns `profile.secret-projections.unsupported` with no layouts.
- Real container-local `pg_dump`/`pg_restore` retains ordinary and secret-bearing
  declarations, exact references and original audit. Fresh restored services read
  and retry with no Provider/secret registration. Private archive cleanup preserves
  primary failures. Independent contexts verify durable results and model/history.
- The current base schema upgrades additively without losing existing data.

The integration suite and Native AOT CI continue to cover existing Application
import/query/provisioning. WSLc remains the documented local Windows fallback.

Profile assignments and distribution authorization, defaults administration,
list/update/delete/rollout, planning integration, secret resolution,
output rendering, materialization and confidential delivery remain deferred.
Persisting a named secret declaration grants no distribution authority. A later
secret-bearing assignment boundary must authorize distribution to its target
before accepting effective configuration for delivery.
