# Local alpha operator workflow

The operator host and `habyctl` are an internal alpha interface for a single
Haby instance. The host listens only on IPv4 loopback with HTTP/2. TLS is
enabled by default and can be explicitly disabled for local debugging. It
has no application authentication or authorization and is unsuitable for broad
or remote exposure. Its protobuf contract may change before M4. It validates,
imports, reads immutable manifest revisions and previews one supported database
Resource against hypothetical request metadata. It also creates and reads durable
ChangeSets and Plans, checks exact planning inputs, closes Plans and explicitly
commits desired state with Pending work. Explicit operation execution can create that
one database through the existing durable executor. Explicit manual recovery can
assign an existing target after uncertainty. Secret generation and configuration
delivery remain unavailable through this API.

The CLI accepts `--help` at the root and at every command level without an
endpoint. Command results go to standard output; syntax, local-file and
transport errors go to standard error. Its alpha exit codes are `0` for a
successful result, `1` for an expected registry rejection, `2` for invalid
syntax, local input or endpoint configuration, and `3` for transport failure
or cancellation. The current gRPC contract cannot classify an unset or
unknown outcome as a typed registry rejection; the CLI reports it as a safe
local error. The CLI can select a local registry through a named context.
The recursive `--output` / `-o` option accepts exactly `text` (the unchanged
default) or `json`, before or after nested command tokens. An unsupported value
is a usage error before configuration loading or RPC. Help and version remain
human-readable and do not load configuration or connect, even when JSON output
is requested.

### Alpha JSON result envelope

Every result from `manifest validate`, `manifest import`, `manifest revision get`,
`manifest revision preview`, `changeset create|get`, `plan create|get|check|close`,
`changeset commit`, `changeset commit get`, `deployment generation get`,
`operation execute|get`, `operation recovery inspect|assign-existing`, `config view` and `config current-context` emits one
complete JSON object and one newline to stdout with these ordered fields:

```json
{"formatVersion":1,"command":"manifest validate","outcome":"valid","data":{"applicationId":"example.service","applicationVersion":"v1","diagnosticsTruncated":false},"diagnostics":[]}
```

`formatVersion` is `1`; `command` is the canonical space-separated command;
`outcome` is its lower-camel-case typed result; `data` is the command-specific
object or `null`; and `diagnostics` is always an array. Optional data fields are
omitted when absent, except `currentContext`, which is `null` when unset.
Diagnostic objects contain only `code`, `path`, `message`,
`severity`, `relatedPaths` and optional `location` (`line`, `column`). Diagnostic
parameters are never emitted. Truncation is reported through
`data.diagnosticsTruncated` or, for a stored revision,
`data.revision.historicalWarningsTruncated`. A revision has a typed `reference`
with `applicationRecordId` and `revisionNumber`, plus revision metadata and
`historicalWarnings`. Conflict data includes `applicationId`,
`applicationVersion`, `existingRevision`, `changedPaths` and
`changedPathsTruncated`. The `wouldCreate` preview data sets `hypothetical: true`
and `canApply: false`; it is never a provisioning result. Every preview rejection
also carries those markers.

An expected rejection still emits the envelope to stdout and exits `1`:

```json
{"formatVersion":1,"command":"manifest revision get","outcome":"notFound","data":{"requestedReference":{"applicationRecordId":"missing","revisionNumber":1}},"diagnostics":[]}
```

The offline `config view -o json` result maps the validated configuration into
sorted arrays of named instances and contexts, without source YAML or comments:

```json
{"formatVersion":1,"command":"config view","outcome":"ok","data":{"formatVersion":1,"currentContext":"local-a","instances":[{"name":"local-a","endpoint":"https://localhost:7443"}],"contexts":[{"name":"local-a","instance":"local-a","environmentId":"development-a"}]},"diagnostics":[]}
```

Syntax, local configuration, transport, cancellation and unknown-server-outcome
failures keep stdout empty and use stderr with the existing exit codes `2` or
`3`. Text output and exit codes are unchanged. The JSON envelope is an alpha
operator format; it is not a stable M4 public API.

## Read-only local contexts

The first CLI configuration format is YAML with `formatVersion: 1`. Save this
secret-free example as `config.yaml`:

```yaml
formatVersion: 1
currentContext: local-a
instances:
  local-a:
    endpoint: https://localhost:7443
  local-b:
    endpoint: https://127.0.0.1:8443
contexts:
  local-a:
    instance: local-a
    environmentId: development-a
  local-b:
    instance: local-b
    environmentId: development-b
```

The `environmentId` is the logical EnvironmentId from ADR-016: a registry-local,
unique lowercase kebab-case key of at most 64 ASCII characters, such as `pilot`.
It is not the persisted UUIDv7 EnvironmentRecordId or a display name. The server
resolves it to an existing persisted Environment for `changeset create`; it never
creates one or selects a default. Manifest RPCs do not receive this context field. The configuration contains no
credentials, tokens or private keys. Both endpoints must be HTTP or HTTPS
loopback addresses with no user information, path, query or fragment. HTTPS
certificate validation remains enabled unless the local debug setting below is
explicitly set. The alpha API is unauthenticated; do not forward it to a remote
or broadly reachable address.

