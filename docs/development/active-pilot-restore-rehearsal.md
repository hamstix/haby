# Single-instance active-pilot catalog restore rehearsal

This extends the [shadow rehearsal](shadow-pilot-rehearsal.md) through durable
execution and manual recovery (ADR-006/023/024/025/026). Use only disposable
PostgreSQL and isolated test targets. Keep the production Host's TLS and IPv4
loopback boundary. The alpha has no operator authentication; its recorded
technical actor identifies the allowed Host, not an authenticated human.
This checkpoint does not complete the usable application-configuration pilot:
role/credential generation, configuration delivery and broader orchestration
remain separate work.

## Automated reproduction

From the repository root with the .NET 10 SDK selected by `global.json`, run
the existing supported runtime. Do not supply a personal catalog or Provider.

Windows 11 with WSLc, in PowerShell:

```powershell
.\scripts\test-postgresql-wslc.ps1
```

Linux with a working Docker-compatible runtime:

```sh
dotnet test ./tests/Haby.PostgreSql.IntegrationTests/Haby.PostgreSql.IntegrationTests.csproj --artifacts-path ./.artifacts
```

For just this checkpoint on Linux:

```sh
dotnet test ./tests/Haby.PostgreSql.IntegrationTests/Haby.PostgreSql.IntegrationTests.csproj --artifacts-path ./.artifacts --filter FullyQualifiedName~Pg_dump_restore_preserves_active_pilot_history
```

`OperatorHostIntegrationTests.Pg_dump_restore_preserves_active_pilot_history_without_provider_access_or_replay`
uses the real PostgreSQL image's `pg_dump` and `pg_restore`, not an EF copy.
The existing Linux `postgresql-integration` CI job runs it, alongside the native
CLI rehearsals. The WSLc helper runs the same assertions through its explicitly
opted-in, uniquely named container published only to loopback. See the
[runtime guide](postgresql-integration-tests.md) for prerequisites and the
separate native CLI checks. Runtime failures are environment blockers, not
evidence of a Provider regression.

The fixture prepares immutable Provider configuration and an exact
SecretVersion through application APIs. Setup uses the existing deterministic
alias preflight double; real deployment must use real preflight. Production
Host RPCs import five Applications with historical unused-Resource warnings,
create ChangeSets/Plans, and commit generation 1. The histories are:

| History | Preparation and preserved result |
| --- | --- |
| Pending | Commit without execute; no attempt, no AppliedGeneration, target absent. |
| Dispatching | Journal APIs simulate death after the durable marker before SQL; one unfinished attempt, no AppliedGeneration. Ordinary reads preserve Dispatching. |
| NeedsAttention | Explicit execute recovers a dispatch marker without SQL; one uncertain attempt and bounded `UncertainAfterDispatch` reason, no AppliedGeneration. |
| Confirmed success | Real CREATE and independent existence confirmation; original successful attempt and AppliedGeneration 1. |
| Manually assigned success | Real CREATE followed by deterministic lost confirmation; explicit recovery and affirmative assignment RPC record one immutable receipt and AppliedGeneration 1, retaining the uncertain attempt. |

Source and restored Hosts never overlap. After disposing the source Host, the
test disables its Provider login and takes the dump. It restores into another
empty database as the actual restored catalog role, verifies schema privileges,
object ownership and transactional DDL/DML, then runs the normal migration
initializer before starting a fresh production-composed TLS Host. A test-only
Provider boundary counts and rejects both reads and dispatches, including calls
whose failures production code catches. Its count must remain zero during
startup, repeated reads and an assignment retry.

Repeated generated-client reads compare full revision/warning, ChangeSet,
Plan, commit, generation, operation/attempt and recovery responses, including
receipt identity, actor, time and reason. Independent fresh DbContexts compare
canonical row-content digests for every public-schema table, including migration
history, applied generations, pinned inputs, SecretRef associations and audit
rows. These digests test complete content preservation without rendering that
content in failure output. Internal application APIs separately resolve the
exact restored secret and verify the original protected note and acknowledgment;
no reveal RPC is added. The assignment retry carries the original exact identity
with a different valid explanation: it must return the original receipt, perform
zero target checks and leave every catalog table unchanged. External target
existence is checked independently after the restored Host stops.

## Operator isolation and inspection procedure

1. Prepare the disposable installation using
   [local operator setup](alpha-operator-local.md), including the explicit
   Plaintext SecretStore choice and its access restrictions. Record safe exact
   revision, ChangeSet, Plan, deployment/generation, Resource/action, Provider
   instance/configuration and receipt identities. Keep confidential explanations
   in protected audit storage. Never edit catalog rows to manufacture history.
   Crash windows are test-only hooks, not operator commands.
