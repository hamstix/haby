# Operator Workload desired-state admission

Issue #227 opens the existing authenticated ChangeSet -> Plan -> Commit path for
two bounded Workload shapes. Commit accepts desired state only. Explicit submission is provided by the [issue #229 path](229-operator-workload-submission.md).
Public wait/resume, retirement and Applied completion use the shared execution pipeline. The existing Resource-only workflow remains supported. See the [corrective runtime boundary](319-corrective-runtime.md) for later supported transitions.

## Supported shapes

- Initial Workload-only Application: ExpectedAbsence, no Resources or existing
  Resource management obligations; generation 1 with null AppliedGeneration.
- First Workload addition to a fully applied Resource-only deployment: exact
  expected generation, unchanged complete Resource set with valid original reuse
  evidence, no retained/reintroduced Resource, no new Resource operations and no
  prior Workloads. AppliedGeneration remains at the previous generation.

Every declared Workload must have exact registered preparation support. The
current Kubernetes adapter retains its bounded Deployment settings; unsupported
container settings and companion objects refuse. Selected ordinary configuration
documents are planned but never materialized by admission. Static Profile secret
projections (#209) retain their separate admission rules. Bounded Workload updates and corrective generations follow the [corrective runtime boundary](319-corrective-runtime.md).

## Operator example

Prepare the exact imported Application revision, Kubernetes Provider revision and
Placement selection through the existing administration commands. Configure an
operator-admin credential and selected registry context in the usual CLI config.
The following commands use their server-resolved exact catalog identities:

```text
habyctl changeset create <application-record-id> <revision-number> --expected-absence
habyctl plan create <changeset-id>
habyctl plan get <plan-id>
habyctl plan check <plan-id>
habyctl changeset commit <changeset-id> --plan <plan-id>
habyctl changeset commit get <changeset-id>
habyctl deployment generation get <deployment-id> <generation>
```

For the supported first Workload addition, replace `--expected-absence` with
`--expected-generation <fully-applied-generation>`. Use `-o json` for generated
machine-readable output. There is no internal-mode option or caller-supplied
authority, receipt, Provider resolution or actor.

## Exactness, inspection and safety

Plan freezes the complete declaration/configuration, exact keyed Provider inputs,
Placement revisions and graph. Pure adapter descriptions show intended Workload
names, resolved image references and selected document names. They never follow
current defaults. Missing exact description support returns unavailable detail
without erasing readable immutable metadata.

Check revalidates catalog freshness and exact available handlers without writing
a replacement Plan. Commit repeats authoritative shape, membership, expiry,
closure, configuration, Provider/Placement and Resource-evidence checks under the
existing transaction/locks. Changed input requires a new Plan; accepted inputs
never relocate or rebind dynamically. Committed-ChangeSet replay returns its
original receipt, Plan, actor and timestamp, including an alternate-Plan retry.

Commit/get JSON retains `desiredStateOnly`. `workloadStage.submission: notSubmitted`
requires absence of saved dispatch evidence for that exact generation. Existing
internal action evidence produces `unknown`; this bounded projection does not
decode receipts or claim historical/live readiness. Actual AppliedGeneration is
reported beside the immutable desired snapshot and never advanced by these commands. Zero Resource nodes is not success.

Planning, checking, committing and reading perform no Kubernetes reads/writes,
TokenReview, credential material resolution, document materialization, runtime
identity/publication or Resource-ready checkpoint acceptance. Operator audit comes
from the authenticated grant. Ordinary pre-Applied configuration and public
execution guards remain in place. No schema/codec migration, reset or image build
is required. Stage execution follows in a separate slice.

## Verification

Focused fast cases cover exact description dispatch, missing/ambiguous support and
source-generated CLI output. Real PostgreSQL cases use the production Host and
generated client for both shapes, original Resource execution, restart, replay,
stale/closed/expired Plans, authorization and no-effects boundaries. The existing
mandatory published CLI venue exercises both shapes and JSON/text inspection;
both native loopback gates cover Workload output fields. Separate arrange/act/assert
contexts and existing suite/gate inventories remain mandatory. Per-source execution
results and environment limitations belong in external task/CI evidence.
