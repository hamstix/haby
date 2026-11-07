# Immutable unresolved configuration documents

Issue #146 implements [ADR-041](../adr/0041-configuration-document-materialization-and-preview.md).
An operator can materialize one document, inspect its metadata and read its stored
redacted preview. This does not deliver application-ready configuration: the
connection-string value remains a typed confidential DeferredValue containing an
exact SecretRef. No secret material is read and no formatter is invoked.

## Prepare the generation

Use the isolated [five-step operator rehearsal](120-five-step-operator-path.md)
and [Profile/Binding setup](144-postgresql-connection-rendering.md). Import the
exact Profile and assign it before calculating the Plan. The api Binding pins
timeout 200; worker inherits timeout 15. Both consume the same application-login
output and Password SecretRef. The Profile owns document `settings`, fileName
`appsettings.json`, ordinary Logging and the connection-string projection path.

Complete all five generation operations. Configuration commands never execute
provisioning, advance desired/applied state or regenerate a credential. A pending,
partially successful or uncertain current desired generation refuses selection;
there is no previous-applied fallback, including for static-only documents.

## Create, inspect and preview

Select the existing CLI context/registry. Replace `processor-service` with the
portable ApplicationId from the imported manifest, and `pilot` with the Environment
name. These commands require no catalog UUID. `configuration` is separate from
the local `config` command group.

```powershell
habyctl configuration materialize processor-service --environment pilot --component api --document settings --output json
habyctl configuration materialize processor-service --environment pilot --component worker --document settings --output json
habyctl configuration get processor-service --environment pilot --component api --document settings --output json
habyctl configuration preview processor-service --environment pilot --component api --document settings --output json
```

`--environment` overrides the configured context Environment; `--context` still
selects the registry. Without an explicit Environment, the selected context's
Environment is used. All three commands also support positive `--generation`.

The server resolves names and the generation once in a repeatable-read view,
checks the exact frozen Component/document layout, and thereafter uses only
DeploymentId + Generation + ComponentId + DocumentId. Every success reports that
resolved target and ConfigurationDocumentRevisionId. Concurrent changes can cause
refusal, but never retarget the request. The reply does not promise that its
generation remains current when received.

The first acceptance returns `created`. Equivalent repeats return `existing` with
the original revision identity, complete protected inputs/tree and actor/time.
Different Component/documents use separate requests and transactions. Concurrent
acceptance uses a per-document transaction lock, a declarative unique key and
comparison with the complete winning artifact. Inconsistent candidates refuse
with `integrityConflict`; nothing is overwritten.

`get` exposes safe source/Plan, serializer, fingerprints, target and original audit.
It excludes the semantic tree, secret selectors and deferred arguments. `preview`
reads the stored artifact and returns one CLI result envelope whose `data.preview`
is the object `{document, redactedPaths}`, not an escaped JSON String. For example:

```json
{"document":{"Logging":{"Level":"Information"},"PostgreSQL":{"DefaultConnection":{"ConnectionString":"***REDACTED***"}}},"redactedPaths":["/PostgreSQL/DefaultConnection/ConnectionString"]}
```

Each complete confidential node becomes one marker. Canonical paths are sorted
and deduplicated; a confidential root has path `""`. A literal ordinary marker
creates no redacted path. Ordinary static settings, including accidentally embedded
credentials, remain visible: this is typed confidentiality, not heuristic secret
detection. JSON errors and malformed/foreign replies are rejected before stdout.
Exit codes are 0 for success, 1 for a bounded refusal, 2 for usage/invalid reply
and 3 for transport failure. Text output identifies the resolved target and prints
readable redacted JSON for preview.

Absent stored documents return `notFound` and perform no write. Preview never
materializes implicitly. Current omitted-generation reads still require the
fully applied desired selection. Explicit-generation historical reads need only
the stored generic codec/serializer, not an active renderer, formatter, SecretStore
or external Provider connection.

## Repeat, restart and restore proof

