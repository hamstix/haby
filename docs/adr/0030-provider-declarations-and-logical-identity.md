# ADR-030: Provider declarations, logical identity and exact preflight dispatch

- Status: Accepted
- Date: 2026-10-29
- Scope: Named Provider bootstrap, alpha operator API and Native AOT CLI

## Context

ADR-024/028 establish Provider-neutral persistence and explicit bootstrap.
ADR-029 adds named administrative secrets. Issue #89 needs a concrete input
contract, retry rules and exact typed preflight dispatch. A Provider represents
an operator-recognized logical instance; its connection address cannot reliably
identify a physical cluster and must be changeable with explicit consent.

This decision specifies planned behavior. It supersedes ADR-024's immutable
host/port target rule; implementation must evolve existing checks compatibly.

## Decision

### One JSON declaration creates one immutable configuration revision

The accepted input shape is:

```json
{
  "apiVersion": "haby.dev/v1alpha1",
  "kind": "Provider",
  "metadata": {
    "name": "pg-main",
    "environment": "staging"
  },
  "spec": {
    "revision": 1,
    "providerKind": "postgresql/v1",
    "configurationContract": "haby.postgresql.connection/v1",
    "implementationId": "<registered-implementation-id>",
    "properties": {
      "host": "postgres.internal",
      "port": 5432,
      "maintenanceDatabase": "postgres",
      "administratorUserName": "registry-admin"
    },
    "secretRefs": {
      "administrator-password": {
        "secretName": "pg-admin",
        "version": 1
      }
    }
  }
}
```

The implementation ID above is a placeholder, not a registered implementation.
The displayed envelope members are required; version/revision numbers are
positive integers. Unknown members and invalid/null required members fail
closed. The PostgreSQL properties object has exactly the four displayed
required fields, with no input defaults. Other adapters own their own closed
properties contracts. Empty secretRefs is allowed only when the selected
contract declares no required secret slots.

Environment must already exist. Provider name is unique within Environment,
lowercase kebab-case, maximum 64 characters. An explicit --environment must
match metadata.environment; mismatch fails before writes. Context selects the
registry and never silently replaces the declaration's Environment.

Provider kind, exact configuration contract and ImplementationId have separate
meanings. Registered adapters explicitly define supported combinations; IDs
are not inferred from prefixes or selected by registration order. Provider kind
does not change across revisions of one instance. A compatible implementation
or configuration contract may change through a validated new revision.

### Keep typed Provider details outside shared persistence models

Shared envelopes, persistence rows and CLI contracts do not acquire mandatory
host, port, administrator or password fields. A JsonElement may temporarily
carry properties at intake, then a registered adapter parses a closed typed
DTO using generated System.Text.Json metadata. Arbitrary JSON, dynamic CLR
activation and reflection discovery are not the domain model.

The adapter registers allowed/required credential slots. Each secretRefs entry
selects a name and explicit version under ADR-029 in the Provider Environment.
Unknown slots, missing required slots or unavailable exact versions fail.
Acceptance resolves references without reading material and atomically stores
the immutable typed payload and its exact SecretRef associations. No live name
or latest lookup remains in accepted configuration. Inline credential material
is rejected; its optional input-only block belongs to the later M6 backlog.

### Apply is atomic, immutable and naturally retryable

A new name creates the instance and revision 1 atomically. No empty instance
survives failed revision creation. New revisions have explicit numbers above
the highest previously allocated number; gaps are allowed. Existing numbered
revisions are checked for equivalence before the new-number rule is applied.

Environment + Provider name + revision is the natural request identity:
equivalent validated configuration returns ExistingEquivalent, differing
content returns RevisionContentConflict, and a new accepted revision returns
Created. Comparison includes exact resolved SecretRefs and ImplementationId;
JSON whitespace and member ordering are not significant. No separate RequestId
receipt is required. Concurrent repeats and lost-response retries converge on
one accepted revision.

Apply validates registration, schema, semantics and reference integrity, but
does not connect to the Provider, run preflight, switch aliases or provision.
Server unavailability does not prevent preparing a valid configuration revision.

### Preserve logical identity through explicitly confirmed endpoint changes

