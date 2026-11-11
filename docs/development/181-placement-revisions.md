# Immutable Placement revision administration

Issue #181 implements preparatory Placement validation, import and exact read under
ADR-047/054/055. It changes no selection, Plan, desired generation, applied state or
Kubernetes object. A Kubernetes Provider registration, credentials, namespace access
or preflight is neither required nor established by import. The durable Workload
admission guards remain in place.

## Operator commands

Use the existing operator-admin credential and selected registry/context described
in [operator access](150-operator-access-tokens.md). The context supplies the logical
Environment name. Import/get require an existing Environment and an explicit exact
Provider alias; aliases use the existing Application ID syntax (maximum 128
characters, lowercase letters/digits separated by single dots or hyphens). No live
alias binding is looked up. Future planning must establish exact Provider compatibility.

```powershell
habyctl environment create development
# Select a configured context whose environmentId is development.
habyctl --context development placement validate ./placement.json
habyctl --context development placement import ./placement.json --provider-alias cluster
habyctl --context development placement revision get standard-runtime 1 --provider-alias cluster
habyctl --context development placement revision get standard-runtime 1 --provider-alias cluster -o json
```

`validate` checks only portable content through the same closed schema and semantic
decoder used by pure planning. It requires operator authentication, but no Environment
lookup and no writes. `import` additionally checks the Environment/alias context and
atomically accepts the immutable revision. The portable JSON carries name/revision,
never Environment, alias, internal UUIDs or credentials. Names use manifest-local ID
syntax (lowercase kebab-case, maximum 63 characters); revisions are explicit positive
Int32 values. There is no latest lookup, revision update or deletion. Conditional selection
administration is documented in [issue #183](183-placement-selections.md).

Start with [this complete example](examples/181-placement.json). Optional settings
may be omitted; see the [closed field matrix](../architecture/kubernetes-workload-placement-v1alpha1.md).
`imagePullSecrets` and `tlsSecret` are names of existing objects, without resolved
material. Import neither creates nor verifies those objects.

JSON exact get includes `data.reference`, original `recordId`, `environmentRecordId`,
`technicalActorId`, `importedAtUtc`, and the complete portable `data.declaration`
object. Text output contains bounded identity/audit metadata. Extract and reuse JSON
without copying metadata into the declaration:

```powershell
$result = habyctl --context development placement revision get standard-runtime 1 --provider-alias cluster -o json | ConvertFrom-Json
$declaration = $result.data.declaration | ConvertTo-Json -Depth 64 -Compress
[IO.File]::WriteAllText((Join-Path $PWD 'extracted-placement.json'), $declaration, [Text.UTF8Encoding]::new($false))
habyctl --context development placement validate ./extracted-placement.json
habyctl --context development placement import ./extracted-placement.json --provider-alias cluster -o json
# outcome: existingEquivalent; original recordId and audit are retained.
```

Machine outcomes are `valid`, `created`, `existingEquivalent`, `found`, `notFound`,
`invalidContext`, `environmentNotFound`, `invalidDeclaration`, and
`revisionContentConflict`. The first four exit 0; refusals exit 1. Existing usage
and transport failures retain exits 2 and 3. Invalid declaration replies contain one
bounded value-free diagnostic code, never parser exceptions or submitted values.
Stored-content/driver failures return the fixed gRPC Internal detail
`Placement operation failed.` Authentication failures remain transport failures.

The decoder/client source budget is 1 MiB, maximum JSON depth 64; the Host's existing
1 MiB gRPC request budget includes protobuf scope/envelope overhead. Leave at least
1 KiB of envelope headroom for transport. Oversized requests fail boundedly at the
client, decoder or gRPC transport; none can create a partial revision. UTF-8 is
strict, without BOM, comments, trailing commas, duplicate members, unknown members
or explicit null. Bounded integer fields retain exact schema semantics; no floating
point coercion or precision loss is introduced.

## Equality, storage and catalog implications

`placement_revisions` is a distinct shared entity with UUID identity, Environment FK,
alias/name/revision, original audit and opaque adapter-owned contract/version/payload.
Its exact composite unique index arbitrates concurrent imports. Same key and equal
accepted content returns the original identity and microsecond-precision PostgreSQL
audit; different content conflicts without overwrite. Losing transactions dispose
before an independent exact read of the winner. Cancellation before commit rolls
back; once commit starts the store awaits its outcome without discarding confirmed
success because of late caller cancellation. A lost response is resolved by retrying
the same exact key/content.

The adapter's format 1 payload is the complete closed portable declaration. The
canonical writer sorts object members ordinally and emits exact accepted integers
in integer form, so property order, whitespace, string escapes and equivalent
integer spellings are nonsubstantive. Strings remain ordinal and arrays retain order.
All absent fields remain absent, including operational settings; import does not
insert effective planner defaults or equate absent fields with explicit values.
The existing typed decoder still supplies its declared inner-object defaults when
interpreting a present strategy. Changing declared presence/content is a conflict;
effective planning equality does not authorize rewriting an immutable declaration.

Exact reads are detached, no-tracking snapshots. They validate the stored contract,
format version, complete declaration, name/revision and audit before returning it.
Corrupt/unsupported data is unavailable; there is no empty/default/latest fallback,
repair, backfill or automatic reset. `KubernetesPlacementCodec.ToTyped` reconstructs
the same `KubernetesPlacement` accepted by the existing pure planner. This is a pure
integration seam, not durable admission or permission to execute it.

The additive `AddPlacementRevisions` migration preserves the current base catalog;
no new reset is required for this slice. Existing manifest model 3 compatibility
requirements still apply under ADR-033. The migration introduces no workflow SQL,
triggers, SDK types, Provider-specific columns, Profile lifecycle reuse or credentials.
EF writers reject revision modification/deletion; privileged database corruption
remains within the existing administrative trust boundary and is safely refused on read.

Application declares neutral contracts and orchestration. Kubernetes.Contracts
implements the codec and owns schema/typed interpretation; PostgreSQL depends on
Application and the injected codec interface, with no dependency on the Kubernetes
adapter. Host composes both explicitly. CLI uses only generated RPCs and generated
JSON output metadata, with no database or Kubernetes connection.

## Validation and remaining work

Focused tests cover complete/omitted-field codec round trips, strict refusal,
independent persisted arrange/act/assert contexts, exact scope isolation, first audit,
equivalent/conflicting synchronized PostgreSQL races, precommit rollback, EF write
guards, additive migration, current-build dump/restore, Host restart, authorization,
native text/JSON output, full extraction/revalidation/reimport, safe stored-data errors,
and pure planning from the exact stored declaration. Required solution, measured
PostgreSQL, published Windows/Linux Native AOT, security and dependency evidence is
reported separately at the PR's exact source SHA; this document does not claim
independent review or CI results before those runs exist.

Named/scoped selections and CAS are implemented separately by [issue #183](183-placement-selections.md).
Durable exact input capture/stages, Kubernetes
access/preflight/SDK integration, writes, publication, readiness, retirement and
corrective generations remain open. No cluster or load proof is claimed by this slice.
