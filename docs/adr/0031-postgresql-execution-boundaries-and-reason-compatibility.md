# ADR-031: PostgreSQL execution boundaries and legacy reason compatibility

- Status: Accepted
- Date: 2026-10-29
- Scope: Behavior-preserving execution refactor under issue #90

## Context

The database-only operator rehearsal validated bootstrap, explicit desired-state
commit, execution, safe repeats and restart persistence. Before adding roles or
grants, the Provider-neutrality audit found database-specific execution inputs
and ports in Haby.Application. These belong to the PostgreSQL adapter.

DatabaseNameInvalid also appears in persisted history, protobuf and CLI JSON.
Changing these representations during a structural refactor would mix a source
boundary correction with a storage/wire migration. The common reason model
still needs a mandatory Provider-neutral successor before new effect kinds.

## Decision

### Preserve execution behavior while correcting source boundaries

Keep operation keys, states, journal/query boundaries, the public executor
interface and safe execution results in the application layer. They must not
acquire resource-specific target fields or depend on concrete adapters.

Move the concrete database-create input, including DatabaseName and exact
Environment/Provider/configuration/implementation references, into Haby.PostgreSql.
Use an explicitly specialized name such as PostgreSqlDatabaseCreateInput.
Move IDurableCreateTarget's database existence/dispatch port and its concrete
low-level outcomes into the PostgreSQL layer with equally explicit names.
Update the existing executor, input resolver and recovery dependencies.

Do not introduce a universal target interface, Dictionary, unvalidated JSON,
dynamic activation, object target payload or generic execution framework in
this refactor. Planning/admission and executor selection are designed under
issue #91 against the actual next effects.

Preserve dispatch ordering, cancellation, retry, uncertainty classification,
singleton serialization, terminal persistence and AppliedGeneration behavior.
SQL must still require a confirmed durable Dispatching marker. No external
effect, new admission rule or recovery policy is added.

### Retain legacy representations explicitly, not as the target architecture

The existing durable DatabaseNameInvalid reason remains temporarily available
for PostgreSQL compatibility. Preserve all of:

- historical persisted DatabaseNameInvalid strings and their readers;
- protobuf OPERATION_REASON_DATABASE_NAME_INVALID numeric value 4;
- CLI JSON databaseNameInvalid and historical operation/recovery rendering;
- existing mappings from the PostgreSQL-specific input validation result.

Do not rename stored values, reuse enum numbers, silently reinterpret history
or change existing JSON outcomes. The moved adapter-local validation outcome
may have a specialized type; it maps to the unchanged durable legacy reason.

This specialized reason in the common catalog is recorded technical debt, not
the desired generic operation contract. Issue #91 must define a Provider-neutral
reason model and explicit compatibility mapping. Implement that model before
admitting execution beyond the current PostgreSQL database-create effect,
including application-role/grant effects. Legacy values remain readable and
renderable through explicit compatibility handling; they must not prescribe
database semantics to future Providers. No speculative new reason codes are
introduced by #90.

Review common reason documentation. Conflict and ServerRejected describe
generic conflict and definitive rejection categories; database existence,
PostgreSQL-specific driver behavior and CREATE DATABASE details belong in
adapter documentation. Do not change classification or severity during this
wording correction.

### Keep recovery specialized and storage unchanged

The ADR-025 PostgreSQL database assignment policy remains specialized. Updating
its dependencies to renamed PostgreSQL inputs/ports does not authorize applying
that policy to RabbitMQ users, HTTP objects or other effects. Preserve explicit
consent, exact uncertain-attempt checks, existence evidence and durable audit.

No database migration or wire change is expected for this refactor. If an
implementation discovers a concrete necessity, explain it to the maintainer
before expanding scope. Do not edit merged migrations or add stored workflow
procedures/triggers. Preserve source-generated metadata and Native AOT CLI.

## Required evidence and delivery

Prove source dependency direction and absence of database-target inputs/ports
from shared contracts, allowing the explicitly documented legacy durable reason.
Use focused tests for resolution, dispatch/retry/uncertainty and recovery, plus
real disposable PostgreSQL checks for historical reason reads, restart and
unchanged persistence behavior. Verify existing protobuf number and JSON output
with distinguishing assertions. Arrange, act and assert persisted behavior with
independent DbContext instances.

Run applicable solution, integration, Native AOT, formatting and diff gates.
Full manual operator rehearsal need not be repeated for this behavior-preserving
change alone. Source moves do not require artificial failing tests; any actual
regression fix still requires a focused reproducer.

Deliver #90 as one scoped draft PR. Then use #91 to agree operation dependencies,
aggregate generation completion and the generic reason/dispatch model before
adding database + role + grant execution. Neither issue authorizes the full M3
engine without further decisions.
