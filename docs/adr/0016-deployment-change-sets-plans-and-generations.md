# ADR-016: Deployment change sets, plans and generations

- Status: Accepted
- Date: 2026-09-23

## Context

ADR-010 separates manifest import from Environment-specific desired state and
introduces ApplicationDeployment and DeploymentChangeSet. M2.1 now needs exact
side-effect-free contracts for Environment identity, deployment generations,
change intent, planning, stale detection and atomic desired-state acceptance.

These contracts must prevent a plan from being applied after its assumptions
change, preserve the exact meaning of every accepted generation and make request
retries safe. They must also support explicit removal without treating absence
from a partial release bundle or selector result as deletion.

Provider execution, PostgreSQL persistence and the durable operation engine are
outside M2.1. In-memory repositories and focused tests establish the invariants
before those integrations are added.

## Decision

### Environment has logical and persisted identity

`Environment` is operator-owned state outside `haby.json`. It contains an
aggregate-specific UUIDv7 `EnvironmentRecordId`, a logical `EnvironmentId`, a
mutable display name and an optimistic concurrency token.

`EnvironmentId` is unique within one Haby installation, uses lowercase
kebab-case, has at most 64 ASCII characters and matches
`^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$`. Values such as `production` and
`customer-staging` are valid.

A CLI profile may provide a current Environment for user convenience, but every
mutating server use case identifies the target Environment explicitly. The
server does not select an implicit default Environment.

### ApplicationDeployment is one Application in one Environment

An `ApplicationDeployment` is identified by an aggregate-specific UUIDv7 and is
unique by `EnvironmentRecordId + ApplicationRecordId`. It is created only when
the first desired state is committed, not when an Application or
ManifestRevision is imported.

It contains at least:

- `ApplicationDeploymentId`;
- `EnvironmentRecordId` and `ApplicationRecordId`;
- `DesiredGeneration`;
- optional `AppliedGeneration`;
- an optimistic `ConcurrencyToken`.

The initial desired generation is `1`. `AppliedGeneration` remains absent until
one generation has been fully and successfully realized in external systems.

`Generation` and `ConcurrencyToken` have different meanings. Generation versions
semantic effective desired state. The concurrency token versions the persisted
record and may change for reasons that do not change desired state. ChangeSet
preconditions use generation, not the persistence token.

### Every accepted generation has an immutable snapshot

Every accepted `DesiredGeneration` creates an immutable
`ApplicationDeploymentGeneration`, uniquely identified by
`ApplicationDeploymentId + Generation`. It contains:

- desired presence: `Present` or `Absent`;
- a ManifestRevision reference when presence is `Present`;
- all exact versioned planning-input references accepted for that generation;
- the DeploymentPlan and DeploymentChangeSet that accepted it;
- acceptance time and actor metadata.

`Present` requires a ManifestRevision reference. `Absent` has no target
ManifestRevision. Old generation snapshots remain addressable after later
changes. Rollback creates a new generation that may reference an earlier
ManifestRevision; it never moves the generation counter backwards.

`DesiredGeneration` increments only when a new effective desired state is
accepted. It is not an apply-call counter. Repeated apply, retry, resume,
reconciliation and changes to observed or operation state do not increment it.
If a ChangeSet member resolves to the already accepted effective desired state,
that member is a successful no-op and retains its current generation.

`AppliedGeneration` identifies the latest generation completely realized in
external infrastructure. `DesiredGeneration != AppliedGeneration`, including an
absent AppliedGeneration, means the current desired state has not yet been fully
applied.

### Planning inputs are exact and reproducible

M2.1 has ManifestRevision as its only effective desired-state input. Later
versions add typed references for operator override revisions, Profile revisions,
Provider configuration revisions, Resource export revisions and other accepted
inputs. M2.1 does not introduce a generic stringly typed planning-input reference
for entities that do not yet exist.

