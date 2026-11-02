# Typed secret ownership and stable generated slots

Issue #108 implements the first internal slice of accepted
[ADR-034](../adr/0034-application-credentials-and-postgresql-role-policy.md).

## Contract and trust boundary

`ManagedResourceKey(EnvironmentRecordId, ApplicationRecordId, ResourceLocalId)`
uses persisted internal identities and the stable Resource declaration ID.
`GeneratedCredentialSlot(owner, slotId)` adds the supported slot
`application-password`. Neither identity contains a Provider, database name,
role, deployment generation, storage backend or generation profile.

`IGeneratedCredentialSlots.GetOrCreateAsync` is for trusted application code.
It checks existing Environment and Application catalog identities and a typed
valid Resource local ID. It does not require a database Resource, Provider
registration or accepted external provisioning. It does not establish public
authorization or prove membership in a committed operation graph. The later
graph integration must bind the producer to the exact accepted Resource.

The explicit `SecretOwner` alternatives are Environment and Resource.
Supported purposes are Environment/ProviderAdministrator and
Resource/ApplicationLogin. Both use Credential sensitivity,
`application/octet-stream`, Managed ownership and the selected PostgreSQL
Plaintext backend. The generic writer requires explicit typed metadata;
Resource producers must use atomic slot acceptance. Internal resolution requires
the exact typed owner/purpose and SecretRef. Administrative version addition
cannot rotate Resource credentials.

`random-password/v1` generates 32 cryptographically random bytes and stores their
43-byte unpadded base64url ASCII encoding. Temporary random, encoded and payload
buffers are cleared. Ordinary results contain no material, verifier or hash.
Internal material resolution still returns disposable `SecretMaterial` and
requires callers to clear copies they own. Plaintext catalog and backup readers
can recover passwords; no encryption or new external backend is implemented.

## Outcomes and persistence

| Outcome | Meaning |
| --- | --- |
| Created | One complete candidate transaction was confirmed. Includes exact SecretRef. |
| Existing | The same owner/slot/profile has valid saved metadata, version, payload and audit. Includes the original exact SecretRef. |
| InvalidOwner | Invalid owner/slot or a missing catalog Environment/Application; no candidate is accepted. |
| ProfileConflict | Unsupported first-use profile or a different profile for an existing slot; no replacement/version is created. |
| MaterialUnavailable | Saved material, exact version, association, owner metadata or creation audit is missing/inconsistent. No regeneration. |
| StorageUnavailable | A bounded storage failure, including an ambiguous commit whose winner cannot be safely reread. No second creation in the invocation. |

One catalog transaction persists Secret, immutable first version, protected
payload, unique natural-key slot association and creation audit. Profile is a
field of the immutable association, not part of its primary key. A competing
candidate rolls back all its rows before reading the committed winner through a
fresh DbContext. There is no nested independently committing SecretStore write.

With Read Committed isolation, a winner can commit between an absent-slot lookup
and the retained-secret check. Before treating that Secret as orphaned, reread
the same slot and validate the complete winner. An absent or inconsistent
association still refuses without generating a replacement.

The immutable creation audit records trusted technical attribution and UTC time.
Its unique SecretId foreign key joins the immutable slot's safe owner, slot,
profile and exact SecretRef identities without copying material. Existing calls
do not add another audit. Declarative database constraints enforce uniqueness
and the exact Secret/Environment/Application/Resource/current-version tuple.
The tuple's nullable columns preserve administrative Secrets; its non-null slot
foreign key cannot point at an administrative Secret or change the accepted
owner/version. This additional constraint is explicit migration DDL because an
EF principal alternate key would make administrative owner columns non-nullable.

Under ADR-026, this slice installs no functions or triggers. EF SaveChanges
(synchronous and asynchronous) rejects slot/audit updates/deletes, generated
metadata changes and invalid acceptance associations. The first null-to-version
pin is allowed only for an existing immutable initial version; subsequent pins
are refused. Detached writes are checked against persisted Secret classification.
The acceptance transaction and database uniqueness still arbitrate concurrent
candidates. Existing merged secret-version/payload triggers remain unchanged.

Optional direct-SQL defense in depth can use a restricted catalog writer,
separate from the migration owner: grant SELECT/INSERT on generated slot/audit
tables, never UPDATE, DELETE or TRUNCATE. This is not a mandatory deployment or
runtime role-separation requirement. The supported internal write paths enforce
their invariants through C#/EF transactions, concurrency predicates and declarative
constraints; privileged direct SQL maintenance is outside that guarantee.
Secret metadata needs SELECT/INSERT and only column-level
UPDATE(current_version_id); the composite FK prevents even that permitted column
from moving away from an accepted generated slot. Do not grant broader table
UPDATE through PUBLIC, inherited roles or ownership. For this internal service,
an administrator can grant an existing non-owner `catalog_writer` role:

```sql
GRANT USAGE ON SCHEMA public TO catalog_writer;
GRANT SELECT ON environments, applications TO catalog_writer;
GRANT SELECT, INSERT ON secrets, secret_versions, secret_payloads,
    generated_credential_slots, generated_credential_creation_audits TO catalog_writer;
GRANT UPDATE (current_version_id) ON secrets TO catalog_writer;
```

These are the secret service's minimum grants, not a complete Host role setup.
Schema initialization remains migration-owner work; no runtime role, grant or
external Provider effect is provisioned automatically. Database owners and
superusers remain privileged administrators capable of direct corruption;
EF checks do not protect SQL issued with those privileges. Disposable tests
prove real service create/retry with the restricted writer and SQL mutation
refusal, alongside fail-closed handling of deliberately corrupted owner data.

Cancellation is honored before commit begins. Commit and ambiguity reread use
an uncancelled token after that boundary. A lost response or service restart
uses the same natural key, without a new RequestId/receipt scheme. If reread is
unavailable, the result is StorageUnavailable; a later explicit invocation
resolves the same committed slot. Missing payloads or orphaned Resource secrets
cannot trigger regeneration. Unused/failed material is retained indefinitely;
rotation, deletion, cleanup and recovery are separate decisions.

Administrative create, receipt retry, get/list and name resolution explicitly
select Environment/ProviderAdministrator records. Resource credentials are
unnamed and excluded even when queried by their internal ID. Provider revision
acceptance and password resolution require administrative metadata. Operator
wire responses retain their material-free purpose spelling through explicit
mapping; no new RPC, CLI command or confidential endpoint is added.

## Catalog initialization and verification

This schema revision requires an explicitly initialized fresh catalog under
[ADR-033](../adr/0033-development-compatibility-and-catalog-resets.md).
The unmerged migration has a new identity so catalogs from earlier PR revisions
are also rejected rather than silently retaining their removed triggers.
Startup rejects an earlier migration set and never resets persistent data.
The migration refuses to reinterpret populated earlier secret metadata. There
is no old-alpha reader, metadata backfill or populated downgrade path. A catalog
reset does not remove external resources and requires a separate operator
decision; implementation tests use only owned disposable databases.

Fast guard tests run without infrastructure. `GeneratedCredentialSlotTests` in
the separate PostgreSQL suite exercise a non-database queue-user owner without
Provider registration, synchronized candidate races, exact bytes across restart,
profile/owner isolation, missing/corrupt state, rollback/cancellation and lost
commit responses with successful/failed reread. They check administrative and
Provider isolation and real `pg_dump`/`pg_restore` of the current build, retaining
the same reference, bytes and audit on subsequent use. Persisted arrange, act
and verification use independent contexts.

No operation outputs, graph admission expansion, role/grant effects, recovery,
secret delivery or confidential rendering/API is included.
