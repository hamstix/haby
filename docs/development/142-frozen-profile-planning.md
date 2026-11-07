# Frozen structural configuration planning

The [#144 renderer slice](144-postgresql-connection-rendering.md) extends the
structural-only baseline below with exact renderer/formatter readiness and pinned
defaults/options. Its current format and catalog boundary supersede the format
notes below. Materialization and confidential delivery remain separate.

Issue #142 connects persisted scoped assignments and the existing Profile expander
to durable Plan creation, freshness checks and atomic desired-state commit.
Use matching current-alpha Host and Native AOT CLI builds and the existing
[Profile](134-profile-operator-path.md) and [assignment](136-scoped-profile-assignments.md)
administration paths in an isolated operator environment.

## Operator procedure

1. Create the Environment, import every exact Profile revision and apply the
   Environment defaults. Import the Application and any Application/Component
   exceptions. Component apply requires its explicit validation version.
2. Save the exact Application record ID and revision number returned by import.
   Create a ChangeSet and Plan using the configured context:

   ```powershell
   habyctl changeset create <application-record-id> <revision-number> --expected-absence
   habyctl plan create <changeset-id> -o json
   habyctl plan get <plan-id>
   habyctl plan check <plan-id> -o json
   ```

3. Review the Plan and commit that exact Plan explicitly:

   ```powershell
   habyctl changeset commit <changeset-id> --plan <plan-id>
   habyctl deployment generation get <deployment-id> 1
   habyctl deployment generation execute <deployment-id> 1
   ```

Environment defaults apply automatically to each Component in the requested
ManifestRevision. Whole-Profile Use/Disable precedence remains Environment ->
Application -> Component. Empty local assignments inherit. Assignments for other
Applications, Environments or absent/renamed Components do not participate.
Neither the latest imported version nor the last-change checked revision selects
applicability. Import, assignment, planning and commit perform no Provider effects.

`configurationRefused` returns only bounded, distinct standard expansion error
codes, including `profile.secret-projections.unsupported` for effective secret
projections. It accepts no Plan or partial layout. A secret-bearing Profile that
is replaced or disabled before expansion is harmless. No secret name resolution,
SecretStore material read, renderer, formatter or option-contract validation is
performed. Ordinary replies never include templates, option values, secret
selectors or internal snapshots. There is no effective-preview/reveal command.

`planStale` means relevant scope token/absence or another exact mutable selection
changed. If the ChangeSet still has its expected absence, explicitly create and
review a new Plan for that ChangeSet. Do not retry/replan automatically. Unchanged
assignment apply retains its token and does not stale a Plan; changing and then
restoring decisions still changes the exact token. Creating a previously absent
empty assignment also stales it. `planInputUnavailable` refuses missing, corrupt,
inconsistent or unsupported saved inputs without adopting current defaults.
Reading an available saved Plan keeps its frozen structure even when it is stale.

## Frozen contract and synchronization

The typed configuration snapshot retains the exact ManifestRevision and immutable
normalized Application declaration, every relevant scope as absence or token plus
local decisions, exact referenced immutable Profile content, structural documents
and static contributions, unresolved Binding requests, Profile-plus-Binding
options and selection/layout/option provenance. Normalized Bindings remain the
only slot source. Renderer defaults and adapter compatibility remain unresolved.
An empty configuration is valid and preserves existing provisioning behavior.

The existing closed normalized/Profile/assignment codecs now live in Application
so persistence and the pure configuration fingerprint use the same explicit
encoding. The current configuration payload is format 1; the durable Plan format
and input fingerprint are version 2. Canonical ordinal object/map order and
normalized set order are deterministic; arrays and exact arbitrary-precision
numbers retain their semantics. The saved layout is checked against only its
frozen inputs on decoding, without consulting mutable catalog assignments.
Unknown/duplicate fields, inconsistent layout/content and unsupported formats
refuse. Commit copies the complete saved payload into the accepted generation.
Later assignment changes cannot mutate it or block generation execution/retry.

Planning and freshness capture all relevant scope keys with shared transaction
advisory locks in ordinal key order before reading any local assignment. Apply
acquires the matching exclusive key before reading/CAS, including Unchanged and
first insertion. Commit holds the shared keys through confirmed generation
acceptance. Therefore assignment-first produces stale refusal; commit-first pins
the complete earlier snapshot, and the writer affects future planning only.

Lock order is: planning's ChangeSet SHARE, then sorted configuration keys;
commit's ChangeSet UPDATE, deployment-key advisory lock, Plan UPDATE,
Provider-alias SHARE, then sorted configuration keys. Assignment apply takes
one configuration key and never a ChangeSet, Plan, deployment or alias lock.
This adds no generic locking framework, database workflow function/trigger or
mandatory role split. Each configuration lock wait is bounded by PostgreSQL's
transaction-local 15-second lock timeout and accepts caller cancellation.
Cancellation is checked before commit; commit confirmation uses the existing
uncancelled confirmation boundary. Timeouts/infrastructure failures do not
fabricate a stale or successful result. A lost response still requires inspection.

## Catalog compatibility and evidence

Migration `20261106153418_FreezeConfigurationPlanning` adds shared
`configuration_json` fields to Plans and generations. A previously admitted
catalog with no durable Plans/generations can migrate without losing Profile,
assignment or manifest history. A prior-format catalog containing any Plan or
generation requires an explicitly initialized fresh catalog under ADR-033.
Startup refuses before migrating it; the migration also refuses direct application
over older durable state. There is no legacy decoder, inferred backfill or
automatic reset. Reinitializing a catalog never removes external resources.
Retain the old binary/catalog backup when historical inspection is needed.
Current-build restart and real `pg_dump`/`pg_restore` remain supported.

Tests cover exact persisted scopes/revisions, saved layout/content/fingerprint
round-trips, order independence and exact numbers, original expansion refusal
codes, effective versus overridden secret projections, stale versus immutable
reads, synchronized existing-row and absent-insert races in both orders, a writer
blocked between scope reads, corrupt format/content refusal, catalog admission,
restart and real dump/restore. Two synthetic typed contribution families traverse
the actual shared capture/expansion and composition seam; they do not establish
another production Provider. Generated client -> actual Host -> PostgreSQL and
published Native AOT CLI exercise current, stale and configuration-refused replies
without secret reads. Existing real five-step execution tests change an assignment
after commit and retain generation execution/retry, uncertainty, outputs and
AppliedGeneration behavior. CLI checks new refusal metadata before any output.

[PR #143 F1 evidence](143-configuration-race-oracle.md) strengthens synchronization
with server-observed ungranted advisory locks and blocking-owner PIDs, and records
an executed counterfactual that releases protection after comparison but before commit.

Run CONTRIBUTING's complete solution restore/build/test/format workflow, EF
pending-model checks and the entire [PostgreSQL gate](postgresql-integration-tests.md),
including its unchanged measured runner and required Windows/Linux Native AOT CI.
Specific run results belong in the PR handoff; these test descriptions are not
claims of manual rendering or delivery rehearsal.

Actual Resource-output renderers, renderer defaults/option policy, formatter
execution, ConfigurationDocumentRevision materialization, preview, secret
resolution, confidential retrieval/auth, rollout and generation updates remain
separate slices.
