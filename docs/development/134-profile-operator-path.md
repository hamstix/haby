# Profile operator administration

Issue #134 exposes installation-scoped Configuration Profile declaration validation,
immutable import and exact revision metadata through the existing internal alpha
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

Both text and JSON output contain only status, exact identity, original audit
when accepted/found, and safe bounded diagnostics when invalid. JSON stdout is
one format-version-1 envelope. Diagnostics retain standard codes, total error
count and truncation; arbitrary member names or overly long source paths are
reported at the root rather than echoed. Equal redacted findings are deduplicated
and sorted; the total counts original findings and truncation also signals this
reduction of diagnostic detail. No raw JSON, template/static values,
options, secret names/versions, Arguments or snapshots are returned. There is
no export/reveal/raw option; editing uses the operator's original source file.

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
restart, reordered retries, conflicts, independent revisions, malformed requests,
size/depth limits, bounded diagnostics, zero database commands for validation,
no unrelated writes, corrupt snapshots and captured log redaction. Separate
DbContexts arrange and verify persisted state. A blocking command interceptor
also observes cancellation reaching the actual store's database command.

With `HABY_AOT_CLI_REQUIRED=1` and `HABY_AOT_CLI_PATH` pointing at a published
binary, this test runs real CLI imports, retries, validation and exact reads
against both Host lifetimes, using text/JSON, config/context override, BOM refusal,
conflict and not-found exits. `CliLoopbackTests` additionally runs the published
binary against a disposable generated service in Linux/Windows Native AOT CI.
Existing Application persistence, generation, provisioning and startup suites
remain required. Run the solution restore/build/test/format commands from
CONTRIBUTING and the disposable PostgreSQL suite; Windows can use
`scripts/test-postgresql-wslc.ps1` with the same Native AOT environment variables.

[Scoped assignment administration](136-scoped-profile-assignments.md) is now implemented separately. Planning integration, renderers, document
materialization, secret resolution/distribution/delivery, list/search/export,
updates/deletion and UI remain deferred. Persisted secret-bearing Profiles still
fail effective expansion with `profile.secret-projections.unsupported` and no
partial layout. This slice adds no packages, migrations, reset or public API
compatibility guarantee; use matching alpha Host and CLI builds.
