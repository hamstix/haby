# PostgreSQL connection rendering primitives

Issue #144 implements ADR-040's pure renderer/formatter and planning readiness.
It does not expose rendered configuration through the operator API.

## Declare the consumers

Use [the Profile example](examples/144-postgresql-connection-profile.json) as an
Environment assignment. Import it and apply the exact revision using the existing
[Profile assignment commands](136-scoped-profile-assignments.md). Both Components
bind the same `postgresql.application-login/v1alpha1` Resource:

```json
{
  "api": {
    "bindings": {
      "storage": {
        "target": { "kind": "local-resource", "resourceRef": "app-login" },
        "configurationSlot": "main-database",
        "options": { "timeout": 200 }
      }
    }
  },
  "worker": {
    "bindings": {
      "storage": {
        "target": { "kind": "local-resource", "resourceRef": "app-login" },
        "configurationSlot": "main-database"
      }
    }
  }
}
```

The Profile supplies placement in `settings` at
`/PostgreSQL/DefaultConnection/ConnectionString`. Its empty template permits the
existing composer to create parents. Static contributions and collision rules
remain unchanged. Renderer defaults are timeout 15, commandTimeout 30 and sslMode
verify-full. The example explicitly selects disable for a disposable isolated
pilot. A Profile default is overridable; it is not an Environment security floor.

Import validates the closed partial Binding options schema. Profile import stays
portable. Planning validates each active Profile/Binding layer, exact Resource
compatibility, registered renderer/formatter availability and complete settings.
Unknown/null properties, legacy aliases, nonintegral values, numeric strings and
out-of-range values refuse. Integral decimals/exponents normalize exactly, without
floating point conversion. Endpoint, identity, SecretRef, schema, raw suffixes,
driver Options, Search Path, Passfile and pooling controls are not consumer options.
Unused rules require neither capability nor output consumption.

## Exact planning and pure execution

Plans and accepted generations retain renderer/formatter contracts and compatible
implementation identities, Resource/output/producer selection, renderer defaults,
complete effective options and per-property origins. Renderer defaults have their
own provenance. Strict decode checks the saved structural inputs and option merge;
it never obtains defaults from current registrations. A compatible later build
can change defaults for new Plans while old Plans retain their accepted values.
Unavailable or incompatible exact support refuses without substitution.

`ConfigurationOutputRenderer` uses the shared `IConfigurationOutputEvidence` port.
The catalog adapter reads the exact generation in a repeatable-read transaction,
checks the containing Environment/Application/Resource, expected action, exact
output contract/key and successful current producer attempt. It does not resolve
current aliases, Provider settings or secret material. The PostgreSQL adapter
decodes the confirmed payload into its typed connection evidence.

The pure renderer returns one confidential deferred String fragment. It retains
the existing exact Password SecretRef and safe typed arguments, without creating
another role or credential slot. The Profile owns its path. Preview replaces the
whole deferred scalar and records that path; ordinary serialization refuses it.

The pure formatter accepts explicitly supplied transient material and uses
`NpgsqlConnectionStringBuilder`. It sets only the accepted connection properties,
explicit TLS mode and disables Npgsql 10 GSS encryption so the TLS contract is not
replaced by driver negotiation defaults. It does not override the role's
server-side search_path. No SecretStore, network, Provider or document dependency
is passed to the formatter. Its result has a redacted ToString and no material
value equality/hash. Callers must treat `Reveal()` as confidential. Npgsql needs
managed strings; clearing owned test buffers cannot erase all managed copies.

Planning responses expose only bounded standard diagnostic codes. They do not
include templates, option values, deferred arguments or secret selectors. Existing
`profile.secret-projections.unsupported` refusal remains in force.

## Catalog admission

The configuration snapshot is now format 2. Outer Plan/graph format and fingerprint
version remain 2; the fingerprint includes the complete new configuration payload.
Migration `20261107101422_PinConfigurationRendering` marks this current-alpha
boundary without adding Provider-specific columns or tables. A prior catalog
containing any durable Plan/generation requires explicit fresh initialization under
ADR-033. Startup and direct migration refuse it without reset or backfill. A prior
admitted catalog without durable Plans/generations can migrate, preserving imports,
Profiles and assignments. Catalog initialization never removes external resources.
Keep old binaries/backups for inspection of superseded histories.

Current-build restart, retry and actual pg_dump/pg_restore remain supported. The
assignment scope locks, absence tokens and atomic commit protection from #142 are
unchanged. Changing assignments after commit does not alter generation execution.

## Evidence and limits

Fast tests cover option/schema parity, exact integer spellings, forbidden overrides,
quoting of semicolons/equals/quotes/backslashes/Unicode, all three explicit TLS
mappings, confidential serialization/redaction, frozen defaults and a second
synthetic typed family through actual generic dispatch and registration refusal.

The disposable PostgreSQL suite covers real five-step confirmed output selection,
api/worker settings and shared SecretRef, actual Npgsql authentication with supplied
test material, wrong-password rejection, exact current_user/current_database,
composition, restart and real catalog restore. The fixture uses Disable; it does
not establish a VerifyFull certificate deployment or a real Require TLS rehearsal.
The full unfiltered measured gate, existing restore/race/provisioning cases and
required Linux/Windows Native AOT jobs remain mandatory. Actual run results belong
in the PR handoff, separately from these test descriptions.

There is no production material resolver, reveal/delivery endpoint, materialized
ConfigurationDocumentRevision store or rendered-preview command. Authorization,
document selection and confidential transport remain separate accepted slices.
Direct Profile secret projections, Liquid/parameters/YAML execution, certificate
distribution, rotation/recovery, generation updates and multi-Application admission
are also deferred.
