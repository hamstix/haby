# ADR-039: Scoped Profile assignment administration

- Status: Accepted
- Date: 2026-11-06
- Scope: P2 persisted assignments and operator administration
- Extends: ADR-026/027/033/035/037/038

## Decision

### Identity, scope and existence checks

One assignment is identified by stable Environment record identity plus its exact
target: Environment, Application(applicationId), or Component(applicationId,
componentRef). Operators select the Environment by name. There is no independent
assignment name. Reuse ADR-038's portable JSON without Environment, concurrency
or validation-version fields in the document.

The Environment must exist. Its defaults may be prepared before any Application
is imported. Application assignments require an already imported Application;
no deployment is required. Component assignment apply additionally requires an
explicit ApplicationVersion in operation context. The server resolves that exact
accepted ManifestRevision and checks componentRef there, never selecting latest.
ApplicationVersion is required only for Component apply and is rejected for other
targets. It is not assignment identity or a condition on future applicability.
Retain the checked exact ManifestRevisionRef in last-change audit for Component
assignments. Logical Application/Component references remain the assignment target.

A later version with the same Component receives the assignment. If the Component
is absent from the planned revision, its assignment does not participate. Rename
does not transfer assignments. Exact-read needs no ApplicationVersion and may read
an existing assignment even when its Component is absent from newer revisions.

### Full replacement and inheritance

Apply replaces the complete local decision set, at most 64 unique ProfileIds.
Order is insignificant. Omission inherits; Use selects an exact Profile revision;
Disable suppresses that ProfileId. Reuse Environment -> Application -> Component
whole-Profile precedence, never JSON merging or priority by input order.

Every Use requires an existing, readable exact Profile revision. Missing,
unsupported or corrupt content refuses the write. Disable may name an ID absent
from both catalog and parents. No secret name lookup/material read, renderer or
output lookup is performed. Full effective-set document/path/slot compatibility
belongs to expansion/planning, not assignment acceptance.

An empty decision set explicitly restores inheritance. Keep the persisted empty
record and its concurrency identity; no delete API is required. Absent and empty
are equivalent for inheritance but distinct for reads, concurrency and audit.

### Trusted administrator and secret distribution

The isolated operator API is used by a trusted administrator. Assigning a Profile
is explicit permission to include every secret it declares in configurations of
Components receiving it. An Environment assignment also covers future inheriting
Components. No separate per-secret operator authorization check is performed in
this pilot. This supersedes ADR-038's earlier per-secret permission-check rule.

Secret-bearing Profiles may be assigned even when the named secrets do not yet
exist. Assignment stores declarations/references only. Planning later resolves
named versions to exact Environment-scoped SecretRefs; delivery requires a
separately accepted authorization/transport contract. Assignment does not enable
secret resolution or remove the current profile.secret-projections.unsupported
expansion refusal. No new API exposure or authentication mechanism is introduced.

### Concurrency, audit and uncertain responses

Every write requires exactly one typed operation precondition: ExpectedAbsence or
ExpectedConcurrencyToken. Tokens are opaque server-issued record identities,
not DesiredGeneration. Allocate a fresh, non-reused token on each actual change.
Atomically enforce unique scope and compare-and-swap with EF Core transactions
and declarative constraints under ADR-026. No new workflow triggers/functions or
mandatory database-role separation.

Check the precondition before content equality:

- ExpectedAbsence and no row: Created, including an explicitly empty assignment.
- Matching token and changed decisions: Updated, fresh token and last-change audit.
- Matching token and equivalent decisions: Unchanged, preserving token and audit.
- Existing row for ExpectedAbsence, or missing/different token: Conflict, even if
  proposed decisions equal current content. No partial write.

Scope/reference validation still applies to a successful apply. A successful
Unchanged response preserves the earlier audit and checked revision; it does not
rewrite history merely because another validation version was supplied.

Persist current target/decisions, token, trusted technical actor, UTC last-change
time, and checked Component ManifestRevisionRef where applicable. Full edit
history and request receipts are deferred. Reads return exact local state or
NotFound; corrupt/unsupported/unavailable storage is a safe failure, not NotFound.
All persistent operations are asynchronous and cancellation-aware. A confirmed
commit is not converted into cancellation by a late token check.

CLI defaults to read-then-conditional-apply. Explicit --if-match or --if-absent
skips automatic precondition acquisition. Never retry a conflict automatically.
After a lost response, read and compare current decisions. Equality proves the
current state, not which request wrote it. Do not blindly resubmit or fabricate
an original success/audit receipt.

### Operator surface

Commands:

```text
habyctl profile assignment validate assignment.json
habyctl profile assignment apply assignment.json --environment production
habyctl profile assignment apply component.json --environment production --application-version 2.4.0
habyctl profile assignment get --environment production
habyctl profile assignment get --environment production --application processor-service
habyctl profile assignment get --environment production --application processor-service --component api
```

Environment selection follows the existing configured-context/explicit-override
convention. Portable validate checks only the existing declaration parser, without
scope or reference lookup. Apply's target comes from JSON; get's target comes from
explicit options, with --component requiring --application. Missing/foreign or
contradictory context/precondition input refuses rather than guessing.

Get returns local decisions, exact scope, token and last-change audit, including
checked revision where applicable. No effective inheritance preview yet. Apply
returns Created/Updated/Unchanged, or typed validation/reference/scope/precondition
refusal. Keep safe distinctions between expected refusals and infrastructure or
corruption failures. Use existing text/JSON envelope, exits and cancellation.

Assignment responses contain no Profile templates, options, secret selectors,
snapshots or material. CLI is API-only, production Native AOT, using generated
gRPC and System.Text.Json metadata. Keep shared contracts/persistence Provider-neutral.

### Planning and separate Profile read expansion

Changing assignment performs no planning, rollout, rendering or generation update.
Future planning must snapshot exact scope decisions/tokens, effective Profile
revisions/options and required resolved references consistently; accepted Plans
and generations never re-read mutable assignments. That integration is a separate
slice, not a capability of persisted administration.

Separately accept expansion of profile revision get: JSON output will include the
complete normalized portable Profile declaration plus exact identity/audit; text
may retain its short summary. This is not original file bytes. Templates/options
and declarative named-secret references are returned as stored, without resolving
or reading secrets. Ordinary values are returned as-is; mistakenly embedded
credentials cannot be reliably redacted. No automatic material lookup or magic
JSON interpretation is allowed. Implement via explicit generated gRPC/JSON DTOs
in a separate issue; PR #135's metadata-only read remains current until then.

## Required evidence and delivery boundary

Prove generated client -> actual Host -> Application -> disposable PostgreSQL,
restart and published Native AOT CLI for the three target kinds. Cover existing
Environment/imported Application, exact Component validation version with
out-of-order imports, missing scopes/revisions/Components, and later Component
absence without assignment transfer. Prove CAS races, stale-token refusal even
for equal content, Unchanged audit, empty inheritance and safe corrupt reads.
Use separate DbContexts for arrange/act/assert, current-build migration and real
backup/restore evidence. Stored decisions must reach the existing pure expander
through real read ports; retain Provider-neutral synthetic-family tests.

Secret-bearing assignment succeeds without SecretStore lookup/material access;
the existing expansion gate still refuses without partial layouts. Responses,
logs and diagnostics expose no Profile values or secret selectors. Verify
source-generated JSON, CLI exits/config selection and required AOT CI.

First implement persisted assignment administration, not Profile full-read,
planning integration, materialization/delivery, selectors/groups, deletion,
automatic rollout, history/receipts or additional Provider effects. Accepted
design does not mean those capabilities are already implemented.