The file path order is `--config`, then `HABYCTL_CONFIG`, then the per-user
default at `%APPDATA%\habyctl\config.yaml` on Windows or
`~/.config/habyctl/config.yaml` on Linux. A missing explicitly selected file
fails; a missing default file permits the legacy no-config behavior. The
context order is `--context`, then `currentContext`. A selected context uses its
named instance endpoint even if `HABY_OPERATOR_URL` is set. An unknown context
or instance fails without a fallback. Only with no selected context can
`HABY_OPERATOR_URL` provide the transitional endpoint. Invocation overrides do
not edit the file.

```powershell
dotnet run --project .\src\habyctl -- config view --config .\config.yaml
dotnet run --project .\src\habyctl -- config current-context --config .\config.yaml
dotnet run --project .\src\habyctl -- manifest revision get '<application-record-id>' 1 --config .\config.yaml --context local-b
```

The two `config` commands work offline. `config view` renders the validated
typed fields rather than the source YAML, so comments and formatting are not
reproduced. The reader accepts one UTF-8 mapping document of at most 64 KiB
and rejects unknown or duplicate fields, anchors, aliases, merge keys, custom
tags, malformed encoding and invalid references. This slice has no config
write commands. YamlDotNet 18.1.0 (MIT) provides the low-level YAML
representation model; Haby maps it explicitly into typed records, without
reflection-based object activation. The parser path was published and executed
as a Windows Native AOT spike before it was selected.

## Explicit Environment administration

With the loopback Host running and `config.yaml` from the example above,
create or rediscover an Environment through the registry API:

```powershell
dotnet run --project .\src\habyctl -- environment create pilot --config .\config.yaml --context local-a
dotnet run --project .\src\habyctl -- environment create pilot --config .\config.yaml --context local-a
dotnet run --project .\src\habyctl -- environment get pilot --config .\config.yaml --context local-a -o json
dotnet run --project .\src\habyctl -- environment list --page-size 1 --config .\config.yaml --context local-a -o json
```

The first create returns `created` with `name` and a separate `recordId`
(UUIDv7). The repeat returns `existing` with the same identity. Concurrent
creates, a retry after a lost response, and a retry after Host restart return
that same persisted identity. If `pilot` already exists, both creates return
`existing`. A get returns `found` with the same metadata, or `notFound` (exit
1) without creating anything. Invalid names return `invalidName` (exit 1):
use a leading lowercase letter followed by lowercase letters/digits and
single hyphen-separated segments, at most 64 ASCII characters. Names are
validated server-side without trimming or case conversion.

`environment create <name>` and `environment get <name>` always use the
explicit name. The selected local context chooses the registry endpoint;
its `environmentId` does not override the argument. Selecting a context,
viewing configuration, get and list never create an Environment. Creation
writes only Environment metadata: no secrets, Providers, deployments or
operations. Later planning resolves the logical name server-side to this
persisted identity; no direct CLI database access is involved.

List returns one page in ascending ordinal name order. The default page size
is 50; `--page-size` accepts 1..100 (wire value 0 selects the default).
If more results exist, text prints `nextCursor: env1:<last-name>` and JSON
includes `data.nextCursor`. Pass the returned token unchanged:

```powershell
# For example, if the previous page returned nextCursor "env1:pilot":
dotnet run --project .\src\habyctl -- environment list --page-size 1 --cursor 'env1:pilot' --config .\config.yaml --context local-a -o json
```

Follow each returned cursor until `nextCursor` is absent (or no continuation
line in text). An empty page is successful. Results are never silently
truncated: each page that has more rows includes its continuation. Page size
may change between calls. Cursors are versioned, bounded to 69 ASCII
characters and validated using the same name rules. Malformed cursors return
`invalidCursor`; out-of-range page sizes return `invalidPageSize` (exit 1).
A valid continuation name need not currently exist. Pagination is a live
traversal, not a snapshot: names created after the cursor can appear; names
created before it require restarting the list. Rename and delete are deferred.

The alpha RPCs are `CreateEnvironment`, `GetEnvironment` and
`ListEnvironments`. Their typed statuses distinguish success, invalid name,
not found, invalid cursor and invalid page size. Ordinary metadata contains
only logical names and persisted identities. JSON retains the existing
`formatVersion: 1` envelope and uses source-generated serialization metadata.

## Native AOT publish and loopback smoke gate

