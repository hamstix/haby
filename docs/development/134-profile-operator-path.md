# Profile operator administration

Issue #134 exposes installation-scoped Configuration Profile declaration validation,
immutable import and exact revision reads through the existing internal alpha
gRPC Host and Native AOT `habyctl`. These are Configuration Profiles, separate from
Resource privilege policies and Kubernetes Workload profiles.

Use the current [Host/PostgreSQL setup](alpha-operator-local.md) and the
existing loopback-only transport/network isolation. Configure a registry instance
and context with the usual CLI YAML configuration. `--config` and `--context`
select that registry; a context's Environment does not scope Profiles and need
not exist. No network, authentication or secret-distribution policy changes.

## Rehearsal

Keep editable source JSON under operator control. The repository's safe examples
are [layout](../examples/configuration-profiles/layout.json),
[logging](../examples/configuration-profiles/serilog.json) and
[unresolved secret declarations](../examples/configuration-profiles/secret-projection.json).
For a minimal independent example, save this as `profile.json` in UTF-8 without BOM:

```json
{
  "apiVersion": "haby.dev/v1alpha1",
  "kind": "ConfigurationProfile",
  "metadata": { "id": "operator-layout", "revision": 1 },
  "spec": {
    "documents": {
      "settings": {
        "fileName": "appsettings.json",
        "format": "json",
        "template": { "Logging": { "Level": "Information" } }
      }
    }
  }
}
```

```powershell
habyctl profile validate profile.json --config config.yaml --context pilot
habyctl profile import profile.json --config config.yaml --context pilot -o json
habyctl profile revision get operator-layout 1 --config config.yaml --context pilot -o json
```

Validation returns `valid` or `invalidDeclaration`; it checks only portable
wire/declaration validity. It performs no storage or reference lookup. Even a
Profile with `secretProjections` may validate successfully without those secrets
existing. This grants no distribution authority or executable configuration.

Import returns `created`, `existingEquivalent`, `invalidDeclaration` or
`revisionContentConflict`. Save the first successful JSON result's exact
`data.reference`, `data.technicalActorId` and `data.importedAtUtc`. Reorder JSON
Object members, repeat import, and compare those three fields: they must remain
identical with `existingEquivalent`. Stop and restart the Host against the same
catalog, then repeat exact get and equivalent import. Get returns `found` with
the same identity and original audit; reimport still returns `existingEquivalent`.
The actor is the Host's technical identity, never an operator-supplied identity.

Change a template value while retaining ID/revision: import must return
`revisionContentConflict`, preserving the earlier content/audit. Choose another
explicit positive Int32 revision to accept another immutable declaration. Reading
an absent revision returns `notFound`, even when a higher revision exists. There
is no automatic next revision, latest, replacement or Environment fallback.

Exact get with JSON output now returns the complete normalized portable declaration
in object-valued `data.declaration`, alongside exact `data.reference` and original
import audit. It includes every document/template, static contribution/value,
Binding rule/options/projection and declared secret name/version/path/format.
Names may be absent from SecretStore: read never resolves names, reads material,
executes templates/formatters/renderers or calls Provider/output adapters. Ordinary
values are returned unchanged; credentials mistakenly embedded in ordinary values
are also returned unchanged. This is declaration administration under the existing
isolated operator boundary, with no new reveal switch or network/auth policy.

This is normalized content, not original file bytes, ordering, omission choices,
source formatting, an internal snapshot wrapper or a serialized CLR graph. Maps
and projection sets use deterministic ordinal ordering; ordinary arrays retain
order and numbers retain exact arbitrary precision as JSON numbers (including
`1e999`). Empty maps/options/secretProjections use documented omitted defaults.
Portable numeric spelling can differ from internal CanonicalText: `0e0` becomes
`0`, unnecessary `e0` is omitted, and plain spelling is used only when shorter.
Huge exponents stay scientific. Contract integers (`metadata.revision` and
`secretRef.version`) also use shorter exact exponent spelling when beneficial;
the CLI decodes them exactly as positive Int32 values, including `1e9`. Domain normalization/equality/hashing, internal
snapshots and existing configuration JSON serialization remain unchanged. Text get remains a concise status/identity/audit summary.

Validate/import replies, including Created/ExistingEquivalent, and assignment
responses remain metadata/decisions only. NotFound/conflict/failure never includes
partial declaration content. JSON stdout is one format-version-1 envelope.
Extract a successful exact read in PowerShell 7 using JsonElement.GetRawText(),
which copies number tokens without conversion to double/decimal:

