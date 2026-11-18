# Bounded Deployment update planning foundation

Issue #252 implements an internal Plan/Check/Commit foundation under
[ADR-058](../adr/0058-bounded-deployment-image-updates.md). It does not enable
operator UPDATE or Kubernetes UPDATE execution. P3 remains incomplete.

## Admission and frozen authority

Friend composition may plan a successor of a fully Applied managed Workload
generation. AppliedGeneration must equal DesiredGeneration, with the original
accepted Plan/commit, complete initial retirement proof, confirmed Deployment
receipt, managed identity and original Resource evidence available. Planning
performs catalog reads only; it neither contacts Kubernetes nor resolves secrets.

Resolve current Provider, naming, Placement and configuration selections normally,
then compare against the baseline. The bounded delta permits ApplicationVersion
and the typed image declaration to change. Component, Resource and Workload sets,
all nonimage declarations, exact Provider/Placement revisions, Profile selections,
scope tokens/absence, renderer/default pins and frozen secret references must
remain unchanged. Object-map order is ignored; meaningful fields are preserved.
Source ManifestRevision identity alone does not constitute a Profile change.
Every Workload must have exactly one supported adapter and original confirmation.

The graph carries a separate immutable update inventory, without an initial
retirement inventory. It records:

- Baseline deployment/generation, original Plan/ChangeSet/fingerprint and completion
  checkpoint/hash, and the exact target generation.
- Each stable managed target, original operation/attempt/observation, adapter-owned
  old/new image, UID, original intent and receipt, and selected preparation documents.
- Retained owned-object payloads with their original generation/operation, retained
  runtime bindings and lifecycle tokens, and the complete publication inventory.
- For a successor, exact old publication/receipt references that require eventual
  retirement. Target document IDs and permissions are future preparation results.

Target generation and document names bind future materialization/publication
obligations. Before later configured UPDATE execution, those exact documents must
be prepared for that generation and authorized for the retained identity. Planning
does not create revisions, publish rights or authorize retirement. Old permissions
remain available until later rollout and old-recipient observation prove retirement
safe, including terminating Pods. Static grants have their independent lifecycle.

Resource reuse continues through the existing adapter compatibility and original
proof paths. Original credentials, outputs, attempts and generation provenance are
not relabeled. Original identity and Deployment receipts likewise remain unchanged.

Semantic input comparison preserves accepted deep Profile documents using the
snapshot codec's depth limit of 160. Image-only changes compare equal; meaningful
nested configuration changes refuse update admission. Malformed JSON fails closed.

## Check, Commit and request semantics

Internal Check revalidates the baseline, immutable evidence and current selected
inputs. Saved reads retain their frozen inputs after selection changes. Commit uses
the existing ChangeSet/aggregate locks and desired/applied CAS, repeats admission
and inventory capture in its transaction, and writes no accepted generation or
receipt on refusal. Competing successors cannot both commit from one baseline.

The existing ADR-045 semantics apply. A new ManifestRevision is a new target even
when its effective image is unchanged; the adapter records `UnchangedImage`, not
a fictional transition. Exact already accepted inputs are a no-op at their existing
generation, with no preparation/retirement obligations and retained publications.
Retrying the same accepted ChangeSet returns its original commit receipt.

## Public execution remains closed

Production operator admission returns typed `UnsupportedShape` for a Workload
update. Operator Check/Commit cannot promote an internally saved successor Plan;
operator replay cannot expose an internally committed update. No RPC switch,
public test hook or new friend assembly is introduced. Existing initial CREATE
and Resource-only supported paths retain their boundaries.

The original internal execution authority requires an initial retirement inventory.
An update inventory cannot enter CREATE, initial empty-retirement completion or
Applied advancement. [Issue #257](257-durable-deployment-update-submission.md) adds
a distinct internal preparation/publication/conditional submission capability,
without opening normal operator UPDATE or completing its lifecycle.

## Catalog boundary

This changes the current graph format to version 5 and durable input storage to
version 6. A fresh development catalog is required under
[ADR-033](../adr/0033-development-compatibility-and-catalog-resets.md). Startup
rejects older durable formats explicitly. There is no reset, history backfill or
silent external-resource adoption. Resetting a catalog does not remove its external
Resources. New inventory uses the existing neutral graph payload and generated
JSON metadata; no Provider-specific table or column is added.

## Acceptance and remaining work

| Criterion | Implementation and evidence venue |
| --- | --- |
| A | Typed Kubernetes delta; exact baseline, target generation, original receipt/UID, Resource reuse and future preparation/retirement inventory. |
| B | Pure input/delta negatives and PostgreSQL missing/corrupt/foreign/revoked proof refusal. |
| C | Explicit reference/tag/ApplicationVersion image tests and persisted version-only/equivalent request tests. |
| D | Persisted reload and restart; selected Profile/Placement changes do not rewrite saved Plans and refuse acceptance. |
| E | Competing Commit CAS and changed Applied baseline tests; execution/initial completion guards remain closed. |
| F | Operator Plan/Check/Commit/replay refusal through the production admission implementation; existing initial/runtime suites remain mandatory. |
| G | Shared graph storage, generated metadata, bounded inventory and explicit fresh-catalog requirement. |

Test inventories are not passing execution evidence. Exact commands, results and
current-SHA CI belong in the author report and pull request.

Issue #257 supplies internal generation-specific preparation/publication, stable
identity authorized-use evidence and conditional same-UID configured UPDATE.
Remaining ADR-058 slices must implement new readiness, old-recipient observation,
bounded permission retirement and Applied
completion, ADR-052 forward correction, and the real integrated rehearsal. No
operational UPDATE promise follows from this planning foundation.