The CI gate publishes `src/habyctl` for `win-x64` on `windows-latest` and
`linux-x64` on `ubuntu-latest` with the SDK selected by `global.json`.
It treats publish warnings as errors, executes the resulting `habyctl` binary
with `--help` and `--version`, then runs only `CliLoopbackTests` with
`HABY_AOT_CLI_REQUIRED=1` and `HABY_AOT_CLI_PATH` pointing to that binary.
Those tests launch the executable as a subprocess for context selection,
text and JSON output, redaction, and gRPC calls to disposable loopback hosts.
The test fails if the required path is missing or does not name a file.
Neither the smoke gate nor these hosts need Docker, a persistent database,
credentials, or a non-loopback API.

Run the Windows gate from the repository root in PowerShell. Install the
.NET SDK selected by `global.json` and the Visual Studio C++ build tools
required by Native AOT first:

```powershell
dotnet publish .\src\habyctl\habyctl.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:TreatWarningsAsErrors=true -o .\.artifacts\aot\win-x64
$env:HABY_AOT_CLI_PATH = (Resolve-Path .\.artifacts\aot\win-x64\habyctl.exe).Path
$env:HABY_AOT_CLI_REQUIRED = '1'
& $env:HABY_AOT_CLI_PATH --help
& $env:HABY_AOT_CLI_PATH --version
dotnet test .\tests\Haby.Cli.Tests\Haby.Cli.Tests.csproj -c Release --filter FullyQualifiedName~CliLoopbackTests -p:TreatWarningsAsErrors=true
```

Run the Linux gate from the repository root with the SDK selected by
`global.json`, `clang`, and the zlib development package installed (on Ubuntu,
`sudo apt-get install clang zlib1g-dev`):

```bash
dotnet publish ./src/habyctl/habyctl.csproj -c Release -r linux-x64 --self-contained true -p:PublishAot=true -p:TreatWarningsAsErrors=true -o ./.artifacts/aot/linux-x64
export HABY_AOT_CLI_PATH="$(realpath ./.artifacts/aot/linux-x64/habyctl)"
export HABY_AOT_CLI_REQUIRED=1
"$HABY_AOT_CLI_PATH" --help
"$HABY_AOT_CLI_PATH" --version
dotnet test ./tests/Haby.Cli.Tests/Haby.Cli.Tests.csproj -c Release --filter FullyQualifiedName~CliLoopbackTests -p:TreatWarningsAsErrors=true
```

Use an isolated local PostgreSQL catalog database and a principal with the
migration rights described in the [catalog guide](../../src/Haby.Persistence.PostgreSql/README.md).
Only one host instance should run against it. Back up the catalog before a
reviewed migration using that guide's procedure. Startup awaits migrations;
failure stops the process before it listens.

From PowerShell in the repository root, create and trust a local development
certificate, then export its password-protected PFX. Keep the PFX and password
outside the repository:

```powershell
dotnet dev-certs https --trust
dotnet dev-certs https --export-path "$env:TEMP\haby-local.pfx" --password '<local-password>'
```

Use a certificate whose subject alternative name covers `localhost`. The CLI
uses the operating system's normal certificate validation by default. For
local debugging with an untrusted certificate, set
`HABY_OPERATOR_SKIP_TLS_VALIDATION=true` in the CLI session. This explicitly
disables server certificate validation for that CLI process; unset it after
debugging. The CLI accepts only loopback endpoints. Configure the
host through environment variables. The connection string and PFX password are
local secrets; do not put them in shell history, checked-in settings,
screenshots or logs. The sample
below assumes they have already been supplied through the current session's
secret-management method:

```powershell
$env:Haby__PostgreSql__ConnectionString = '<isolated catalog connection>'
$env:Haby__Tls__CertificatePath = "$env:TEMP\haby-local.pfx"
$env:Haby__Tls__CertificatePassword = '<local-password>'
$env:Haby__Port = '7443'
dotnet run --project .\src\Haby.Operator.Host
```

The host serves gRPC reflection on the same loopback endpoint in all
environments, including Production. Reflection reveals the service and message
contracts to local clients.

For local debugging without TLS, set `Haby__Tls__Enabled=false` before starting
the host. `Haby__Tls__CertificatePath` and `Haby__Tls__CertificatePassword` may
then be omitted. Use `HABY_OPERATOR_URL=http://localhost:7443` in the CLI
session. This uses cleartext HTTP/2 on loopback; keep TLS enabled for the
shadow pilot and any deployment beyond local debugging. An invalid
`Haby__Tls__Enabled` value stops startup. When TLS is enabled (the default),
the certificate path remains required and the password may be omitted for an
unencrypted PFX.

```powershell
$env:Haby__Tls__Enabled = 'false'
dotnet run --project .\src\Haby.Operator.Host

# In the CLI session:
$env:HABY_OPERATOR_URL = 'http://localhost:7443'
```

In another PowerShell session set the URL and use a local UTF-8 import envelope.
The client rejects files larger than 1,047,552 bytes before sending them. The
server enforces a 1 MiB gRPC receive-message limit:

