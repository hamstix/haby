# Named Provider declarations

ADR-030 defines the reference-only `Provider` declaration. The checked-in schema
is `schemas/provider.v1alpha1.json`. Properties and required secret
slots belong to explicit typed adapters, not generic catalog columns. The
PostgreSQL implementation is `haby.postgresql.database/v1alpha1`.

An example file, after creating Environment `staging` and secret `pg-admin`, is:

```json
{
  "apiVersion": "haby.dev/v1alpha1",
  "kind": "Provider",
  "metadata": { "name": "pg-main", "environment": "staging" },
  "spec": {
    "revision": 1,
    "providerKind": "postgresql/v1",
    "configurationContract": "haby.postgresql.connection/v1",
    "implementationId": "haby.postgresql.database/v1alpha1",
    "properties": {
      "host": "postgres.internal",
      "port": 5432,
      "maintenanceDatabase": "postgres",
      "administratorUserName": "registry-admin"
    },
    "secretRefs": {
      "administrator-password": { "secretName": "pg-admin", "version": 1 }
    }
  }
}
```

## Transaction and retry design

Apply owns one EF transaction. It locks the existing Environment to serialize
absent-name races, then the existing Provider row to serialize all revision
writers. Named Secret/version metadata is resolved within that transaction;
material and secret payload tables are never read. Instance, immutable snapshot,
exact associations and any identity confirmation audit are committed together.
The existing unsealed/association/seal guards remain in force. No workflow SQL,
new trigger, receipt or external effect is introduced.

Existing-number equivalence precedes monotonicity and confirmation checks.
Adapters compare typed settings, exact SecretRefs and ImplementationId. New
revision numbers increase, with gaps permitted. A new name requires revision 1.
Confirmation compares against the highest accepted revision, not alias selection;
the lock guards its baseline until commit. Endpoint change records the previous
revision, trusted technical actor and UTC time in the immutable revision row.
Equivalent replay neither requires consent again nor creates another audit.
After uncertain transport or commit failure, retry the same declaration and
confirmation arguments; its natural identity resolves the outcome.

## Compatibility

The additive migration adds a nullable name and a scoped unique index, plus
nullable confirmation audit fields. Unnamed instances stay unnamed. No payload,
target metadata, SecretRef, Plan, generation or operation is rewritten. Initial
endpoint-derived instance target values remain historical compatibility data;
exact reads validate each revision's frozen payload and associations independently
of that old endpoint. Legacy internal revision creation remains unconfirmed and
must compare against its latest revision. Operator named revisions use declaration apply.

Only exact registered kind/contract/implementation combinations can be applied
or decoded, and transitions require the new adapter's explicit compatibility
support. Invalid/ambiguous current registrations fail Host construction. Missing
registration of an adapter's reload codec also fails: the adapter must use the
same singleton codec registered for persistence reload, with matching kind,
contract, payload format and configuration CLR type. Equivalent retries compare
the resolved exact slot/SecretRef set against stored associations independently
of the adapter's typed settings comparison; slot ordering is insignificant.
Missing historical support does not block startup or metadata reads. Unsupported input
and unavailable historical decoding have distinct typed outcomes; no handler,
implementation or revision substitution is permitted.

## Outcomes and metadata

The alpha API/CLI distinguish Created, ExistingEquivalent, invalid input,
Environment mismatch/absence, unsupported configuration, unavailable decoding,
unavailable exact secret reference, revision conflict/non-increase, required
identity confirmation, stale baseline, kind conflict and storage unavailability.
Metadata reads expose only identity, name, kind, exact revision/contract/
implementation and exact secret references. They never expose properties,
snapshot JSON, endpoint or secret material.

List is a live ordinal keyset traversal: default 50, range 1..100, limit-plus-one,
PostgreSQL C collation. Named instances precede unnamed instances keyed by
`~<identity>`. The bounded `provider1:<EnvironmentRecordId>:<position>` cursor is
Provider-specific and rejects malformed/cross-Environment input. Get without a
revision reports highest accepted metadata; explicit revision get remains pinned.

```text
habyctl provider apply --file provider.json
habyctl provider get pg-main --environment staging
habyctl provider list --environment staging --page-size 50
habyctl provider revision get pg-main --revision 1 --environment staging
habyctl provider apply --file provider.json --confirm-logical-identity --expected-previous-revision 3
```

The file's Environment is mandatory. Explicit `--environment` must match it;
context supplies only registry selection for apply. Apply never connects,
preflights, changes aliases or provisions. Use [exact checks and checked alias commands](provider-check-alias.md) as separate steps.
