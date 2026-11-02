# Generated credential outputs and local operation completion

Subsequent [issue #114](114-postgresql-restrict.md) adds confirmed database output
and opt-in PUBLIC restriction, with a new fresh-catalog baseline. The full pair
remains unavailable in production. The scope below records this earlier slice.

Issue #110 implements the output/local-execution part of
[ADR-034](../adr/0034-application-credentials-and-postgresql-role-policy.md).
It builds on [stable generated slots](108-generated-credential-slots.md).
Production admission still accepts only the existing standalone database Resource.

## Frozen references and supported payload

An immutable graph node carries zero or more
`OperationOutputRef(producerOperationId, outputContract, outputKey)` selectors.
They are defensively copied, canonically ordered, serialized with generated JSON
metadata in graph format 2, and included in graph equality and Plan fingerprints.
Publication never changes the accepted graph, input or fingerprint.

The only supported output is `GeneratedCredentialOutput(SecretRef)`, contract
`haby.generated-credential/v1`, key `password`. Its strict generated codec stores
only exact Secret/SecretVersion identities. Unknown contracts, unknown fields and
corrupt payloads refuse safely. The common `operation_outputs` envelope uses the
write-once key (deployment, generation, producer operation, output contract, key).
It contains no database, login, role, plaintext, verifier or material hash.

The producer action is exactly `haby.secret.ensure-generated/v1` and its input
format is `haby.generated-slot/v1`: stable Resource owner,
`slotId=application-password`, `generationProfile=random-password/v1`.
Selectors must target that producer and exact output, with a direct dependency,
matching Resource, Environment and exact admission references. Graph acceptance,
Plan/Check/Commit and committed-state consumption validate these rules.
Cross-Resource consumption is deliberately unsupported in this slice.

## Internal execution and trust boundary

`GeneratedCredentialAction` is an exact build-time handler parameterized by the
Resource's real admission key. It is not a secret Provider. It calls
`IGeneratedCredentialSlots`; it never generates material independently or opens
an external Provider connection. Production Host registers no new graph shape
or external effect. Test-only composition admits a queue Resource, plans a local
producer and a typed test consumer, and uses the real dispatcher and persistence.
This proves the common seam without implementing RabbitMQ or a second production
Provider. Planning still uses one exact global Provider selection.

`IGeneratedCredentialOperations` is a trusted internal catalog boundary, not
a public authorization API. The implementation derives the owner from the
committed deployment, accepted generation and pinned manifest Resource. It
checks the frozen input and Resource contract against that state. Consumption
starts from the containing consumer key and accepted selector, never a supplied
owner or latest generation. It requires exact-generation producer success and
a closed successful local attempt, the exact write-once output, the same valid
slot/creation audit, and owner-scoped exact SecretStore resolution. All temporary
material is disposed. Metadata lookup alone cannot authorize another owner's
SecretRef, nor can an output row without producer success authorize consumption.
No new RPC, CLI result, confidential endpoint, rendering or reveal is introduced.

## Atomicity, concurrency and retry

Slot acceptance commits independently before publication. One subsequent catalog
transaction conditionally owns the producer's Preparing/current-attempt row,
publishes the output, records Succeeded, closes the exact attempt and advances
AppliedGeneration only if all required nodes succeeded. Failure rolls back all
those changes together. A missing/failed/NeedsAttention sibling prevents aggregate
completion. Publication never implies downstream external success.

The local action never enters Dispatching. The generic external journal marker
and success APIs refuse this producer, so they cannot complete it without output.
An interrupted or failed publication leaves safe local Preparing work; a later
explicit invocation retains/abandons its prior attempt and reuses the same slot,
SecretRef and bytes. It does not rotate, reset, compensate or acquire external
effect replay/recovery semantics. Successful exact execution returns saved
success without another attempt, output or creation audit.

Conditional state/attempt updates, unique output keys, foreign keys, EF immutable
output guards and the transaction arbitrate competing calls. Identical accepted
publication is idempotent; conflicting references are refused without overwriting.
An ambiguous commit is independently reread after transaction disposal. Confirmed
success returns success; unavailable/negative confirmation returns a bounded
retryable result. A later explicit call observes the retained state.
Cancellation is honored before commit begins; confirmation after that boundary
uses an uncancelled token.

No new SQL functions, procedures or triggers are installed. No mandatory runtime
database-role split is required. C#/EF-managed writes and declarative integrity
are the guarantee; privileged direct SQL maintenance can corrupt state and is
outside it. Existing merged guards remain unchanged.

## Catalog initialization and validation

This schema/graph revision requires an explicitly initialized fresh catalog under
[ADR-033](../adr/0033-development-compatibility-and-catalog-resets.md).
Startup rejects earlier migration sets and never automatically resets data.
There is no alpha compatibility reader, migration backfill or automatic external
resource cleanup. Current-build restart and real pg_dump/pg_restore preserve
slots, exact references/material, output associations and journal/attempt state.

The Application tests cover strict codecs, frozen selectors, fingerprints and
invalid producer/dependency/type/owner boundaries. The disposable PostgreSQL
suite exercises real Plan/Check/Commit and exact dispatch, rollback at publication,
attempt and aggregate boundaries, retained slot retry, independent attempts,
ambiguous commit rereads, cross-owner/environment/deployment/generation refusal,
conflicting publication and real restore. Persisted arrange/act/assert use
independent contexts. A later-generation storage fixture does not enable
generation-2 admission.

Remaining work is adapter-owned PostgreSQL pair admission and the separately
approved restrict/login/SCRAM/ownership actions and typed adapter outputs.
Combined multi-Provider admission, new recovery, credential rotation/cleanup,
confidential configuration delivery and public authorization remain deferred.