```powershell
habyctl profile revision get operator-layout 1 -o json | ForEach-Object {
    $options = [System.Text.Json.JsonDocumentOptions]@{ MaxDepth = 66 }
    $result = [System.Text.Json.JsonDocument]::Parse($_, $options)
    try { $result.RootElement.GetProperty("data").GetProperty("declaration").GetRawText() }
    finally { $result.Dispose() }
} | Set-Content -NoNewline -Encoding utf8 normalized-profile.json
habyctl profile import normalized-profile.json -o json
```

Successful equivalent reimport preserves the first import actor/time and returns
ExistingEquivalent. Use number-preserving tooling when editing the extracted
JSON; converting arbitrary values through floating-point types loses precision.

Diagnostics retain standard codes, total error
count and truncation; arbitrary member names or overly long source paths are
reported at the root rather than echoed. Equal redacted findings are deduplicated
and sorted; the total counts original findings and truncation also signals this
reduction of diagnostic detail. Diagnostics never contain source values, Arguments
or snapshots. No export/reveal/raw command is added; editing may use the original
source file or an extracted normalized declaration.

Exit codes follow existing commands: 0 success, 1 expected refusal/conflict or
not-found, 2 usage/configuration/file failure, 3 transport/server failure.
Usage and transport failures go to stderr. The existing bounded file reader
preserves source bytes, including BOM, so BOM is rejected by the server rather
than silently stripped. The client has the existing 1 MiB minus 1 KiB file limit;
the Host retains its 1 MiB protobuf message limit (including framing), parser
1 MiB payload limit and 64-container depth limit. Oversized transport requests
fail with ResourceExhausted; malformed exact identities fail with InvalidArgument.

Cancellation propagates through use cases and stores. A lost response does not
prove rollback: inspect the exact revision and retry the same source identity
and content explicitly. There is no client retry loop. Confirmed import uses the
store's original audit and commit semantics. Corrupt/unavailable storage fails
with a fixed safe Internal status, never not-found or a repaired declaration.

## Evidence and limits

`OperatorProfileIntegrationTests` exercises generated client -> actual Host ->
Application parser/import/query -> disposable PostgreSQL, including startup,
restart, full normalized declaration round-trip and ExistingEquivalent reimport,
original audit preservation, exact numeric values, missing declared secrets,
near-limit strings/numbers, Unicode block-list characters, omitted defaults
and depth-64 content through published CLI, reordered retries, conflicts,
independent revisions, malformed requests,
size/depth limits, bounded diagnostics, zero database commands for validation,
no unrelated writes, corrupt snapshots and captured log redaction. Separate
DbContexts arrange and verify persisted state. A blocking command interceptor
also observes cancellation reaching the actual store's database command.

With `HABY_AOT_CLI_REQUIRED=1` and `HABY_AOT_CLI_PATH` pointing at a published
binary, this test runs real CLI imports, retries, validation and exact reads
against both Host lifetimes, using text/JSON, config/context override, BOM refusal,
conflict and not-found exits. `CliLoopbackTests` additionally runs the published
binary against a disposable generated service in Linux/Windows Native AOT CI.
Malformed/missing/truncated/foreign declaration replies and forbidden outcome
content fail before any stdout in both formats, including the published binary.
The CLI checks transport structure/identity/status, without a second semantic
Profile parser or persistence dependency. The Profile JSON envelope alone permits
two additional wrapper containers; declaration depth remains 64. Host portable
serialization uses minimal JSON escaping (including Unicode property names,
controls and contract paths) and the CLI preserves its checked tokens. The
1,000,234-byte / 500,000-zero regression proves import -> exact get -> native JSON
extraction -> ExistingEquivalent reimport with original audit on both Host lifetimes.
The published CLI writes UTF-8 without BOM even on Windows, preserving literal
Unicode independently of the console code page. No
unrelated request/message limits are changed.
Existing Application persistence, generation, provisioning and startup suites
remain required. Run the solution restore/build/test/format commands from
CONTRIBUTING and the disposable PostgreSQL suite; Windows can use
`scripts/test-postgresql-wslc.ps1` with the same Native AOT environment variables.

[Scoped assignment administration](136-scoped-profile-assignments.md) is now implemented separately. [Structural planning integration](142-frozen-profile-planning.md) is now implemented separately. Renderers, document
materialization, secret resolution/distribution/delivery, list/search/export,
updates/deletion and UI remain deferred. Persisted secret-bearing Profiles still
fail effective expansion with `profile.secret-projections.unsupported` and no
partial layout. This slice adds no packages, migrations, reset or public API
compatibility guarantee; use matching alpha Host and CLI builds.
