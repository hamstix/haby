# Five-step operator rehearsal

Issue #120 activates the accepted ADR-034 path in the production Operator Host.
The database admission explicitly composes standalone and pair shapes under one
exact key; the login has one separate admission. Real Create, restrict, local
generated material, login and access handlers are registered, together with the
three external output contracts, generated-slot services and generic reservations.
There is no fixture prefix, implementation fallback or automatic execution.

The common dispatch registry requires a mandatory external-output producer's
contract before starting any Pending graph node. Duplicate admission, executor
or external output registrations refuse construction. Missing support leaves
saved state readable and returns HandlerUnavailable before an attempt or effect.
Exact action/input checks, dependency blocking and atomic aggregate completion
remain enabled. No schema/model/migration or additional reset is introduced;
the current catalog baseline remains #116 under ADR-033.

## Preconditions and trust boundary

This is a single-instance, generation-1, isolated alpha rehearsal. Use a newly
created **disposable PostgreSQL 17 cluster and empty disposable catalog**, never
a developer's persistent database/role/catalog. Record the unique container and
catalog names and who owns cleanup before starting. Use a configured SUPERUSER
Provider; restricted administration is deferred. Keep untrusted clients away
throughout bootstrap: CREATE DATABASE precedes PUBLIC restriction and has an
initial PUBLIC-access window. Do not expose this unauthenticated API remotely.