```powershell
$env:HABY_OPERATOR_URL = 'https://localhost:7443'
dotnet run --project .\src\habyctl -- manifest validate .\manifest-envelope.json
dotnet run --project .\src\habyctl -- manifest import .\manifest-envelope.json
```

When debugging with an untrusted local certificate, set
`$env:HABY_OPERATOR_SKIP_TLS_VALIDATION = 'true'` before running the CLI commands.

For a small example, `manifest-envelope.json` can contain:

```json
{"applicationVersion":"v1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"example.service"},"spec":{}}}
```

The import prints an Application record ID and local revision number. Stop the
host, start it again with the same catalog, then inspect the exact revision:

```powershell
dotnet run --project .\src\habyctl -- manifest revision get '<application-record-id>' 1
```

The CLI prints identifiers, disposition, diagnostics and historical warnings.
It does not print the normalized declaration or configuration values. The host
records `haby-alpha-operator-host` as a technical audit actor because the alpha
endpoint cannot identify an individual caller. Keep the endpoint reachable only
from the intended developer console. A remote deployment, network policy and
application-level access control require separate review.

## Hypothetical database preview

Import this exact envelope as `manifest-envelope.json` with the command above:

```json
{"applicationVersion":"v1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"postgres-demo"},"spec":{"resources":{"database":{"type":"postgresql.database/v1alpha1","providerRef":"postgresql.primary","properties":{"name":"postgres_demo_db"}}}}}}
```

Use the returned Application record ID and revision number. Create a local
`preview-context.json` containing only non-secret, explicitly hypothetical
metadata:

```json
{
  "preview": true,
  "environmentId": "local-test",
  "providers": [{
    "alias": "postgresql.primary",
    "instanceId": "local-postgresql",
    "configurationRevision": 1,
    "implementationId": "haby.postgresql.database/v1alpha1"
  }]
}
```

After restarting the host with the same catalog, request the preview:

```powershell
dotnet run --project .\src\habyctl -- manifest revision preview '<application-record-id>' 1 .\preview-context.json
```

The CLI reads at most 16 KiB, accepts UTF-8 with or without a BOM, and rejects
unknown fields, including any secret or connection string. The server validates
the typed context again. A successful result says it **would create one database**
with the selected alias and exact metadata. It is hypothetical, cannot be
applied, and creates no ChangeSet, Plan, deployment, generation or target
database. This context is never saved as authoritative Provider state; the
later active pilot requires server-owned
durable inputs and a separately accepted Plan.

## Durable planning (single-database pilot)

Prerequisites are the migrated, single-instance loopback Host and the native CLI
above, plus an operator-prepared Environment and exact Provider configuration:

- The selected context must name an existing logical Environment ID in this registry.
- Trusted setup must prepare that Environment with `ISecretStore.CreateEnvironmentAsync`,
  the exact administrator SecretRef with `ISecretStore.CreateAsync`, and the Provider
  with `IProviderConfigurationStore.CreateInstanceAsync` and `AddRevisionAsync`.
  Choose the accepted secret-storage mode and database/backup controls before storing
  credentials. These are internal composition APIs, not operator RPCs or CLI commands.
- The imported Resource's alias must select a sealed, compatible exact revision through
  `IProviderAliasStore.ChangeAsync`. Normal alias switching performs mandatory read-only
  Provider preflight. Planning uses only the alias reader and codec; it never
  performs that network preflight or resolves secret material. Execution uses
  separate registered dependencies only after an explicit operation request.
- This slice adds no bootstrap API. A fresh empty catalog alone cannot produce a Plan.
  Have the maintainer prepare records through the existing trusted application/persistence
  APIs; do not insert catalog rows manually. The disposable rehearsal below provides
  executable test setup without requiring a persistent Provider database.

Use the single `postgresql.database/v1alpha1` Resource envelope shown above, with
no Components, exports or Parameters and only a valid literal database `name`.
An unsupported declaration returns `unsupportedShape`. Create intent explicitly
with `--expected-absence`; generation preconditions and unspecified preconditions
are rejected by the server. Every planning command requires a selected context;
`HABY_OPERATOR_URL` alone is insufficient. For ID-based reads/checks/closure, the
context selects the registry; returned records retain their own Environment.

With `config.yaml` selecting the prepared `pilot` Environment, run the published
executable (PowerShell, from the repository root):

```powershell
$cli = (Resolve-Path .\.artifacts\aot\win-x64\habyctl.exe).Path
$import = & $cli manifest import .\manifest-envelope.json --config .\config.yaml -o json | ConvertFrom-Json
$reference = $import.data.revision.reference
$change = & $cli changeset create $reference.applicationRecordId $reference.revisionNumber --expected-absence --config .\config.yaml -o json | ConvertFrom-Json
$changeId = $change.data.changeSet.changeSetId
& $cli changeset get $changeId --config .\config.yaml
$planned = & $cli plan create $changeId --config .\config.yaml -o json | ConvertFrom-Json
$planId = $planned.data.plan.planId
& $cli plan get $planId --config .\config.yaml
& $cli plan check $planId --config .\config.yaml -o json
& $cli plan close $planId --config .\config.yaml
& $cli plan get $planId --config .\config.yaml -o json
& $cli plan check $planId --config .\config.yaml # closed; expected exit 1
```

