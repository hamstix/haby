# PR #143 F1: server-observed configuration lock waits

The review found an evidence gap: a pre-command interceptor notification did not
prove that PostgreSQL had blocked the contender while commit/capture was paused.
The fix changes tests only. Production locking, persisted formats and catalog
compatibility are unchanged.

`Scope_writes_and_commit_have_a_synchronized_atomic_order_including_absent_insertion`
now records the paused owner's and contender's backend PIDs. Before releasing
the owner, a third connection must observe an ungranted advisory lock in
`pg_locks`, the matching granted lock owned by the paused backend, and that
owner in `pg_blocking_pids(contender)`. Both existing and absent assignments run
in both transaction orders. The multi-scope capture test uses the same check
while paused between Environment and Application reads.

The pre-command signal supplies the contender PID only. Successful synchronization
requires the database observation. If the contender completes first, the test
fails explicitly. No elapsed delay or immediate task-state assertion establishes
blocking. The existing bounded cancellation remains, and `finally` releases the
owner and drains both operations even when the observation fails.

## Executed test-value evidence

Base: `39116ad5754f1ff17f5e231196e5d19427aea7df` (the reviewed PR head).
Test patch SHA-256:
`f4731f4f56c646cbb4637e850c87f44d4e5940cd91507307e24affb1acc4c8a3`.
Counterfactual production patch SHA-256:
`95ba37de5bece0ee1f8569ae8c04855cdffe24e4381d92d7ca623059e1827cd0`.

The buildable counterfactual was confined to a separate owned checkout. It adds
a transaction savepoint immediately before scope capture/freshness comparison
in the desired-state committer, then rolls back to that savepoint after the
comparisons and graph validation, before generation persistence. PostgreSQL
releases scope locks acquired after the savepoint, creating the precise
compare/commit gap without changing the competing writer command, test assertions
or existing outer transaction. No counterfactual is included in production code.

| Variant | Executed / passed / failed / skipped | Result |
| --- | --- | --- |
| Correct production code + strengthened tests | 5 / 5 / 0 / 0 | Actual server waits observed before release. Exit 0. |
| Savepoint counterfactual + identical strengthened tests | 5 / 3 / 2 / 0 | Executed: detected for `commitFirst=true`, with both `existing=false` and `existing=true`. Exit 1. |
| Correct production code restored and rebuilt | 5 / 5 / 0 / 0 | Same selection passes again. Exit 0. |

Both red cases fail with:
`The competing operation completed before PostgreSQL reported it blocked by the paused owner.`
These are assertion failures, not compilation, discovery, timeout or database
setup failures. The writer-first and multi-scope cases remain green in this
specific counterfactual; it targets the post-comparison commit boundary.

Each run rebuilt the integration project, using separate checkout outputs and
a fresh disposable WSLc PostgreSQL 17.6-alpine instance. The selection was:

```powershell
dotnet test tests/Haby.PostgreSql.IntegrationTests --filter 'FullyQualifiedName~Scope_writes_and_commit_have_a_synchronized_atomic_order_including_absent_insertion|FullyQualifiedName~Multi_scope_capture_blocks_a_writer_between_scope_reads_and_saves_one_coherent_view'
```

Local experiment artifacts are ignored: `.artifacts/f1-tests.patch`,
`.artifacts/f1-savepoint-fault.patch`, `.artifacts/race-f1-green/`, and the
counterfactual checkout's `.artifacts/race-f1-savepoint-red/` and
`.artifacts/race-f1-restored-green/`. Raw test logs are not public CI artifacts.
The PR description records final-head complete CI separately from this focused
paired experiment. This evidence does not claim another production atomicity
defect or a complete Windows PostgreSQL gate result.
