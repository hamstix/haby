# Local alpha operator workflow

The operator host and `habyctl` are an internal alpha interface for a single
Haby instance. The host listens only on IPv4 loopback with HTTP/2. TLS is
enabled by default and can be explicitly disabled for local debugging. It
has no application authentication or authorization and is unsuitable for broad
or remote exposure. Its protobuf contract may change before M4. It validates,
imports, reads immutable manifest revisions and previews one supported database
Resource against hypothetical request metadata. It also creates and reads durable
ChangeSets and Plans, checks exact planning inputs and closes Plans. Desired-state
commit, Provider execution, database provisioning, secret generation and configuration
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
`config view` and `config current-context` emits one
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
  Provider preflight. The planning Host registers only the alias reader and codec;
  planning itself never performs that network preflight or resolves secret material.
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
server status is a safe local error. No apply/execute/recovery command is exposed.

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
catalog, then executes all six new commands through the native binary. A TCP trap
proves planning never connects to the Provider; independent catalog reads assert
no desired generations, commits, operations, attempts, recovery decisions or target
database. Setup alone uses a deterministic preflight double for the offline fixture;
the production Host and planner are unchanged. Normal operator setup still requires
real preflight. Linux integration CI requires this native rehearsal; both Windows
and Linux AOT gates additionally exercise every new command in text/JSON through
disposable gRPC loopback test services.
