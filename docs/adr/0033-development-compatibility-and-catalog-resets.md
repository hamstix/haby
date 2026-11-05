# ADR-033: Development compatibility and catalog resets

- Status: Accepted
- Date: 2026-11-02
- Scope: Active development before supported, durable production use

## Context

Haby is undergoing active development and operator rehearsals. Keeping readers,
facades, wire aliases and migrations for every intermediate alpha design adds
maintenance work before those contracts have reached stable use. The maintainer
does not require preservation of development-era catalogs, Plans, operations or
audit records across breaking revisions.

## Decision

### Prefer one current model

During this stage, application/domain types, Provider interfaces, internal alpha
gRPC contracts, CLI adapters, persisted formats and schemas may change together.
Do not add compatibility layers solely to preserve superseded development builds.
Remove obsolete readers, mappings, aliases and compatibility-only tests when the
corresponding behavior is replaced. Keep useful current-behavior tests.

This includes the evolving haby.dev/v1alpha1 Application manifest: accepted alpha
changes may update the schema, DTOs and normalized model without changing
apiVersion, as clarified by ADR-038. Do not create an alpha2/legacy adapter solely
to preserve an intermediate alpha form. The explicit supported compatibility
baseline below, rather than alpha publication, establishes that future obligation.

A breaking slice may require a fresh Haby catalog. Its PR and runbook must state
that requirement explicitly. No automatic data migration, backfill, old-reader
support, upgrade/downgrade matrix, retained protobuf number or historical JSON
spelling is required solely for a previous development build.

Development EF migrations may be replaced or consolidated when necessary for a
coherent new schema. Validate fresh schema creation and current-model consistency.
Do not rewrite unrelated migrations just to tidy their history. Once a slice
declares reset-required, old database compatibility is not an acceptance gate.

Provider-neutral interfaces and typed action inputs may be redesigned fully
within the accepted functional scope. This does not authorize new external
effects, privileges or secret-handling policies that still need decisions.

### Preserve runtime correctness

This decision removes cross-build preservation requirements, not the integrity
of a running installation. Within the current supported schema and build:

- accepted ManifestRevisions, Plans, generations and recovery decisions retain
  their required immutability;
- exact Provider/configuration/SecretRef inputs remain pinned;
- attempts and audit are attributable and retries preserve successful work;
- restart and current-build backup/restore retain state without replaying effects;
- dispatch uncertainty, explicit recovery consent, transaction atomicity and
  aggregate AppliedGeneration rules remain mandatory;
- secret redaction, source-generated metadata and Native AOT CLI remain required.

Do not regenerate uncertain external effects or erase their current journal to
avoid recovery. Valid administrative database edits remain within ADR-023's
accepted trust boundary.

### Reset is explicit and scoped

No startup code may silently delete or recreate an existing catalog. Reject an
unsupported catalog safely and explain the reset/reinitialization requirement.
Implementation agents may reset their own disposable test catalogs; this policy
does not authorize deleting a developer's persistent catalog or external Provider
resources. A catalog reset does not roll back databases, roles or other effects
already created outside Haby. Operators must explicitly decide how retained test
resources are handled before another rehearsal.

### Establish compatibility when stable use begins

Before declaring supported durable production use, explicitly establish the
supported API/schema baseline, upgrade and retention policy. From that point,
changes affecting supported installations need the agreed compatibility and
migration guarantees. Do not infer that baseline from an intermediate alpha PR.

## Superseded requirements and immediate application

For development-era cross-build compatibility, this ADR takes precedence over
earlier requirements to retain old Plan/operation/audit storage, legacy
ResourceId + Create addressing, reason strings/protobuf numbers/JSON spellings,
old Provider payload readers and merged EF migration chains. Earlier ADR runtime
and functional rules continue to apply. Completed issues describe their original
delivery, not an obligation to carry those compatibility layers forever.

Issue #104 must implement a clean canonical operation graph and journal model.
It may require a fresh catalog, replace the current operation addressing and
update Host/CLI together. It must not add a legacy-to-node bridge or old-history
migration solely to preserve the previous pilot. Recovery audit/results must
describe node resolution and actual aggregate completion directly; there is no
requirement to preserve an earlier receipt shape that conflates the two.