The server assigns a stable internal ProviderInstanceId to the logical
instance. The adapter compares endpoint settings with the highest accepted
configuration revision, regardless of which revision an alias selects.
For PostgreSQL this comparison concerns configured host and port.

A changed endpoint without confirmation returns IdentityConfirmationRequired
without writes. The intended explicit retry is:

```text
habyctl provider apply --file provider.json --confirm-logical-identity --expected-previous-revision 3
```

Confirmation is input to the acceptance command, not a persistent manifest
permission to accept future transfers. The server atomically verifies the
expected previous revision and records consent with the accepted revision,
previous revision, trusted actor and time. A concurrent newly accepted revision
invalidates that expectation; do not confirm against a different baseline.
An already accepted equivalent revision returns without repeating confirmation
or its audit entry. Secret or compatible adapter changes alone do not constitute
a PostgreSQL endpoint change.

Preflight cannot prove physical cluster identity, even at an unchanged address.
The operator decides whether a transfer preserves logical identity or requires
an independent new Provider. Existing Plans/generations and payloads remain
pinned and unchanged. Legacy endpoint-derived target values must not continue
to veto an explicitly confirmed transfer. Use reviewed additive compatibility
changes where needed, never rewrite history or merged migrations.

### Resolve exact handlers without preventing metadata access

Current-build registrations must be valid and unambiguous for each exact
ProviderKind + ConfigurationContract + ImplementationId combination. Invalid
or overlapping registrations fail startup. A handler may explicitly support
several combinations; each requested combination resolves to exactly one.

Missing support for a stored revision does not prevent Host startup or ordinary
metadata/history reads. Operations requiring absent support return typed
availability/compatibility failures; other Providers remain usable. Metadata
availability does not imply payload decoding, preflight or execution capability.
Decode by the persisted payload format/version with explicit compatibility
readers. Never substitute another implementation, contract or revision.

Preflight loads the exact saved configuration and checks its exact secrets,
connectivity, authentication and available privilege evidence with deadlines
and cancellation. It performs no external mutation, including temporary probe
objects. Results are bounded safe categories, not driver text or credentials.

### Keep alias changes separate and conditional

Alias set names the target Provider and explicit revision, plus expected alias
absence or its exact old binding. Server-side preflight is mandatory; prior
check success is not reusable authorization. Network work occurs outside the
alias transaction. Final compare-and-swap verifies expectation and exact target
availability atomically. Failure leaves binding unchanged.

Preserve one alias per instance. Repeating an already reached target returns
Unchanged without duplicate successful-change audit. No force, implicit latest,
mutable active-revision pointer or apply-time alias switch is introduced.
Changing alias does not rewrite historical resolutions.

### Expose operator commands and safe metadata

```text
habyctl provider apply --file provider.json
habyctl provider get pg-main --environment staging
habyctl provider list --environment staging
habyctl provider revision get pg-main --revision 1 --environment staging
habyctl provider check pg-main --revision 1 --environment staging
habyctl provider alias get postgresql.primary --environment staging
habyctl provider alias set postgresql.primary --provider pg-main --revision 1 ...
```

The ellipsis stands for mandatory expected-binding/absence arguments, not an
executable command example. Get/list return bounded metadata: names, identities,
kind, revision/contract/implementation references and safe exact references.
Do not dump full configuration payloads or secret material. Follow existing
pagination, generated JSON, Native AOT and API-only CLI principles.

Existing unnamed instances and exact references remain usable. Do not infer
names, merge instances by address or silently assign names. Explicit name
adoption, if needed, is a separate operation.

## Delivery and required evidence

Split #89 into #95 (named declarations/revisions), then #96 (exact preflight
dispatch and operator alias commands). #95 depends on #88; #96 depends on #95.
Use PostgreSQL and a second typed fake through actual declaration, persistence,
reload, preflight and CAS boundaries. This proves seams, not a second production
Provider. Test concurrent apply, equivalence/conflicts, atomic rollback,
confirmed transfer and stale confirmation, old pinned reads, missing handlers
with successful startup/metadata reads, startup ambiguity, mandatory preflight,
failed/stale alias changes, redaction and published Native AOT execution.

No new external effects, general plugin framework, recovery policy, inline
credentials, authentication or remote exposure is authorized by this decision.
