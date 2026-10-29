# Named administrative secrets (issue #88 design notes)

This implements the accepted ADR-029 bootstrap contract on the loopback-only,
unauthenticated alpha registry. It does not implement Provider manifests (#89),
rotation, reveal, deletion, revocation or generated application credentials.

## Transaction boundary

`SecretAdministration` validates names, RequestId and byte count and resolves an
existing Environment. `IAdministrativeSecretCatalog.CreateAsync` is a single
atomic persistence operation. Its PostgreSQL adapter opens one EF Core context
and transaction, locks the existing Environment row `FOR UPDATE`, then checks
the receipt and the Environment/name pair. This intentionally serializes
administrative creates within an Environment, including absent-name races and
the same RequestId racing with different names. Other Environments remain
independent. The lock is the localized ADR-026 raw-SQL exception; EF owns CRUD,
comparison orchestration and commit. No workflow procedure or trigger is added.

The adapter stages Secret, first numbered SecretVersion and payload, saves them
inside that transaction, then stages the current-version link and receipt and
saves again. Only the outer transaction commits. A receipt failure or
precommit cancellation rolls everything back. Equivalent requests with fresh
RequestIds insert their own receipts in the same boundary. Database unique
indexes additionally protect `(EnvironmentRecordId, name)`, `(SecretId, number)`
and `(EnvironmentRecordId, operation, RequestId)`. Receipt foreign keys pin a
version belonging to that Secret in that Environment.

Retry checks compare name, fixed administrative metadata and trusted actor,
then resolve the exact receipt version through the configured internal
`ISecretStore`. PostgreSQL resolution reuses the owning transaction context and
connection, including with a one-connection pool. Natural-name equivalence always resolves numbered version 1,
never the current-version link. Comparisons use exact bytes. Receipts contain
only operation, RequestId, Environment identity, name, exact Secret/version
identities, actor and timestamp. They retain no value or secret-derived hash
and do not expire. Missing or unsupported material fails closed.

The current transactional writer supports the explicitly registered PostgreSQL
Plaintext backend. An unsupported registration returns
`SecretStorageUnavailable`; it never falls back to plaintext or calls an
independently committing store write. Adding another backend requires its own
atomic writer under this boundary. Shared contracts contain no Provider
configuration settings.

Cancellation is checked before commit. Commit itself is not canceled: a lost
response or connection during commit can leave the client uncertain. Retry the
same Environment, name, exact bytes and RequestId. The CLI reports RequestId and
these instructions on transport failure, cancellation during the call or a
storage-unavailable result. It does not claim rollback of a committed request.
Persisted and returned timestamps use PostgreSQL microsecond precision so
initial responses and recovery reads agree.

## Exact API and metadata shapes

The alpha `ManifestOperator` adds:

| RPC | Request fields | Reply fields |
| --- | --- | --- |
| `CreateSecret` | `environment`, `name`, UUID `request_id`, write-only bytes `material` | `status`, optional `secret`, optional `request_id` |
| `GetSecret` | `environment`, exactly one of `name` / `secret_id` | `status`, optional `secret` |
| `ListSecrets` | `environment`, `page_size`, `cursor` | `status`, `items`, `next_cursor` |

`SecretMetadata` contains `environment_record_id`, `secret_id`, optional `name`,
`purpose`, optional `created_at_utc`, and `versions`. Each
`SecretVersionMetadata` contains exact `version_id`, optional positive `number`
and optional `created_at_utc`. Discovery returns the initial numbered version
for named secrets. For a legacy unnamed secret it returns its exact current
reference with no invented number. This keeps each metadata item bounded to
one version in this creation-only slice; enumeration of historical versions
and future rotation remain separate work. Creation replies always contain the
accepted exact version, independent of any later current-version change.
There are no descriptions, payloads, protection keys or hashes in these DTOs.

Create statuses are `Created`, `ExistingEquivalent`, `InvalidRequest`,
`EnvironmentNotFound`, `RequestContentConflict`, `SecretStorageUnavailable`.
Discovery adds `Found`, `NotFound`, `Listed`, `InvalidCursor`, `InvalidPageSize`.
Driver exceptions do not enter result DTOs.

Pagination defaults to 50 (`page_size = 0`) and accepts 1..100. It uses a
limit-plus-one keyset query with PostgreSQL `C` collation (ordinal ASCII order).
Named items sort by name; unnamed legacy items sort afterwards by the internal
ordering key `~<canonical-SecretId>`. This key is never assigned as a name.
The explicit cursor is `secret1:<EnvironmentRecordId>:<ordering-key>`.
The parser bounds its length and rejects unknown versions, Environment cursors,
noncanonical legacy identities, invalid names and another Environment's cursor.
A well-formed position need not still identify a row. The cursor is not an
authorization capability. Traversal is live: inserts after the position can
appear, inserts before it are not repeated, and there is no snapshot promise.
The CLI fetches one page; the caller explicitly follows `nextCursor`.

## CLI input and output

```text
habyctl environment create staging
habyctl secret create pg-admin --environment staging
habyctl secret create pg-admin --environment staging --stdin --request-id <uuid>
habyctl secret get pg-admin --environment staging
habyctl secret get --id <secret-id> --environment staging
habyctl secret list --environment staging --page-size 50 --output json
```

`--environment` overrides the selected context's logical Environment. Neither
context selection nor secret creation implicitly creates an Environment.
No literal secret argument is accepted. Interactive input uses the testable
`ISecretTerminal` abstraction over hidden character reads, UTF-8 encoding and
Enter exclusion. Explicit `--stdin` reads exact bytes to EOF, including BOM,
CRLF and LF, using a 65,537-byte sentinel buffer. Both paths and the server
enforce 1..65,536 bytes. Redirected input without `--stdin` fails. Avoid shell
commands that embed secrets as literals or alter file encodings/newlines.

An omitted `--request-id` generates a UUID once per invocation. Normal text
contains name/version and safe identities, without RequestId. JSON uses the
existing `formatVersion: 1` envelope and generated `CliOutputJsonContext`
metadata; create data contains `requestId` and safe `secret` metadata, while
list data contains `items` and `nextCursor`. Exit codes remain 0 success,
1 typed rejection, 2 usage/input/configuration error and 3 transport failure.
Results go to stdout; prompts, usage/input errors and uncertain-outcome retry
instructions go to stderr. The CLI talks only to registry APIs.

Write-only request diagnostics use `ICustomDiagnosticMessage`, so generated
`ToString()` redacts material and does not snapshot any request fields. Do not
log or explicitly serialize secret-bearing requests with a general protobuf
JSON formatter. Response presenters use an explicit metadata allowlist.
Temporary mutable input/comparison buffers are cleared; immutable protobuf
transport buffers remain subject to managed-runtime memory lifetime.

## Storage and upgrade compatibility

**Plaintext is not encryption. Database-row and backup readers can recover
secret values.** Restrict database and backup access and handle both as
secret-bearing assets. Prefer encrypted protection where available; this slice
does not implement an encrypted backend. The Host retains its explicit
Plaintext registration and existing loopback/TLS boundaries.

`AddNamedAdministrativeSecrets` adds nullable name, version number and creation
timestamps plus the receipt table. Existing unnamed rows retain every SecretId,
SecretVersionId, current-version link and payload; no names, chronological
numbers or historical timestamps are invented. Nullable unique-index entries
allow multiple legacy rows. Existing Provider/history foreign keys and exact
SecretRefs are unchanged. Legacy metadata is discoverable by identity and in
the bounded list. Old-schema integration fixtures seed historical columns
explicitly before applying the new migration rather than writing an old schema
through today's EF model.

## Evidence

The disposable PostgreSQL integration suite covers receipt and natural-name
equivalence, incompatible metadata/name/value reuse, simultaneous contenders,
Environment isolation, restart recovery, exact initial-version pinning,
database rollback after first inserts and before receipt commit, missing and
unsupported material, ordinal bounded/live pagination and historical migration
compatibility. Arrange, act and verification contexts are independent.
Generated-client transport and published Native AOT CLI tests exercise the
production Host and PostgreSQL, as well as the standalone loopback smoke host.
Fast CLI tests cover byte limits, hidden UTF-8/Enter handling, BOM/newline
preservation, redirected-input rejection, redaction and text/JSON behavior.
