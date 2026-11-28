# Initial Resource-stage composition for corrective runtime

This internal #319 integration composes existing initial Resource Provision actions
with the first Workload generation. The complete immutable accepted declaration
retains both stages; adapters receive only the existing Resource graph projection.
No Resource action, lifecycle mode, output contract or ownership rule is added.
Unsupported acquisitions and combinations remain refused. Public combined initial
admission, ordinary UPDATE and corrective accepting Commit are not activated by
this slice.

## Stage evidence and authority

The existing journal exposes only the exact Resource nodes of an admitted combined
initial generation to the Resource executor. Workload action keys remain unavailable
to that executor. ResourcesReady counts those Resource nodes and their original
attempts/outputs, independently of later internal Workload journal rows. Its
confirmation never advances Applied for a Workload generation. Preparation requires
that complete checkpoint; a marked uncertain Resource blocks preparation and
corrective planning. Resource transitions retain the aggregate lock and current
accepted-generation authority checks before a new dispatch.

An incomplete first installation can therefore retain completed Resources while
Applied remains unset. Restart reads the same checkpoint and terminal Resource
attempts; it does not provision again. Existing initial Workload readiness and
retirement completion own the eventual Applied transition after all required
stages. Ordinary resource-only completion keeps its existing behavior.

Corrective planning has a separate direct-producer evidence check for this case.
It validates the current captured Resource entry against the original producer's
confirmed ResourcesReady entry, including management identity, source fingerprint,
original operation/attempt/recovery identities and output references. It neither
copies outputs to a successor nor fabricates Applied or a successful attempt.
Ordinary reuse still requires an Applied producer and complete retirement proof
for a combined Workload producer. The public corrective Commit guard remains closed.

## Validation scope and remaining integration

Focused tests use actual existing PostgreSQL Resource CREATE effects and owned
catalogs with the existing simulated Kubernetes action/readiness fixtures. Configured
and document-free variants cover unset Applied, blocked early Workload execution,
pending rollout, immutable checkpoint/attempts over restart, exact corrective
Plan/Check provenance, and final initial retirement before Applied. A dispatch-marker
interruption leaves uncertainty and no Workload/publication/checkpoint acceptance.
Existing initial-completion variants, internal Workload isolation and fixed migration
and Host execution smoke remain part of the focused regression set.

This is not a real Kubernetes rollout, authenticated corrective acceptance, complete
supersession fence or full #319 completion. Exact-SHA hosted, Native AOT and installed
qualification remain separate required evidence. Corrective per-member CREATE/UPDATE,
successive corrections, all direct-entry fencing, authenticated continuation and
retirement remain integration obligations before opening the accepting public guard.
#319, parent #317 and follow-ups #320/#321 remain open.
