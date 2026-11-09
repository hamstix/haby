# PostgreSQL connection renderer contract

Status: accepted design under [ADR-040](../adr/0040-postgresql-connection-rendering-and-consumer-options.md).
Pure primitives and exact planning are implemented by [issue #144](../development/144-postgresql-connection-rendering.md).
This document does not claim a working configuration delivery endpoint.

Renderer metadata selects the exact Resource contract and producer action/output
contract/key. Planning resolves exactly one matching producer for the bound Resource
and freezes its concrete operation ID in the render pin. Multiple logins use the
same renderer/formatter registration; there is no Application-wide fixed producer.
Missing or ambiguous matches refuse. See [#168](../development/168-independent-database-login-pair.md)
for current alpha API/catalog implications.

## Exact contracts

- Resource: postgresql.application-login/v1alpha1.
- Confirmed output: postgresql.application-login-output/v1alpha1, key connection.
- Renderer: postgresql.npgsql-connection/v1alpha1.
- Deferred String formatter: postgresql.npgsql-connection-string/v1alpha1.

The final access action supplies host, port, database, schema, login and Password
SecretRef. Shared selection checks exact generation/Resource/producer evidence;
the adapter checks its typed payload. Current aliases or Provider configuration
are not rendering inputs. The renderer returns one deferred scalar; Profiles own
its document path, and the standard composer owns insertion/collision handling.

## Closed Binding.options and Profile defaults

```json
{
  "timeout": 200,
  "commandTimeout": 30,
  "sslMode": "verify-full"
}
```

| Field | Type | Allowed values | Default |
| --- | --- | --- | --- |
| timeout | integer | 1-300 seconds to establish connection | 15 |
| commandTimeout | integer | 1-3600 seconds for SQL execution | 30 |
| sslMode | string | verify-full, require, disable | verify-full |

Absent fields inherit: renderer defaults, then exact Profile rule.options, then
Component Binding.options. Missing options/{} is an empty partial override.
Unknown fields, null, zero/infinite timeout, out-of-range or nonintegral values
refuse. Exact integer values such as 200.0/2e2 normalize to 200 without double.
No legacy aliases connectionTimeoutSeconds/commandTimeoutSeconds are introduced.
Matching a driver property name does not admit other driver properties.

verify-full validates TLS/server identity, require encrypts without identity
verification, disable explicitly disables TLS. Isolated pilots may choose disable.
No Trust Server Certificate option or custom certificate file path is exposed.
No host/port/database/user/password/schema/search-path/privilege override, suffix,
arbitrary Options, Passfile, logging or pooling knobs are permitted by this contract.

## Profile placement and Component exception

```json
{
  "slot": "main-database",
  "rendererRef": "postgresql.npgsql-connection/v1alpha1",
  "options": { "sslMode": "disable" },
  "documentProjections": [
    { "documentRef": "settings", "path": "/PostgreSQL/DefaultConnection/ConnectionString" }
  ]
}
```

An Environment-selected layout Profile defines settings/appsettings.json with
template {}. api and worker bind the same app-login and request main-database.
api overrides options with { "timeout": 200 }; worker inherits 15. Both receive
the same exact password reference and Resource identity. Command Timeout remains
30. Explicit disable above is for an isolated pilot, not a general TLS default.

## Validation and confidentiality

Application import checks supplied partial options against registered metadata.
Profile import remains portable; planning resolves active exact contracts,
Resource/output compatibility, defaults and effective typed options. Freeze all
resolved settings/capability identities in Plan/generation inputs. Rehydration
does not recompute them from the current build's defaults.

The renderer has no SecretStore/network capability. Its arguments retain typed
SecretReference, not material. The pure formatter uses NpgsqlConnectionStringBuilder
only after an authorized caller supplies the exact resolved material. First-slice
tests may explicitly supply disposable synthetic material; production resolution
and delivery remain separate work.

Preview redacts the entire connection-string value; normal serialization refuses
it unresolved. Do not persist/log/hash a completed string or secret. Driver key
ordering is not contractual. Correct escaping and actual connection behavior are
verified using the driver and real disposable PostgreSQL. No materialized
ConfigurationDocumentRevision, credential reveal or delivery route is introduced
by accepting this renderer contract.