2. Drain and stop the sole source Host and all other catalog writers before
   snapshotting. Confirm it has exited. Keep source startup disabled throughout
   the restore drill. Never let two catalogs control the same Provider targets.
3. Follow the shadow guide's `pg_dump --format=custom` and
   `pg_restore --exit-on-error --no-owner --no-privileges` procedure into a
   **distinct empty** database. Protect the dump before creation as described
   below. Do not restore over the source or an existing catalog. If restoring as
   an administrator, regrant the actual catalog principal's schema, object
   ownership/DDL and read/write rights: `--no-privileges` omits original grants.
   Verify those rights as that principal before startup migrations.
4. Keep Provider network access blocked during inspection. Start only a fresh
   restored Host against that catalog and require migration initialization to
   succeed before serving. Use the existing explicit read commands with a local
   CLI context selecting the restored registry:

   ```powershell
   & $cli manifest revision get $applicationRecordId 1 --config .\restored-config.yaml -o json
   & $cli changeset get $changeId --config .\restored-config.yaml -o json
   & $cli plan get $planId --config .\restored-config.yaml -o json
   & $cli changeset commit get $changeId --config .\restored-config.yaml -o json
   & $cli deployment generation get $deploymentId 1 --config .\restored-config.yaml -o json
   & $cli operation get $deploymentId 1 db --action create --config .\restored-config.yaml -o json
   & $cli operation recovery inspect $deploymentId 1 db --action create --config .\restored-config.yaml -o json
   ```

   Compare exact safe historical content, not only row counts. `plan get` also
   evaluates current validity; expiry does not erase its immutable snapshot.
   Pending remains Pending and Dispatching remains Dispatching. NeedsAttention
   remains uncertain without AppliedGeneration. Both kinds of Succeeded retain
   their original applied generation and attempt; manual success retains its
   original receipt. Ordinary reads and startup authorize no Provider contact,
   dispatch, recovery decision or reinterpretation of uncertainty.
5. For a previously assigned operation only, the existing
   [assignment retry](alpha-operator-local.md#explicit-recovery-of-one-uncertain-operation)
   with the original Environment/attempt and explicit acknowledgment must return
   `alreadyAssigned` and its original receipt, even while targets are blocked.
   Supply the valid bounded note through a protected local note file; never echo
   or attach it. Verify no decision or attempt was added. This idempotent history
   read grants no permission to perform another effect.
6. Stop the restored Host before ending isolation or restarting the source.
   Remove the owned dump and disposable restore resources. If verification
   fails, keep both controllers stopped while investigating safe evidence.
   Preserve the original test failure if cleanup also fails; report cleanup
   separately without raw utility output or confidential operands.

## Catalog history is not external recovery

Restoring Haby does not restore, delete or roll back external PostgreSQL
databases. An old snapshot can lose knowledge of effects that occurred later.
Never automatically execute its Pending work or replay attempts to make history
look current. Independently investigate the exact pinned Provider and external
state before explicitly authorizing any further action.

Under the existing ADR-025 boundary, explicit execution converts Dispatching to
NeedsAttention without SQL. Assignment of an uncertain existing database requires
affirmative provenance responsibility, the exact attempt/Environment, a protected
explanation and a fresh pinned-target existence check. Presence alone proves no
ownership; absence alone grants no safe retry. If provenance or the old command's
completion remains uncertain, leave NeedsAttention unresolved. This rehearsal
adds no recovery policy, automatic replay, scheduler, restore service or schema.

## Secret-bearing temporary storage

Plaintext SecretStore backups contain recoverable credentials, even when all
ordinary reads are safe. The automated archive is created inside a uniquely
named mode-0700 container directory, then explicitly set to mode 0600. It never
leaves the test-owned container. Finally removes only that dump and directory;
container teardown removes the disposable server, databases, roles and any
remaining temporary data. Utility failures report only the executable and exit
code because raw PostgreSQL diagnostics can contain copied row data.

For manual drills, pre-create private storage before `pg_dump`: on Linux use
`umask 077` and an operator-only directory; on Windows use a dedicated directory
whose ACL grants only the operator and necessary system administrators access.
Do not rely on changing file permissions after a public file has been written.
Remove archives promptly; deletion is not a claim of forensic secure erasure.
Never commit or upload dumps, credentials, PFX files, protected notes, catalog
snapshots or secret-bearing logs as artifacts. Retain only safe identities,
pass/fail evidence and bounded reasons. Encrypted storage would additionally
require independently retained external key material; this test proves only the
currently implemented Plaintext mode.