Save the successful `data.target` and `data.artifact`, especially revisionId,
fingerprint, frozenInputFingerprint, sourceRevision, planId, actor and createdAtUtc.
Record the object-valued preview. Retry and inspect with the returned generation:

```powershell
habyctl configuration materialize processor-service --environment pilot --component api --document settings --generation 1 --output json
habyctl configuration get processor-service --environment pilot --component api --document settings --generation 1 --output json
habyctl configuration preview processor-service --environment pilot --component api --document settings --generation 1 --output json
```

Verify `existing` and unchanged original identity/audit. Restart the current-build
Host against the same catalog, then repeat all three commands and compare results.
Use the protected catalog backup/restore procedure from the
[restore rehearsal](active-pilot-restore-rehearsal.md), restoring only into a
separate empty disposable database. Start a fresh current-build Host against the
restored catalog and compare the same exact revision, fingerprints, preview and
audit. Never publish the archive or protected internal payloads.

A canceled/lost materialize response does not prove rollback. Inspect or explicitly
repeat the exact returned generation; there is no automatic CLI retry. If the
generation was not received, investigate before retrying. A new omitted-generation
request may select a different target and is not an original request receipt.

## Storage and limits

The additive `MaterializeConfigurationDocuments` migration extends the current
ADR-040 catalog, including catalogs with existing Plans/generations. It adds a
generic table with an exact-generation foreign key and unique document key. It
does not backfill, reset or reinterpret any prior artifact. Earlier alpha reset
boundaries remain in force. The document API does not add generation-2 admission.

The closed versioned internal codec uses explicit tagged nodes for every JSON kind,
SecretReference and DeferredValue. Ordinary magic-looking objects remain ordinary,
even inside deferred arguments. Exact arbitrary-precision numbers, ordinal keys
and the `haby.configuration-json/v1` escaping policy are preserved. A deterministic
versioned SHA-256 fingerprint covers the complete payload, including frozen source
inputs, output/confirmed-attempt associations, semantic tree, original audit and
identities. This is consistency metadata, not tamper resistance against privileged
catalog editing. No material-derived hash is computed.

Before saving, the complete tree is limited to 64 semantic container levels, the
complete internal payload to 4 MiB UTF-8 and the complete redacted preview envelope
to 4 MiB UTF-8. Internal node wrappers have a separate 512-level codec budget.
Bounded traversal, source-length checks and capped output streams limit work before
acceptance. The configuration response channel allows 8 MiB for protobuf and the
outer CLI envelope; ordinary request/import limits and other CLI commands retain
their existing limits. Oversize/refused/corrupt requests publish no partial artifact
or stdout document. Unsupported codecs or corruption fail visibly and never repair
from current defaults.

## Evidence and remaining scope

`ConfigurationDocumentCodecTests` measures exact/one-over encoded budgets, Unicode,
escaping, 64/65 levels, exact numbers, all node kinds, magic objects and invalid
internal formats. `ConfigurationDocumentLoopbackTests` runs managed and published
Native AOT clients, exact 4 MiB preview transport, object-valued JSON, overrides,
refusal exits and malformed/foreign replies before output.

The disposable PostgreSQL suite exercises actual generated client/Host five-step
execution, api/worker, an eight-candidate synchronized race, immutable repeat,
one-time selector under a synchronized competing desired change, evidence/gate
failures, precommit cancellation/confirmed commit, renderer-free restart and real
`pg_dump`/`pg_restore`. A second synthetic queue renderer crosses the same common
composition/persistence seam, reuses one fragment for two projections, never calls
its trap formatter and detects inconsistent concurrent candidates. The selector
test relaxes only its disposable fixture's generation-1 check to simulate a future
desired value; production generation admission is unchanged.

Authorized confidential retrieval, transient material resolution/formatting,
delivery endpoints, direct Profile secret projections, Liquid/parameters, YAML/text,
batch materialization, automatic rollout/retry and document cleanup/history commands
remain separate work. CI and measured full-suite results must be reported for the
actual final commit; these test descriptions alone are not execution evidence.
