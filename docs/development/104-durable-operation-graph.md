# Issue 104: persisted dependent operation graph

Base: origin/main 3c74916 (includes merged PR #103). ADR-032 runtime rules and
ADR-033 development policy apply. Production retains one PostgreSQL database
Create; composition of multiple effects is confined to typed test handlers.

## Current model

A typed OperationId identifies a step within deployment + generation, independently
of its owning Resource and exact action contract. A Plan contains a canonical,
versioned immutable graph: nodes sorted by ordinal ID, sorted explicit dependencies,
exact Resource/Provider/configuration/implementation keys, and adapter-encoded,
versioned non-secret input. Explicit typed codecs use generated JSON metadata.
Graph validation rejects duplicate IDs/dependencies, missing dependencies, cycles,
inconsistent references and unsupported formats. Fingerprints include the canonical
graph and exact planning references. Check verifies freshness/support without
replacing the graph; Commit copies the accepted graph, never replans it.

## Persistence and ownership

The planner owns its Plan transaction; the committer owns acceptance, retaining
ChangeSet/Plan/alias locks through generation, graph and initial journal inserts.
No Provider calls or secret-material reads occur there. Persist canonical graph
JSON on Plan and generation, and canonical OperationId on operations, attempts and
recovery decisions. Journal transitions retain compare-and-update guards.

A singleton generation gate serializes runner, direct operation execution and
manual assignment. The runner holds that gate once and calls an internal unlocked
executor; adapters never reacquire it. Read-only progress uses independent snapshots
and does not hold an execution gate or require handlers. Direct calls enforce the
same dependency condition. The runner selects ready nodes ordinally and stops the
entire pass on the first refusal, failure, uncertainty or cancellation. Explicit
resume skips successes. Dispatching is recovered before selecting a handler.

Final success and recovery both call one aggregate completion helper in their
existing transaction, locking the deployment row before completion decisions.
Partial success remains durable with AppliedGeneration unset. Final-node success,
attempt closure and eligible AppliedGeneration advance commit together or roll back.
Recovery records resolved OperationId, target generation and actual applied state
at decision time. Receipt replay reads that immutable decision and performs no
external check or later aggregate advancement.

## Coordinated changes and evidence

Host and API-only Native AOT CLI address OperationId directly, with updated generated
contracts/presenters. There is no ResourceId + Create facade, historical action
mapping, old-input reader or legacy reason bridge. This breaking slice requires a
fresh catalog. Startup must reject a pre-graph catalog without resetting it. Catalog
reset does not remove or establish ownership of any external database or role.

Validate pure graph invariants, typed counting effects over actual persisted
Plan/Check/Commit, deterministic stop/resume, direct/concurrent calls, restart,
barriers, partial/final success, transactional rollback, specialized recovery with
siblings and immutable receipts. Use separate arrange/act/assert DbContexts and
real disposable PostgreSQL, including pg_dump/pg_restore of partial state. Run
focused, full solution, PostgreSQL, formatting, diff and published Native AOT gates.

## Review corrections

The current OperationRequest uses field 5 for OperationId and reserves the removed
identity slots 3/4 and names. Requests without an explicit current OperationId are
rejected before execution/recovery. This prevents cross-interpretation of identity
bytes; it provides no old operation facade, payload reader or cross-build support
promise. ADR-033 and the fresh-catalog requirement still apply.

Unsupported/corrupt stored Plan snapshot or graph formats yield PlanInputUnavailable
through Check and Host Get/Check, with no Plan projection when decoding fails.
The Host does not decode an unreadable Plan again to build the reply. Readable
Plans whose pinned support is unavailable retain their safe metadata. These reads
never change stored state or contact Providers.
