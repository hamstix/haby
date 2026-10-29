# ADR-029: Named administrative secrets and durable creation receipts

- Status: Accepted
- Date: 2026-10-29
- Scope: Administrative secret bootstrap through the alpha operator API and CLI

## Context

[ADR-028](0028-operator-bootstrap-and-provider-manifests.md) requires explicit
secret creation and safe retries before Provider bootstrap. Operators need
names and version numbers instead of recording internal UUIDs. The existing
SecretRef remains an exact SecretId and SecretVersionId pair; operator naming
does not change that domain contract.

This decision defines planned behavior for issue #88. It does not claim that
the operator endpoints or commands are already implemented.

## Decision

### Scope administrative creation explicitly

The first command creates Provider administrative credentials, independent of
the concrete Provider type. Metadata is Owner = Environment,
Purpose = ProviderAdministrator, Sensitivity = Credential and ownership policy
Managed. Generated application credentials require a separate ownership and
creation contract; do not silently reuse these fixed administrative semantics.

The Environment must already exist. The server resolves its logical name to
the persisted Environment identity; secret creation never implicitly creates
an Environment. The server supplies the trusted technical actor.

### Use a name and explicit version for operator references

A Secret name is unique within its Environment, uses lowercase kebab-case and
has a maximum length of 64 characters, following the accepted logical-name
rules. The operator supplies the name; the server assigns internal SecretId
and SecretVersionId identities and creates version number 1.

The intended command shape is:

```text
habyctl secret create pg-admin --environment staging
```

Repeating create with the same Environment, name and exact value returns the
existing version 1. A different value is a conflict. Create always compares
against the initial version 1, including after a future rotation; it never
selects the current version implicitly. Rotation is a separate future command
that creates a new immutable numbered version.

Provider input references may select a Secret name and explicit positive
version number. When accepting a Provider configuration revision, the server
resolves that selection within its Environment to an exact internal SecretRef
and persists the resolution immutably. Rotation does not change accepted
Provider revisions. Implicit latest selection is forbidden. This clarifies
ADR-028's SecretRef-first input policy; exact Provider JSON field names and
schema remain a separate decision.

### Combine name equivalence with durable request receipts

Each API creation request carries a UUID RequestId. Its uniqueness scope is
EnvironmentRecordId + operation kind + RequestId; this operation kind is
administrative secret creation. An operator may supply --request-id; otherwise
the CLI generates one once and reuses it for retries within that invocation.
A fresh invocation without the old RequestId still benefits from name
equivalence, but is a new request identity.

The Secret, first SecretVersion and receipt are committed atomically. A
receipt retains the request identity, exact result reference, timestamp and
trusted technical actor. It does not retain another copy of secret material.
Requests that find an equivalent existing named Secret also persist their
receipt atomically with accepting that result.

An identical repeat returns ExistingEquivalent and the original exact
SecretRef. Reuse with a different name, value or other significant metadata
returns RequestContentConflict without changing the accepted result or
creating another Secret/version. Concurrent requests, lost responses and Host
restarts must preserve these rules. Different RequestIds for the same name
and value converge on the same first version; different values conflict.

Compare exact secret bytes without whitespace, newline, encoding or case
normalization. For a retry, resolve the originally accepted exact version
through the internal SecretStore and compare its material without exposing
it. No public secret-derived fingerprints or duplicate plaintext receipt
payloads are introduced. An unavailable or unsupported stored material format
returns a safe storage-unavailable outcome, never a new creation.

Receipts do not expire in this slice. Deletion, revocation, rename, name reuse
and receipt cleanup are deferred until policies protect historical references
and prevent an old request from becoming a new creation. Numbered-version
metadata for existing unnamed secrets requires explicit compatibility handling;
do not invent names or reinterpret their identities silently.

### Keep input exact and output safe

Interactive input uses the small testable hidden-input abstraction accepted
in ADR-027, without a library solely for masking. Encode interactive input as
UTF-8; the terminating Enter is not part of the secret. Explicit --stdin reads
exact bytes until EOF, preserving BOM and newline bytes. Redirected input
without --stdin fails rather than falling back to a prompt.

Accept 1 through 65,536 bytes inclusive, using bounded reads. Provider-specific
credential validation remains the responsibility of its typed adapter. Secret
literals are not CLI arguments, local configuration or diagnostic content.

Creation returns metadata and SecretRef only. Ordinary text output shows the
name and version without RequestId. JSON includes RequestId and exact internal
identities using generated System.Text.Json metadata. After an uncertain
outcome, CLI diagnostics include RequestId and safe retry instructions.
Prompts and diagnostics use stderr, preserving stdout for command results.

Metadata get by name or internal identity and bounded list within Environment
expose safe identities, name, version numbers, purpose and creation time.
Follow issue #87's shared pagination rules. Free-form descriptions are deferred.
Neither metadata reads nor creation reveal secret material; reveal requires
the separate authorization and audit decision described by ADR-027.

### Preserve cancellation and protection boundaries

Cancellation before the transaction's commit phase rolls back. Once commit
has started, cancellation or a lost connection may leave the client uncertain;
retry with the same RequestId resolves the result. Never promise to undo an
already committed transaction or create another version to recover an answer.

Typed results distinguish Created, ExistingEquivalent, InvalidRequest,
EnvironmentNotFound, RequestContentConflict and SecretStorageUnavailable.
They do not contain secret values, fragments, secret-derived fingerprints or
raw driver exceptions. gRPC and CLI preserve the same outcome semantics.

Creation uses the configured protection backend. Plaintext is a fully
supported standard mode, not hardcoded into the operator contract. With
Plaintext, database and backup readers can recover secret values; operator
documentation must state this exposure and recommend encrypted protection
where available. This slice does not implement an encrypted backend.

## Required validation and boundaries

Prove repeated and concurrent requests, incompatible RequestId reuse, distinct
Environment isolation, same-name requests with different RequestIds, restart
and lost-response recovery, rollback before commit, unavailable stored material,
exact stdin bytes and size boundaries, and absence of secret leakage. Use real
disposable PostgreSQL for atomicity and uniqueness, with independent DbContext
instances for arrange, act and assert. Preserve Native AOT and generated-client
checks.

No rotation, reveal, deletion, revocation, inline Provider credentials,
application credential generation, remote exposure or authentication is added
by this decision. Provider bootstrap remains a separate typed declaration and
preflight slice.