Check each exit code before using a result in automation. `changeset create` and
`plan create` create new immutable identities on each successful call. To recreate
a stale Plan, call `plan create` again with the same ChangeSet ID. A changed alias
makes the old Plan `planStale`; the new Plan pins the new exact revision. It never
rewrites the old Plan. A stale ChangeSet instead requires new intent.

`plan create` and `plan get` include server-owned `intendedWork` alongside the
Plan identity, fingerprint, current status, Environment and exact pinned Provider
instance/configuration revision/implementation. The current pilot describes only
Create of one local `postgresql.database/v1alpha1` Resource, with an explicitly
typed, Provider-neutral target name. The server maps the validated PostgreSQL
database detail to this review target; the wire contract and CLI do not interpret
Provider-specific payloads. Common description contracts and dispatch are also
Provider-neutral: the composition root explicitly registers one pure action
describer for the exact pinned implementation. PostgreSQL shape interpretation
and its typed detail live in the PostgreSQL adapter. An absent or ambiguous exact
describer returns `actionDescriberUnavailable`; no other implementation is
substituted. It derives these fields from the Plan's exact
immutable ManifestRevision, using the same supported-shape/name rules as planning.
It never follows a changed alias to describe an old Plan. Restart over unchanged
trusted revision content preserves the same intended work. No new persisted snapshot or migration is involved.

Before committing, review the existing Plan (illustrative non-secret output for
the `postgres_demo_db` example; actual IDs and timestamps are also printed):

```text
current
Plan: <plan-id>; ChangeSet: <change-set-id>
Environment: pilot; record: <environment-record-id>
Revision: <application-record-id> 1
Provider: postgresql.primary -> <provider-instance-id> revision 1; implementation: haby.postgresql.database/v1alpha1
Intended work only; no Provider preflight, proof of absence or execution guarantee. Commit validation still applies.
Intended action: Create; Resource: database; type: postgresql.database/v1alpha1; target: postgres_demo_db
```

The corresponding JSON fragment under `data.plan` is:

```json
{"intendedWork":{"status":"available","action":{"kind":"create","resourceId":"database","resourceType":"postgresql.database/v1alpha1","target":{"name":"postgres_demo_db"}}}}
```

A description read contacts no Provider, resolves no secrets and changes no catalog
state. Intended work is independent from the current-state check: stale, expired
and closed Plans may still describe their original action without becoming
committable. Missing revision input, decoding/identity/version failures,
unsupported shapes and inconsistent pinned inputs return bounded description statuses `revisionNotFound`, `inputCorrupt`,
`unsupportedShape` or `pinnedInputMismatch`, with no action, raw payload or exception.
The available Plan identity and current status remain visible. A CLI talking to an
older server reports `unavailable` when no description is supplied; unknown wire
outcomes fail closed. Review the intended database and pinned Provider, then use
the explicit commit command below. The server repeats atomic commit validation.

The catalog's persisted immutable ManifestRevision is a **trusted input**. Neither
the Plan fingerprint (which pins a revision reference) nor this read projection
proves historical content authenticity. Structural checks detect decoding,
identity, version and supported-shape failures; they do not detect every valid
content mutation. For example, directly replacing `sample_db` with another valid
name `other_db` in the trusted snapshot can produce an `available` description
with `other_db` for the same Plan. This trust-boundary behavior is exercised in
the real Host test, not claimed to be prevented. Protect catalog write access and
restore trusted backups. A cross-cutting integrity policy for import, planning,
commit and execution is a separate decision; this slice adds no checksum or
migration and does not mark legacy revisions unavailable for lacking a checksum.

A Plan expires 24 hours after creation, with its expiry persisted at creation.
`plan get` returns its immutable snapshot plus its current evaluated status,
including closed or expired records (exit 0 when the record exists).
`plan check` returns exit 0 only for `current`; closed, expired, stale and unavailable
inputs are typed rejections (exit 1). Existing precedence is expiry before closure,
then ChangeSet staleness, then exact-input comparison. `plan close` is idempotent:
`closed` and `alreadyClosed` succeed; a missing Plan is rejected. Closure never
accepts desired state, changes generations, queues operations or invokes a Provider.

Both text and JSON expose exact revision, Environment record/key (Plan), Provider
instance/configuration revision/implementation, fingerprint, creation/expiry and
closure metadata. JSON uses the versioned envelope with `data.changeSet` or
`data.plan` and source-generated DTO metadata. Actors are always the Host's
`haby-alpha-operator-host`, never a caller-provided human identity. Ordinary output
contains no Provider payload, SecretRef material, credential or driver error.
Malformed canonical IDs return `invalidRequest`; missing Environment, revision,
ChangeSet, Plan or alias and unsupported inputs remain typed results. An unknown
server status is a safe local error. Execution is a separate explicit command below;
recovery is a separate explicit workflow below.

