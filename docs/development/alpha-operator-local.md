# Local alpha operator workflow

The operator host and `habyctl` are an internal alpha interface for a single
Haby instance. The host listens only on IPv4 loopback with HTTP/2. TLS is
enabled by default and can be explicitly disabled for local debugging. It
has no application authentication or authorization and is unsuitable for broad
or remote exposure. Its protobuf contract may change before M4. It validates,
imports, reads immutable manifest revisions and previews one supported database
Resource against hypothetical request metadata. It does not durably plan or
commit a ChangeSet, execute a Provider, provision a database, generate a secret
or deliver configuration.

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

The CLI reads at most 16 KiB and rejects unknown fields, including any secret
or connection string. The server validates the typed context again. A successful
result says it **would create one database** with the selected alias and exact
metadata. It is hypothetical, cannot be applied, and creates no ChangeSet,
Plan, deployment, generation or target database. This context is never saved as
authoritative Provider state; the later active pilot requires server-owned
durable inputs and a separately accepted Plan.