Follow [local Host/TLS setup](alpha-operator-local.md#native-aot-publish-and-loopback-smoke-gate) using
secret-manager supplied process environment for the catalog connection and PFX
password. Start the ordinary `Haby.Operator.Host` with its production composition,
one loopback HTTP/2 listener and TLS. Do not put either secret in process
arguments, files committed to Git, shell history, transcripts or screenshots.
Trust the local certificate; disabling validation is only an explicit local
debug choice, never a remote-exposure procedure. Stop any prior Host first.

The Host uses Plaintext SecretStore: catalog and backup readers can recover
administrative and application passwords. Protect those files and accept this
exposure explicitly. No reveal endpoint is provided. The SCRAM-verifier transport
requires the session logging controls and restrictions documented in
[the login logging boundary](116-postgresql-application-login.md#supported-logging-boundary-and-limitations):
PostgreSQL 17, no preload audit libraries, only supported extensions, and permitted
connection-local statement/error/statistics suppression. Verifiers remain
confidential. Do not disable cluster auditing silently; an incompatible audit
policy means stop. Privileged server readers and external tracing are not hidden.

## Publish, select context and bootstrap

Run the blocks below from the repository root in PowerShell 7. Check each result
before moving on. The helper checks exits and parses one JSON object; it does not
retry, choose a Plan or execute operations automatically.

```powershell
dotnet publish ./src/habyctl/habyctl.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishAot=true -p:TreatWarningsAsErrors=true -o ./.artifacts/aot/win-x64
if ($LASTEXITCODE -ne 0) { throw 'Native publication failed.' }
$cli = (Resolve-Path ./.artifacts/aot/win-x64/habyctl.exe).Path
# Linux: use -r linux-x64, its output directory and executable habyctl.
$runId = [Guid]::NewGuid().ToString('N')
$environmentName = 'pilot-' + $runId
$databaseName = 'haby_pilot_' + $runId
$work = Join-Path ([IO.Path]::GetTempPath()) ('haby-operator-' + $runId)
$null = New-Item -ItemType Directory -Path $work
$endpoint = Read-Host 'Trusted TLS loopback registry URL'
$config = Join-Path $work 'config.yaml'
@"
formatVersion: 1
currentContext: rehearsal
instances:
  local:
    endpoint: $endpoint
contexts:
  rehearsal:
    instance: local
    environmentId: $environmentName
"@ | Set-Content -LiteralPath $config -Encoding utf8NoBOM
function Invoke-Registry {
    param([string[]]$Arguments)
    $result = & $cli @Arguments --config $config -o json
    if ($LASTEXITCODE -ne 0) { throw 'Registry command did not succeed; inspect its safe result and stop.' }
    $result | ConvertFrom-Json
}
& $cli config current-context --config $config
if ($LASTEXITCODE -ne 0) { throw 'Context selection failed.' }
$environment = Invoke-Registry @('environment', 'create', $environmentName)
# Hidden terminal input, no password argument or shell variable. Save the returned
# RequestId for exact-byte retry if transport fails. Do not capture input/transcripts.
& $cli secret create pg-admin --environment $environmentName --config $config -o json
if ($LASTEXITCODE -ne 0) { throw 'Secret creation requires inspection before proceeding.' }
```

For noninteractive use, the existing `secret create --stdin --request-id UUID`
reads exact bytes to EOF. Feed it from a secret manager through stdin, with no
newline/BOM or literal password in the command/history. Do not use `echo PASSWORD`.
Bootstrap administrative material is distinct from the later application slot.

Create the reference-only Provider declaration; these fields contain no password:

```powershell
$providerFile = Join-Path $work 'provider.json'
$provider = @{
    apiVersion = 'haby.dev/v1alpha1'; kind = 'Provider'
    metadata = @{ name = 'pg-main'; environment = $environmentName }
    spec = @{
        revision = 1; providerKind = 'postgresql/v1'
        configurationContract = 'haby.postgresql.connection/v1'
        implementationId = 'haby.postgresql.database/v1alpha1'
        properties = @{
            host = (Read-Host 'Disposable PostgreSQL loopback host')
            port = [int](Read-Host 'Disposable PostgreSQL published port')
            maintenanceDatabase = 'postgres'
            administratorUserName = (Read-Host 'Disposable Provider SUPERUSER name')
        }
        secretRefs = @{ 'administrator-password' = @{ secretName = 'pg-admin'; version = 1 } }
    }
}
$provider | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $providerFile -Encoding utf8NoBOM
$providerResult = Invoke-Registry @('provider', 'apply', '--file', $providerFile)
$check = Invoke-Registry @('provider', 'check', 'pg-main', '--revision', '1')
$alias = Invoke-Registry @('provider', 'alias', 'set', 'postgresql.primary', '--provider', 'pg-main', '--revision', '1', '--expect-absent')
```

## Import, review and accept without effects

Wrap the shared-login example in the actual import envelope. Both api/worker
bindings select `app-login`; they do not generate separate credentials.

```powershell
$manifest = Get-Content ./docs/development/examples/106-shared-application-login.json -Raw | ConvertFrom-Json
$manifest.metadata.id = 'pilot-' + $runId
$manifest.spec.resources.db.properties.name = $databaseName
$applicationFile = Join-Path $work 'application.json'
@{ applicationVersion = 'v1'; manifest = $manifest } | ConvertTo-Json -Depth 30 |
    Set-Content -LiteralPath $applicationFile -Encoding utf8NoBOM
$valid = Invoke-Registry @('manifest', 'validate', $applicationFile)
$imported = Invoke-Registry @('manifest', 'import', $applicationFile)
$reference = $imported.data.revision.reference
$change = Invoke-Registry @('changeset', 'create', $reference.applicationRecordId, [string]$reference.revisionNumber, '--expected-absence')
$changeId = $change.data.changeSet.changeSetId
$planned = Invoke-Registry @('plan', 'create', $changeId)
$planId = $planned.data.plan.planId
& $cli plan get $planId --config $config
if ($LASTEXITCODE -ne 0) { throw 'Plan inspection failed.' }
$planRead = Invoke-Registry @('plan', 'get', $planId)
$current = Invoke-Registry @('plan', 'check', $planId)
# Review all five operation IDs, Resource IDs, exact actions, dependencies,
# Provider instance/revision and fingerprint before explicit acceptance.
$accepted = Invoke-Registry @('changeset', 'commit', $changeId, '--plan', $planId)
$deploymentId = $accepted.data.commit.deploymentId
$generation = [string]$accepted.data.commit.generation
$desired = Invoke-Registry @('deployment', 'generation', 'get', $deploymentId, $generation)
```

Import/Plan/Check/Commit/startup/read dispatch nothing. Independently inspect the
disposable target catalogs and the Haby catalog before execution: target database
and owner-derived role absent; no application generated slot, creation audit,
reservation, operation attempt or output; AppliedGeneration absent. Administrative
bootstrap objects already exist. Do not infer absence from the Plan. The automated
rehearsal performs these independent checks without revealing material.

The generic graph is the complete review surface. The older **single-action
detail** reports `unsupportedShape` for a pair; this is not a rejected Plan or
a standalone-only description of the full workflow. A valid Plan/Check still
reports planned/current, and text/JSON show all five nodes. No payload or password
is included in these summaries.

## Five explicit executions

Select the IDs returned by this Plan, never guessed IDs. These are action-contract
selectors for this rehearsal only; the CLI has no Provider-specific workflow verb.

```powershell
$nodes = $planned.data.plan.operations
function Operation-Id([string]$action) {
    $selectedNodes = @($nodes | Where-Object actionContract -eq $action)
    if ($selectedNodes.Count -ne 1) { throw 'Expected one reviewed action.' }
    $selectedNodes[0].operationId
}
$create = Operation-Id 'postgresql.database.create/v1alpha1'
$restrict = Operation-Id 'postgresql.database.restrict/v1alpha1'
$material = Operation-Id 'haby.secret.ensure-generated/v1'
$login = Operation-Id 'postgresql.application-login.create/v1alpha1'
$access = Operation-Id 'postgresql.application-login.configure-access/v1alpha1'
# Deliberately request a downstream node first: expected exit 1, outcome blocked,
# state Pending and no attempt or effect. Inspect before continuing.
& $cli operation execute $deploymentId $generation $access --config $config -o json
if ($LASTEXITCODE -ne 1) { throw 'Expected a blocked downstream operation.' }
$blocked = Invoke-Registry @('operation', 'get', $deploymentId, $generation, $access)
if ($blocked.data.attempts.Count -ne 0) { throw 'Blocked operation has an unexpected attempt.' }
$created = Invoke-Registry @('operation', 'execute', $deploymentId, $generation, $create)
$restricted = Invoke-Registry @('operation', 'execute', $deploymentId, $generation, $restrict)
$saved = Invoke-Registry @('operation', 'execute', $deploymentId, $generation, $material)
$loginCreated = Invoke-Registry @('operation', 'execute', $deploymentId, $generation, $login)
$configured = Invoke-Registry @('operation', 'execute', $deploymentId, $generation, $access)
$final = Invoke-Registry @('operation', 'get', $deploymentId, $generation, $access)
```

Each must report succeeded. AppliedGeneration remains absent until the fifth
mandatory success, output and attempt closure commit atomically. Internally the
four outputs are database evidence, exact generated SecretRef, role evidence and
final connection with that same SecretRef. Final connection content stays internal;
normal operation/Plan/get/list/logs never deliver it or material.

Independently record database/schema owner, pg_database_owner public-schema owner,
revoked PUBLIC database/public/application-schema privileges, LOGIN with all five
negative cluster flags and no memberships, database-local search_path and creator
defaults. The automated trusted test internally resolves the exact owner-scoped
SecretRef and runs both real EF migrations and DML as that application login.
There is no operator reveal command for reproducing that trusted internal check.

Repeat an exact successful operation; then stop the Host and start the same build
over the same catalog with the same ordinary configuration. Repeat reads and
execution after restart:

```powershell
$repeat = Invoke-Registry @('operation', 'execute', $deploymentId, $generation, $access)
# Operator: stop/restart the same single Host now, preserving its catalog.
$afterRestart = Invoke-Registry @('operation', 'get', $deploymentId, $generation, $access)
$repeatAfterRestart = Invoke-Registry @('operation', 'execute', $deploymentId, $generation, $access)
```

Compare all five states/attempt histories, exact Provider references, slot/ref,
outputs, audit/reservation counts and external identities. Expected: one slot,
one creation audit, one role reservation, five successful attempts (four external
dispatches), four outputs and generation 1 applied. Repeats add nothing. Alias
movement does not redirect accepted inputs. Catalog pg_dump/pg_restore retains
this state but never recreates external databases/roles; reuse the
[current-build restore evidence](118-postgresql-owner-access.md#failure-restart-and-restore).

## Stop conditions, evidence and cleanup

Exit codes stay 0 success, 1 expected rejection (including blocked/needsAttention/
dispatchUncertain), 2 usage/configuration or handlerUnavailable, 3 transport/cancellation. JSON stdout is
one object; diagnostics/prompts use stderr. A timeout is not proof of rollback.
Inspect the exact operation, retain earlier durable successes and **stop** on
Failed/NeedsAttention/DispatchUncertain or unavailable pinned input. Do not reset
passwords, repair ownership, delete catalog rows or replay uncertain work.
There is no recovery for restrict/login/access. Existing database-create manual
assignment is standalone-only, retains the Uncertain attempt and publishes no
database output; it cannot unblock the pair. Inspection and assignment validate the immutable
accepted singleton graph; the decision transaction revalidates that boundary,
including for trusted direct callers. Any further resolution needs a
separately accepted action-specific decision.

Record build SHA, CLI RID, unique disposable identities, safe Plan/commit/operation
JSON and text, statuses/exits, attempt and audit counts, independent ownership/ACL
checks, restart comparison and cleanup result. Never capture password/verifier,
connection string, raw Provider payload or driver exception. Do not dump secret
tables or retain a Plaintext backup as a public test artifact.

Automated disposable rehearsal, including real production composition and native
CLI, runs with:

```powershell
$env:HABY_AOT_CLI_REQUIRED = '1'
$env:HABY_AOT_CLI_PATH = $cli
try { ./scripts/test-postgresql-wslc.ps1 }
finally {
    Remove-Item Env:HABY_AOT_CLI_REQUIRED -ErrorAction SilentlyContinue
    Remove-Item Env:HABY_AOT_CLI_PATH -ErrorAction SilentlyContinue
}
```

The helper creates/stops only its own disposable container. For the separate
operator-owned manual run, stop its Host, verify the recorded exact disposable
container/catalog identity, and explicitly destroy only that run's disposable
cluster and temporary files. Never prune, silently drop an existing database/role,
or treat deleting the catalog as external rollback. The maintainer-environment
manual rehearsal remains a follow-up with its operator. This slice approves no
HA, stable public API, production rollout, new recovery, rotation, confidential
configuration rendering/delivery or remote API exposure.