### Explicit desired-state acceptance

With the same prepared Environment, sealed Provider configuration revision and alias,
create a ChangeSet and a current Plan as above. Keep the Plan open if accepting it;
`plan close` is an alternative to commit, not a prerequisite. Run:

```powershell
$accepted = & $cli changeset commit $changeId --plan $planId --config .\config.yaml -o json | ConvertFrom-Json
& $cli changeset commit get $changeId --config .\config.yaml
$deploymentId = $accepted.data.commit.deploymentId
& $cli deployment generation get $deploymentId 1 --config .\config.yaml -o json
```

Check the exit code before consuming each response. Both ChangeSet and Plan IDs
are required. The CLI never chooses a latest Plan or replans. The IDs address
stored records in the selected registry; the context's Environment does not
reassign or authorize access to those records. All three commands require a
selected context, just like durable planning.

A successful commit atomically persists exactly one desired generation `1`, one
immutable ChangeSetCommit and one Pending Create operation. It leaves
AppliedGeneration absent and performs no Provider contact, secret-material read,
execution attempt or dispatch. The target database has not been created.
Execution is a separate explicit step below. Generation `2`, updates,
removal, bootstrap and background work remain outside this surface.
No schema migration is needed for these adapters.

If the response is lost, repeat the **same ChangeSet ID**, or read it with
`changeset commit get`. Retry returns the original commit, including its original
Plan ID, time and server technical actor, even if another Plan ID is supplied or
inputs have since changed. It creates no second generation, commit or operation.
IDs must still be canonical UUIDv7 values. Do not create a new ChangeSet to retry
a committed one. Competing ExpectedAbsence ChangeSets for one deployment have one
winner; the loser receives `changeSetStale`, with `actualGeneration` when known.

Before a first acceptance, wrong-ChangeSet Plans, closed/expired Plans, unavailable
inputs, stale inputs and unsupported pilot shapes/preconditions remain typed
rejections. `changeSetStale` requires new intent; `planStale` permits a new Plan
for still-valid intent. Rejections leave no partial desired state. Public creation
already rejects unsupported shapes/preconditions before an executable Plan exists.
`commitNotFound` and `generationNotFound` are typed read outcomes; malformed or
missing IDs or a non-positive generation return `invalidRequest` at the API boundary.

JSON uses `data.stateMeaning: "desiredStateOnly"`, `data.commit` or
`data.desiredGeneration`. The generation contains its exact immutable revision,
Environment, Provider identity/configuration revision and implementation references.
These are desired snapshots, not current operation or applied-state read models.
Neither output format claims infrastructure is applied or reveals Provider payload,
secret material or raw driver diagnostics. The actor is server-owned technical
attribution, not authenticated human identity.

### Explicit operation execution and read-only observation

The Host explicitly composes the implemented PostgreSQL `Plaintext` SecretStore
mode under [ADR-006](../adr/0006-secret-storage-and-encryption.md). Secret payloads
are recoverable by readers of the corresponding database rows and backups.
Restrict catalog access, protect backup copies and use this profile only when
that exposure is accepted. `EnvelopeEncrypted` is not implemented by this Host.
No secret creation/reveal, Provider bootstrap or alias-switch RPC is added here;
trusted setup still uses the existing application APIs above. Execution resolves
the accepted exact Provider revision and its exact SecretVersion, even after an
alias switch. Unavailable pinned inputs cannot select a substitute.

After successful desired-state commit, identify the exact local Resource from
the accepted manifest (`db` in this example). Both commands require all four
identity fields; neither guesses a Resource nor selects a latest generation:

```powershell
& $cli operation get $deploymentId 1 db --action create --config .\config.yaml -o json
& $cli operation execute $deploymentId 1 db --action create --config .\config.yaml -o json
& $cli operation get $deploymentId 1 db --action create --config .\config.yaml
```

A context selects the registry. Since these commands use a complete persisted
identity, the transitional loopback `HABY_OPERATOR_URL` fallback also works when
no context is selected. Context Environment metadata does not reassign the
operation. Do not supply a password, connection string, Provider override or
actor; there are no such command arguments or request fields.

`ExecuteOperation` delegates to the singleton `IDurableCreateOperationExecutor`.
Only a previously committed generation `1`, exact Resource and `Create` are
supported. Malformed/missing identity returns `invalidRequest`, an unknown action
returns `unsupportedAction`, a later generation returns `unsupportedShape`, and
an absent operation returns `operationNotFound`. The adapter creates no journal
rows or execution decisions. Import, planning, commit and Host startup never
execute work. `GetOperation` calls the executor's existing read-only observation
path; it never checks the Provider, dispatches, recovers or adopts. In particular,
reading `Dispatching` leaves that marker untouched.

