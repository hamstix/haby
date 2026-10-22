# Local alpha operator workflow

The operator host and `habyctl` are an internal alpha interface for a single
Haby instance. The host listens only on IPv4 loopback with HTTP/2 and TLS. It
has no application authentication or authorization and is unsuitable for broad
or remote exposure. Its protobuf contract may change before M4. It only
validates, imports and reads immutable manifest revisions. It does not plan or
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
uses the operating system's normal certificate validation; it has no
certificate-validation bypass. Configure the host through environment
variables. The connection string and PFX password are local secrets; do not
put them in shell history, checked-in settings, screenshots or logs. The sample
below assumes they have already been supplied through the current session's
secret-management method:

```powershell
$env:Haby__PostgreSql__ConnectionString = '<isolated catalog connection>'
$env:Haby__Tls__CertificatePath = "$env:TEMP\haby-local.pfx"
$env:Haby__Tls__CertificatePassword = '<local-password>'
$env:Haby__Port = '7443'
dotnet run --project .\src\Haby.Operator.Host
```

For local gRPC discovery, set `DOTNET_ENVIRONMENT=Development` before starting
the host. The host then serves gRPC reflection on the same loopback TLS endpoint.
Reflection is unavailable in other environments because it reveals the service
and message contracts.

In another PowerShell session set the URL and use a local UTF-8 import envelope.
The client rejects files larger than 1,047,552 bytes before sending them. The
server enforces a 1 MiB gRPC receive-message limit:

```powershell
$env:HABY_OPERATOR_URL = 'https://localhost:7443'
dotnet run --project .\src\habyctl -- manifest validate .\manifest-envelope.json
dotnet run --project .\src\habyctl -- manifest import .\manifest-envelope.json
```

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
