# Shared Reference kernel (#275-A)

This slice implements the shared durable kernel for [#275](https://github.com/hamstix/haby/issues/275).
It does not complete that issue or register a production Reference Provider.
The closed test-only capability exercises real PostgreSQL persistence and the normal
ChangeSet → Plan → Commit → exact executor → atomic observation acceptance → safe
ordinary output path. It is not a protocol qualification or a second production adapter.

## Acquisition and confirmation

ChangeSet intake saves a canonical, bounded entry for each Resource: acquisition
mode and exact typed, non-secret input. Ordinary intake normalizes Provision once;
the PostgreSQL operator import intake still requires its complete all-Import pair.
The internal acquisition intake is not added to the operator/CLI public surface.
Exact type/mode/configuration/implementation/action registrations govern admission
and dispatch. Missing handlers, evidence contracts or schemas never fall back to CREATE.

Frozen observation groups form a complete, disjoint partition of observational
nodes, each with one mode and no Provision member. The Import pair remains one
group; Reference nodes for one Resource form one group. Intra-group dependencies
and output references refuse. Every member's cross-group prerequisites require
exact confirmed producer/attempt/output evidence, including on direct acceptance.
A complete group confirms its actual verification attempts, immutable association
identities, safe typed evidence, identity claims and ordinary outputs in one catalog
transaction. It does not confirm siblings or advance aggregate readiness prematurely.
Import uses this same service, retaining its protected credential, reservation and
correction guards. Ordinary output Complete cannot bypass group confirmation.

Only an exact registered observational handler can repeat an interrupted read-only
verification after Dispatching. Current-build retry/restart and lost commit replies
recover the same durable receipt and output identities. Fresh Provision uncertain
writes retain their existing recovery restrictions. Observation and catalog commit
are not a cross-system atomic transaction; historical verification is not health.

## Identity and authority

Reference receives Referenced provenance and ConsumptionOnly authority. It cannot
create, mutate, delete, rotate credentials, transfer ownership or acquire management
privileges. No production Reference registration, sharing, removal/reintroduction,
renderer delivery or new secret-purpose binding is enabled in this slice.
Unsupported shapes refuse; there is no partial public Reference capability.

Generic Reference claims and existing reservations share compatible Provider-row
transaction locking, including the no-existing-identity-row race. Both identity
exclusivity and one identity per stable owner/namespace are preserved. The exact
adapter validates Reference claim semantics. Reference/Import overlap refuses,
including same-owner overlap; different namespaces cannot disguise the same typed
identity. Additional Reference associations are deliberately unsupported for now.
Claims do not assert external ownership or exclusivity across independent catalogs.

Immutable receipt lookup verifies original management history, not current heads.
Current Reference consumption additionally requires the exact active association
and receipt; a Resource local ID is insufficient. No removal API is introduced.
This distinction does not apply a future Reference revocation policy to existing
Provision/Import configuration documents. PostgreSQL import keeps its Resource-owned
ApplicationLogin credentials and existing use-and-retain policy. No secret material
is copied into acquisition input, receipt, ordinary output or configuration.

## Catalog and implementation boundary

The current graph format is 7 and durable input format is 8. Acquisition intent
uses its closed source-generated format 1. The common observation receipt format
is 2; the existing receipt table now has a per-group key. These are one current
alpha model under ADR-033. Catalogs containing previous ChangeSets/Plans/generations
require explicit fresh initialization; startup refuses those histories before
applying this migration. It never resets a catalog or external Resources. The EF
migration is generated from the current model, not a runtime schema workaround.

Full #275 still requires accepted sharing semantics, explicit Environment shared
application-secret authority, typed renderer/delivery and lifecycle slices, followed
by a separately admitted real capability. System/administrator access does not gain
application delivery authority. This kernel changes no purpose, static projection,
endpoint/TLS option or retirement decision. Provider status #274, Redis, general CLI,
SDK and runtime plugin work remain separate.

## Validation

Focused fast tests cover acquisition coverage, closed codecs, exact modes, group
partitioning and dependency refusal. Real catalog tests cover mixed modes through
the actual dispatcher, all-member direct-acceptance prerequisites, partial-output
rollback, exact Provider pins after alias change, missing-handler/evidence refusal,
concurrent Reference/Import/reservation conflicts, duplicate Reference refusal,
cancellation/restart, lost catalog commit replies and historical versus active use.
They use independent arrange/act/assert contexts and own their disposable catalogs.

The historical-receipt regression is established RED before production changes.
Focused passing evidence is not full final qualification. Existing Import/Provision,
correction, credential, solution and exact-SHA final gates remain required under
CONTRIBUTING and ADR-061. The coordinator owns the installed final qualification;
this slice neither merges itself nor closes the #275 umbrella.