Provider aliases and other selectors are resolved during planning to exact,
versioned references. Changing an alias or creating a new Provider configuration
revision does not mutate an existing ApplicationDeployment or increment its
generation. A new plan must resolve the new references, and only accepting that
new effective desired state creates a generation.

Consequently, one generation has the same complete meaning today and after later
Environment changes. Its inputs are never dynamically re-resolved when the
generation is read, reconciled or audited.

### DeploymentChangeSet is immutable user intent

A DeploymentChangeSet belongs to exactly one Environment and contains an exact,
non-empty set of members. Selectors and release-bundle filters are resolved
before creation and are never persisted as dynamic membership.

Each member contains an Application reference, one change and one precondition:

```text
DeploymentChangeSetMember
|- ApplicationRef
|- Change
|  |- DeployRevision(ManifestRevisionRef)
|  `- RemoveDeployment
`- Precondition
   |- ExpectedAbsence
   `- ExpectedGeneration(n)
```

`DeployRevision` means desired presence `Present`. `RemoveDeployment` means
desired presence `Absent`. An Application omitted from the ChangeSet is not
changed.

`ApplicationRef` identifies an existing local Application by its persisted
identity. A `DeployRevision` target must belong to that Application, and every
member is evaluated only in the ChangeSet's Environment. Expected generation
values are positive 64-bit integers.

Deploying a new Application requires `ExpectedAbsence`. Updating an existing
deployment and removing a deployment require `ExpectedGeneration`. Removing an
expected-absent deployment is rejected rather than treated as an implicit no-op.
Wildcards, version ranges, `latest` and an unrestricted generation precondition
are invalid.

Member Applications are unique within one ChangeSet. Changes, targets,
preconditions and membership are immutable after creation. Rebasing intent means
creating a new ChangeSet with a new identity.

### DeploymentPlan is an immutable calculation

A DeploymentChangeSet states what the user wants. A DeploymentPlan records the
result of evaluating that intent against exact Environment and deployment inputs.
Creating either entity does not mutate desired state.

A minimal M2.1 DeploymentPlan contains:

- an aggregate-specific UUIDv7 `DeploymentPlanId`;
- its ChangeSet and Environment identities;
- exact resolved members, changes and preconditions;
- deterministic dependency order derived from cross-Application references;
- the complete typed planning-input snapshot known to M2.1;
- a versioned server-generated planning-input fingerprint;
- creation time and actor metadata.

The fingerprint accelerates comparison but is not authoritative by itself.
Current exact planning-input references are compared after a matching fingerprint
using the same collision-safe principle as manifest fingerprints.

One open ChangeSet may have several immutable Plans. A Plan may become stale
while its underlying intent remains valid, allowing another Plan to be created
for the same ChangeSet. A successful desired-state commit completes the ChangeSet;
no additional Plans or commits may then be created for it.

### ChangeSet and Plan staleness are distinct

`deployment-change-set-stale` means at least one member's deployment generation
or expected absence no longer matches the immutable intent. The result reports
all detected member mismatches, bounded and deterministically ordered, without
sensitive values. The caller creates a new ChangeSet.

`deployment-plan-stale` means the ChangeSet preconditions remain valid but one or
more exact planning inputs no longer match the Plan. The caller may create a new
Plan for the same ChangeSet.

Apply checks ChangeSet preconditions first. If they fail, it returns
`deployment-change-set-stale` even when planning inputs also changed. Only while
the ChangeSet remains valid does apply compare the Plan inputs and potentially
return `deployment-plan-stale`.

These are application and concurrency errors, not manifest validation
diagnostics. Staleness is an evaluation result and does not mutate either
immutable entity.

### Applying a Plan atomically accepts desired state

Only a specific DeploymentPlan can be applied. In one atomic transaction or
equivalent compare-and-commit boundary, Haby:

1. returns the existing commit if the ChangeSet was already committed;
2. verifies every ChangeSet generation or absence precondition;
3. rejects all changes with `deployment-change-set-stale` if any precondition
   fails;
4. resolves and compares every exact planning input;
5. rejects all changes with `deployment-plan-stale` if the Plan is no longer
   reproducible;
6. creates ApplicationDeployments for new members with generation `1`;
7. creates immutable generation snapshots only for changed effective desired
   states and advances their `DesiredGeneration` pointers;
8. records unchanged members as no-ops at their existing generation;
9. creates exactly one immutable `ChangeSetCommit` and completes the ChangeSet.

No provider side effect starts before this commit succeeds. Any failure before
the commit changes no ApplicationDeployment.

`ChangeSetCommit` identifies the committed ChangeSet and Plan, records commit
time and actor metadata and contains the resulting generation for every member.
It is the idempotency result for apply.

If a client loses the response and repeats apply for an already committed
ChangeSet, Haby returns the existing ChangeSetCommit before re-evaluating stale
preconditions. This also applies when the retry supplies another Plan belonging
to the same ChangeSet: no second commit occurs, and the result exposes the Plan
that actually committed.

### Desired and applied state advance separately

Atomic desired-state acceptance updates DesiredGeneration but never pretends that
external providers have converged. AppliedGeneration advances only after the
corresponding ApplicationDeploymentGeneration has been completely and
successfully reconciled.

M2.1 creates no provider operations, so newly accepted deployments normally have
an absent or older AppliedGeneration. Retry, resume and reconciliation semantics
are finalized with the operation model; they do not create a generation unless a
new effective desired state is separately accepted.

### Removal is explicit desired state

`RemoveDeployment` never deletes the Application or its ManifestRevisions and is
never inferred from omission in a bundle, selector or ChangeSet. It accepts a new
generation with desired presence `Absent`.

Later reconciliation uses that immutable snapshot to deconfigure managed
Workloads, Resources, access and publications under their lifecycle policies.
When complete, AppliedGeneration advances to the absent generation. The
ApplicationDeployment and generation history remain available for audit; physical
retention or archival policy is a later persistence decision.

M2.1 models and tests the transition to desired absence without executing any
deconfiguration.

## Consequences

- Every desired generation is fixed, reproducible and attributable to a Plan and
  ChangeSet.
- Deployment generations do not change because of retries, drift observation or
  persistence-only updates.
- Administrators cannot accidentally remove Applications by supplying a partial
  bundle or selector.
- Stale user intent requires a new ChangeSet, while stale calculation can be
  replanned without rewriting intent.
- Apply retries are idempotent even after the desired-state commit succeeds and
  the client loses its response.
- Multi-Application desired state is accepted atomically before provider work,
  while applied state honestly remains behind until reconciliation succeeds.
- The M2.1 implementation can prove these rules in memory before PostgreSQL and
  provider execution are introduced.

## Rejected alternatives

### Increment generation for every apply or reconciliation attempt

This would version API calls rather than desired state and make one generation
mean different things over time.

### Store only current desired fields on ApplicationDeployment

Old generations could no longer be reconstructed after aliases, profiles,
overrides or Provider configurations changed.

### Persist selectors as ChangeSet membership

Membership could change between review and apply, making impact approval and
precondition checks unreliable.

### Treat every stale condition as one error

The caller could not distinguish obsolete intent, which requires a new
ChangeSet, from obsolete calculation, which only requires another Plan.

### Infer removal from absence

Partial bundles, filters and operator mistakes could unexpectedly deconfigure an
otherwise healthy Application.

### Mutate or rebase a ChangeSet in place

Review and audit could no longer prove which intent produced a Plan or commit.

## Follow-up work

- implement the M2.1 records, discriminated unions, in-memory repositories and
  atomic compare-and-commit tests;
- define the initial planning fingerprint representation over the typed M2.1
  inputs;
- add PostgreSQL uniqueness and transaction constraints with persistence work;
- define durable provider execution, leases, retry and resume in ADR-005;
- add typed override, Profile, Provider configuration and export revision inputs
  only when their corresponding domain models are implemented.
