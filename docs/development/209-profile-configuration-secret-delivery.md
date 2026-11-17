# Issue #209: Profile configuration secrets through authorized delivery

## Goal and sequence

Implement accepted ADR-057 immediately after #239. A shared Profile can insert exact named configuration-secret versions into ordinary JSON, including logging options beneath existing arrays, and deliver them only to an authorized document recipient. This replaces the former contract-decision-pending status; declaration support alone is not completion.

## Authority and dependencies

Read AGENTS.md, CONTRIBUTING.md, ROADMAP P3, ADR-028/029/033/035/038/039/040/041/042/043/050 and ADR-057. ADR-057 explicitly amends secret-projection array traversal and supersedes the execution deferral. Rebase the implementation on main after #239 and accepted documentation publication. Coordinate the dedicated code/review pair through the P3 coordinator.

## Included scope

- Environment-owned Configuration purpose using shared named secret storage, existing backend and retry receipts.
- Operator RPC/CLI creation with --purpose configuration; preserve administrative default, hidden input and exact stdin. Scope/name uniqueness remains unchanged and purpose participates in conflicts.
- Metadata-only exact name/version/purpose resolution during planning and frozen internal SecretRef through snapshots, restart, materialization and delivery.
- Ordinary static contribution secret projections, strict string/JSON formats, existing-array traversal and missing-object-property insertion per ADR-057.
- No-material preview with whole-node redaction and final redactedPaths.
- Authorized all-or-nothing delivery through existing static grants AND Kubernetes runtime identity, with existing bounds.
- Exact-version reference retention and current operator documentation/examples; secret deletion and receipt CleanUp remain deferred.

## Excluded scope

No Provider-specific tables/columns, fake Resources/Bindings, new Provider/OTEL formatter, secret concatenation, recursive resolution, arbitrary JSON patch, array insertion/replacement, latest lookup, automatic rotation, new revoke API, per-secret operator ACL, new encrypted backend, SDK or workload lifecycle redesign. Preserve other projection kinds. Do not change CI infrastructure or run a new load campaign.

## Acceptance criteria

1. Creation with Configuration purpose supports equivalent retries, lost-response RequestId retries and restart; different material or purpose at an existing Environment/name conflicts. Existing administrative creation behavior remains correct.
2. Portable Profile import/read does not require or read a secret. Planning rejects missing/wrong Environment/purpose/version using metadata only, accepts only Configuration and freezes an exact reference.
3. A Profile revision containing /WriteTo/0/Args/lokiPassword works against an existing array. Reject leading-zero/noncanonical/out-of-range indices, '-', scalar traversal, array creation, element replacement, occupied leaves and equal/overlapping projections. Preserve missing object parents and outer contribution collision semantics.
4. Snapshot/materialization/restart preserve exact references and formats without material. A later assignment change or new same-purpose secret does not retarget accepted documents. Update codecs coherently; document any explicit fresh-catalog requirement without automatic reset.
5. Preview performs zero material reads, redacts the entire inserted JSON/string node and reports final paths, including nested array indices.
6. Valid static and runtime recipients obtain complete correctly escaped JSON with exact strings/types/numbers. Unauthorized/revoked/wrong-document recipients cannot cause secret material reads. Generated ApplicationLogin Binding delivery and existing document routes remain correct.
7. Malformed UTF-8, duplicate JSON members, comments, trailing input, missing versions, wrong purpose/scope and every supported size/depth failure return safe bounded failures and NO partial document or secret-bearing diagnostic. Count expanded size/escaping/depth, not only input size.
8. Preserve exact-version references in accepted Plans, generations and documents and existing immutable/restrict relationships. Secret/version deletion and receipt CleanUp remain deferred under ADR-029; add no deletion policy or API. Prove versions remain resolvable across restart, assignment replacement and new-name replacement, and test applicable persisted restrictions. Future cleanup must account for these dependencies.
9. Public generic examples demonstrate logging-array and telemetry-header use, metadata-only Profile discovery, new-name replacement and plaintext-at-rest limitations without private names/endpoints/material.
10. No reflection serialization fallback; source-generated DTO roots, provider-neutral models and Native AOT CLI remain intact.

## Operator sequence and limits

Create a named Configuration secret using hidden input or exact `--stdin` bytes:

```text
habyctl secret create logging-password --environment staging --purpose configuration
habyctl secret get logging-password --environment staging -o json
```

Then import the [generic logging and telemetry Profile](../examples/configuration-profiles/secret-projection.json)
and assign its exact revision. Import/read is portable and metadata-only; planning
requires each referenced name and numbered version in the target Environment.
Use a new secret name, Profile revision and configuration-only generation for
replacement. Existing Plans, generations and documents keep their frozen refs.
The plaintext catalog and its backups still contain secret bytes; this feature
does not encrypt storage. Only authorized exact-document recipients receive
material, and preview redacts whole inserted nodes.

The current-alpha planning format is v3 and document codec/fingerprint is v2.
An existing catalog from the prior
format needs an explicit fresh catalog under ADR-033; Haby never resets it
automatically. Secret/version deletion and receipt cleanup remain deferred.

## Required validation

Follow current CONTRIBUTING suite routing/isolation and mandatory CI checks on the exact final SHA. Add fast parser/composition/format/bounds tests, real PostgreSQL persistence/retry/retention/restart tests, authorization integration tests for both delivery routes using existing fixtures, and published Native AOT CLI coverage of the new purpose. Use canary material to assert absence from durable payloads, logs, diagnostics and preview. Instrument/assert material reads rather than inferring safety from a successful response. Reproduce regressions before production fixes. Run focused checks first, then all applicable required validation, formatting and git diff --check. No new image rebuild or infrastructure qualification campaign solely for this slice; any genuinely affected mandatory qualification remains required.

## Code-agent result

One scoped draft PR with Closes #209, exact full HEAD, implementation and contract mapping, tests/evidence, catalog implications and unperformed checks. Do not merge. Raise any conflict with accepted ADRs to the coordinator/architect instead of weakening authorization, exact pinning or failure atomicity.

## Independent review task

Review the actual final full SHA against this card and ADR-057, not the author's summary. Read-only except incremental external report at D:/Projects/hamstix/tmp/code-reviews/PR<number>_profile-configuration-secret-delivery.md. Inspect production dispatch for BOTH delivery paths and actual assertions/CI.

Prioritize: no secret reads before authorization; purpose isolation and retry conflicts; exact frozen references across restart; array amendment staying limited to secretProjections; complete-node redaction and whole-document failure; strict UTF-8/JSON/exact numbers and expanded bounds; retention/CleanUp; provider-neutral persistence and source-generated/AOT compatibility. Check ordinary/generated-login delivery regressions. Report actionable P0-P3 with file/line, violated contract, reproducible scenario and smallest correction. Distinguish defects from unverified risks and static/CI/local evidence. No comments, edits, pushes or merge. Finish with exact SHA, blockers or none and unperformed checks; architect merge approval is separate.
