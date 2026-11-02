# ADR-032: Dependent operation plans and exact dispatch

- Status: Accepted
- Date: 2026-11-02
- Scope: Issue #91 and the usable alpha pilot

[ADR-033](0033-development-compatibility-and-catalog-resets.md) supersedes
development-era cross-build preservation requirements. New slices may require
a fresh catalog and coordinated API/schema changes; retain current-build runtime
safety, not compatibility layers for discarded alpha models.

## Context

The single-database rehearsal and ADR-031 refactor provide a working durable
execution path. Planning still admits one PostgreSQL database Resource, and
success of that one operation advances AppliedGeneration. Adding application
credentials, a role and grants requires explicit dependencies and aggregate
completion first. Prioritize a usable installation through small sequential
slices; this decision does not claim production readiness.

## Decision

### 1. Single-instance sequential execution

Keep one Host instance, explicit execution and sequential operation processing.
Do not add parallel workers, distributed scheduling, HA or automatic background
execution for this pilot. The model supports multiple operations per generation.

### 2. Resource and operation identity

A Resource describes desired state; an operation describes one step needed to
reach it. A Resource may require more than one operation. Identify new graph
operations by ApplicationDeployment, generation and deterministic OperationId.
Retain the owning Resource and exact versioned action contract separately.
Retry uses the existing operation and retains all attempts.

Use the canonical OperationId model for new graph work. Under ADR-033, the graph
implementation need not preserve ResourceId + Create addressing, old rows or
earlier operation/recovery response shapes across development builds. It must
preserve current-build operation identity and history through restart and retry.

### 3. Immutable planned graph

Plan contains operation IDs, exact versioned action contracts, Resource
ownership, pinned Provider/configuration/implementation references, required
non-secret inputs and explicit dependency IDs. The graph is part of the
immutable calculation and accepted generation. Execution never rebuilds it
from current aliases or current Provider settings.

Passwords and secret-bearing connection strings are prohibited in the graph.
Future generated material must be stored independently through SecretStore.
Its generation, ownership and output-reference rules require a separate ADR.

### 4. Explicit dependencies and deterministic order

Planning handlers produce dependencies from supported contracts. Do not infer
them from names, JSON member order or execution-time observations. Reject
duplicate OperationIds, missing dependencies and cycles before accepting a Plan.
Among ready independent operations, use ordinal OperationId ordering.
Role/grant dependencies will be specified with those contracts, not guessed here.

### 5. Consistent admission across Plan, Check and Commit

Use one set of registered admission rules across planning, checking an existing
Plan and atomic commit. Common code coordinates resolution, exact references
and graph validation; adapters own action-specific validation and planning.
Do not extend common code with Provider-specific conditional branches or remove
existing guards before replacement checks exist.

### 6. Exact build-time registration

Resolve planning support by the exact Resource contract, ProviderKind,
ConfigurationContract and ImplementationId. Execution additionally resolves the
exact versioned operation contract. Register handlers explicitly at composition.
Ambiguous current registrations fail startup. Missing support for saved work
does not prevent startup or metadata/history reads; the affected operation
returns a typed availability/compatibility refusal before dispatch.
Never substitute an implementation, contract version or configuration revision.

### 7. Typed adapter inputs and versioned storage

Keep concrete action inputs and their behavior in the adapter. Where Plan must
persist adapter-specific inputs, use a versioned payload format with an explicitly
registered typed codec and source-generated serialization metadata. The common
envelope stores the format identity and content without interpreting its fields.
Do not use object targets, CLR type names or reflection discovery as contracts.
Current metadata readability does not imply payload decoding or execution support.

### 8. Atomic desired-state acceptance

Commit atomically persists the generation, fixed planning inputs and complete
operation/dependency set. Verify preconditions and Plan freshness in that commit;
do not accept a partial graph or perform Provider network checks inside its
transaction. Use EF Core/C# and declarative constraints under ADR-026.

### 9. Readiness, stop and resume

An operation is ready only after every dependency has Succeeded. All graph
operations are mandatory in this pilot. A failure, uncertain result or
cancellation stops the current pass, including other independent work.
A later explicit execution continues only work allowed by its durable state
and action policy. Successful operations are never dispatched again.

### 10. Per-operation dispatch barrier