Both formats expose safe identity, state, bounded reason, exact non-secret
Provider references and timestamps. JSON uses source-generated DTOs under
`data.operation` and `data.attempts`; protobuf unknown fields and raw Provider
payloads are not serialized. The Host suppresses low-level EF Core/Npgsql diagnostics
that could expose raw driver errors or credential-dependent payloads. Unexpected
operation infrastructure failures return a value-free transport error, not a
success or a rollback claim. Execute returns its execution snapshot; use get for
the retained attempt history. Get succeeds (exit `0`) when a record exists even
if its state requires attention. Execute succeeds only for `succeeded` (exit `0`);
all typed execution rejections return exit `1`:

| Execute outcome | Meaning and next step |
| --- | --- |
| `succeeded` | Confirmed CREATE and independent existence check were recorded with AppliedGeneration advancement. A repeated execute sends no second CREATE and preserves history. |
| `failed` | Definitive failure, including `conflict` on an existing database. No adoption or AppliedGeneration advancement. |
| `retryableFailure` | Exact dependency, secret or connectivity unavailable before dispatch. Inspect the bounded reason, restore the same pinned input and explicitly execute the same key again. |
| `needsAttention` | Uncertain external effect; repeated execute cannot send CREATE or claim success. Separate manual recovery is required. |
| `dispatchUncertain` | Dispatch-marker commit or terminal persistence could not be confirmed. Inspect the durable record; this response is no proof of non-execution. |

Transport errors, deadlines and cancellation return exit `3`; they do not prove
rollback and are never retried automatically by the CLI. Inspect with `operation
get` after an interrupted request. A persisted `Dispatching` marker becomes
`NeedsAttention` only on explicit execution, without sending SQL, even if a
matching database exists. Neither its presence nor its absence authorizes a
blind retry, adoption or success claim. Only confirmed success advances
AppliedGeneration; desired-generation history is unchanged by execution.

Manual assignment is available through the explicit recovery workflow below.
Do not edit journal rows or delete/recreate an operation to bypass the barrier.
The [active-pilot catalog restore rehearsal](active-pilot-restore-rehearsal.md)
covers operation, attempt and recovery history through a fresh Host after real
pg_dump/pg_restore. Broader pilot readiness remains separate. TLS/loopback isolation, one instance,
no authentication and technical rather than human attribution remain unchanged.

### Explicit recovery of one uncertain operation

First inspect the exact operation. Inspection is read-only: it does not contact
the Provider, change `Dispatching` to `NeedsAttention`, dispatch Create or assign
anything. If a previous process died in `Dispatching`, the existing explicit
`operation execute` path records `NeedsAttention` without sending SQL; inspect
again afterwards. An ordinary definitive `conflict` is ineligible for assignment.

```powershell
& $cli operation recovery inspect $deploymentId 1 db --action create --config .\config.yaml -o json
```

Record the exact `preview.operation.environmentRecordId` and
`preview.uncertainAttemptId`. Independently investigate the Environment,
Application, generation, Resource, pinned Provider instance/configuration,
implementation, target name and uncertain attempt shown by inspection. Database
existence is necessary but does not prove that Haby created it. Proceed only
when the operator accepts responsibility for the provenance and assignment of
this exact target. If that judgment cannot be made, leave it `NeedsAttention`.

Prepare a separate UTF-8 note file containing a short explanation with no
credentials, connection strings, tokens, Provider payloads or driver errors.
The CLI bounds the actual read to 2048 bytes; the server independently requires
1-512 characters, rejects blank text, controls/bidi and known credential patterns.
These checks cannot prove arbitrary prose secret-free: excluding secrets remains
the caller's responsibility. The note is protected audit input and is never
echoed in text/JSON, stderr, validation errors or ordinary reads. A trailing
newline is a control character and is rejected; save the note without it.

```powershell
& $cli operation recovery assign-existing $deploymentId 1 db --action create `
    --environment-record-id $environmentRecordId --expected-attempt-id $uncertainAttemptId `
    --acknowledge-assignment --reason operatorAcceptedExistingDatabase `
    --note-file .\recovery-note.txt --config .\config.yaml -o json
```

There is no inherited yes, prompt acceptance, default consent, Provider override
or actor argument. Before submission the CLI re-inspects and writes the safe
target/attempt/uncertainty summary to stderr. Stdout remains one JSON envelope.
It never replaces supplied expected identities with newer preview identities.
Deployment and Environment record IDs require canonical lowercase UUIDv7.
Attempt IDs use D-format UUID syntax; hex case does not change their identity,
and the CLI submits the caller's original spelling.
Preview is not authorization: the server independently checks consent, exact
Environment/attempt, eligible uncertainty, fresh existence on the pinned target
and authoritative transaction guards. A competing state change fails closed.
No Create is dispatched by assignment, and no new execution attempt is added.

The server selects the compile-time adapter using the operation's immutable
implementation, Resource contract and action. Only PostgreSQL database Create
is registered initially. It never follows the current alias or defaults an
unsupported operation to PostgreSQL. The adapter delegates to
`IPostgreSqlDatabaseRecovery`; the existing atomic transaction records one
immutable decision and advances `Succeeded`/`AppliedGeneration` together.
The original uncertain attempt remains retained.

| Recovery outcome | Meaning |
| --- | --- |
| `found` | Safe read-only preview and any original value-free receipt; exit 0. |
| `assigned` / `alreadyAssigned` | Immutable receipt; exit 0. |
| `invalidRequest` / `rejected` | Invalid consent/reason/note or stale/mismatched identity/state; exit 1. |
| `targetAbsent` | Leave uncertainty unresolved; absence does not authorize another Create. |
| `targetUnavailable` | Exact pinned inputs/target could not be verified; restore those inputs, never substitute another alias target. |
| `operationNotFound` / `unsupportedRecoveryAction` | No matching operation or no registered handler for that pinned contract/action. |

Other typed rejections exit 1. Local syntax/file/unknown-wire errors exit 2;
transport/cancellation failures exit 3 and do not establish rollback. The CLI
never retries automatically. After a lost response inspect, then repeat the
same complete assignment command with the original Environment and attempt.
A successful retry reaches application idempotency and returns the original
receipt, including after restart, without a new Provider existence check.
The receipt has the decision/operation/attempt identities, controlled reason,
server-owned technical actor and timestamp; it contains no free-text note.
That actor attributes activity to the permitted Host, not to an authenticated
human. Network isolation and the operator-controlled console remain required.
No automatic adoption, reset/delete or retry-after-uncertainty policy is added.

### Disposable persistence and native executable rehearsal

After publishing as above, leave `HABY_AOT_CLI_PATH` set to the published executable
and `HABY_AOT_CLI_REQUIRED=1`. On Windows with WSLc, run:

```powershell
.\scripts\test-postgresql-wslc.ps1
```

On a Docker-compatible host, use the same environment variables and run the
integration project as described in [the PostgreSQL test guide](postgresql-integration-tests.md).
`OperatorHostIntegrationTests.Durable_planning_survives_Host_restart_pins_alias_inputs_and_never_contacts_Provider`
creates a disposable catalog, seeds accepted records through existing APIs, calls
the generated client, disposes the Host and starts a fresh instance over that
catalog, then executes all six planning commands through the native binary. A TCP trap
proves planning never connects to the Provider; independent catalog reads assert
no desired generations, commits, operations, attempts, recovery decisions or target
database. Setup alone uses a deterministic preflight double for the offline fixture;
the production Host and planner are unchanged. Normal operator setup still requires
real preflight. Linux integration CI requires this native rehearsal; both Windows
and Linux AOT gates additionally exercise every planning command in text/JSON through
disposable gRPC loopback test services.

The commit rehearsal additionally races generated-client requests, retries with a
second Plan and changed alias, and rereads identical commit/generation snapshots
through a fresh TLS Host. Its Host catalog role is denied SELECT on secret_payloads;
independent assertions require exactly one generation/commit/Pending operation,
absent AppliedGeneration, zero attempts/dispatch and no target database or Provider
TCP contact. The native sequence imports, creates intent and Plan, commits, then
reads/retries through a fresh Host. Both Windows/Linux AOT loopback gates exercise
all three commands in text and JSON; Linux PostgreSQL CI requires the full native
persistence sequence. These tests use disposable catalogs, never personal databases.

`OperatorHostIntegrationTests` also exercises real production Host composition with
a disposable Provider: explicit generated-client and published native CLI execution,
independent database absence/presence and AppliedGeneration checks, concurrent
requests, restart and successful retry, conflict without adoption, unavailable
pinned secret access, read-only Dispatching observation, and deterministic test-only
crash/cancellation windows around the dispatch barrier. The native CLI smoke gates
exercise operation outcomes and safe text/source-generated JSON on Windows and Linux.
Fault hooks have no production configuration or RPC surface.

`OperatorRecoveryIntegrationTests` exercises deterministic uncertain-after-CREATE
recovery through production Host/generated client and the published native CLI,
concurrent decisions, restart/lost-response receipts with the Provider login
disabled, atomic rejection/failure, unavailable/absent targets, stale previews
and unsupported pinned implementations. Independent catalog reads verify one
retained uncertain attempt, one decision and applied-generation advancement;
assignment also succeeds with a pinned login lacking CREATEDB. Both native
loopback gates exercise recovery commands, consent, bounded UTF-8 input, safe
text/JSON and transport failures. The
[active-pilot restore checkpoint](active-pilot-restore-rehearsal.md) additionally
proves recovery-history preservation without Provider access or replay.