Preserve ADR-025's durable attempts and Preparing -> confirmed durable
Dispatching -> external call ordering for each external effect. After restart,
a Dispatching operation cannot be automatically redispatched. Common machinery
records uncertainty; action-specific contracts define evidence and recovery.
PostgreSQL database assignment remains specialized and explicitly consented.

### 11. Aggregate completion

Advance AppliedGeneration only when every required operation in that generation
has Succeeded. Recording the final operation success and advancing the eligible
generation must be atomic. A recovery transition uses the same aggregate rule.
Earlier external successes remain visible after later failure; no automatic
external rollback is introduced. Do not admit multiple effects while the old
one-operation-success rule remains active.

### 12. Operator progress

Expose completed/total operations, current or problematic work, remaining
dependencies, stop reason, DesiredGeneration and AppliedGeneration. Aggregate
status and dependency-blocked status may be computed from the durable graph;
an extra mutable aggregate status is not required. A blocked operation may
remain Pending while its blocked condition is computed.

### 13. Provider-neutral reasons and historical compatibility

Reason codes are bounded and value-free. They describe the failure category,
not retry authorization. Durable state, whether dispatch occurred, and the
action's policy still determine retry and recovery.

Use Provider-neutral categories:

| Reason | Meaning |
| --- | --- |
| InputInvalid | Resolved operation inputs fail the action's execution contract. |
| InputUnavailable | Required pinned input cannot be read, decoded or resolved exactly. |
| HandlerUnavailable | The exact required handler or its supported contract is unavailable. |

Keep the meanings of SecretUnavailable, Connectivity, Conflict, ServerRejected
and UncertainAfterDispatch in the current operation model.
Connectivity is a pre-dispatch availability category; uncertainty after the
dispatch barrier must not be downgraded into a safe connectivity retry.

ADR-033 removes the requirement to retain development-era stored strings, wire
numbers or JSON spellings across breaking revisions. Remove obsolete specialized
reasons and compatibility-only readers/tests when replacing the operation model.
Keep current-code mappings explicit. Unknown current stored codes must fail
safely rather than become success or an invented default reason.

Map invalid action inputs to InputInvalid and unavailable pinned inputs to
InputUnavailable. Preserve definitive/transient classification and state safety. Use
HandlerUnavailable only where absence of exact handler support is actually
known; do not guess it from an arbitrary resolver exception. Exact dispatch
registration will provide that distinction in the next slice.

Within a current installation, retries retain attempt history and obey the same
dispatch barrier. Breaking development changes may require a fresh catalog under
ADR-033 instead of migrating old reasons or maintaining downgrade compatibility.

### 14. Ordered implementation

1. Common reasons were delivered under issue #100. Its development-era
   compatibility additions do not become permanent obligations under ADR-033.
2. Replace checkpoint-specific admission with common coordination and exact
   typed registrations, still executing only the database action.
3. Persist the fixed graph, atomically accept it, execute dependencies
   sequentially and apply aggregate completion, including recovery. Prove the
   selected boundaries with a genuinely different typed test handler.
4. Add PostgreSQL credentials, role and grants after separate approval of their
   ownership, privilege, secret persistence and uncertainty/recovery contracts.
5. Render configuration and provide scoped API retrieval after approval of the
   confidential delivery boundary.

Keep each slice reviewable and preserve the working database path. The second
test handler proves a boundary, not production support for a second Provider.
The graph slice may be split further if needed, but must not enable multiple
effects before dependency and aggregate-completion safeguards are complete.

## Boundaries and evidence

This ADR does not remove current deployment-update/generation admission limits,
define role privileges or generated-secret lifecycle, enable configuration
delivery, add a runtime plugin framework, or authorize the full M3 engine.
Separate accepted contracts remain prerequisites for those changes.

Preserve API-only Native AOT CLI, source-generated JSON, pinned inputs,
cancellation, redaction and historical recovery behavior. Test exact registration
and missing/ambiguous support, graph determinism and invalid graphs, atomic
acceptance, stop/resume, restart after each barrier, partial success and recovery
without premature AppliedGeneration. Use separate DbContexts for persisted
arrange/act/assert and disposable PostgreSQL for relational behavior.

The first implementation slice needs focused compatibility, restart, migration,
wire and CLI tests; it does not need speculative graph tests before that graph
exists. Run applicable solution, PostgreSQL, Native AOT, format and diff gates.
