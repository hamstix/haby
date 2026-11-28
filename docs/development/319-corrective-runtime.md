# Bounded corrective Workload runtime

The authenticated operator API composes existing initial Resource CREATE actions
with Workload execution. ResourcesReady authenticates only the exact original
Resource operations; Applied requires all mandatory Workload stages and retirement.
A failed initial rollout can therefore retain ResourcesReady while Applied is unset.
An explicitly empty Resource stage is supported, including document-free Workloads.

An explicit corrective ChangeSet captures the current incomplete generation's whole
inventory. ApplicationVersion and/or resolved image changes are significant inputs.
Unchanged desired input resumes its generation. Planning and accepting Commit pin
original Resource evidence, immutable Workload producer receipts, current credentials,
publications and obligations. Commit recaptures those facts under catalog locks;
stale authority or a changed inventory refuses without accepting desired state.

Confirmed owned members use UPDATE against the exact confirmed UID. Only positively
never-dispatched initial members use existing CREATE. Missing confirmed objects,
uncertainty, in-flight effects and begun retirement refuse correction; absence is
never permission to recreate or adopt. Successive corrections retain direct original
Resource evidence and accumulate publication retirement obligations. Historical reads
use immutable receipts rather than the latest mutable object projection.

The existing singleton Deployment serialization gate is shared by Commit, preparation,
submission and observation. Durable current-generation checks remain mandatory before
consequential effects and when accepting readiness. External reads may finish after a
successor wins, but cannot promote stale readiness or Applied. Retirement rechecks
fresh recipients, including terminating recipients, before revoking obsolete grants.
Administrative revocation retains its separate existing authority.

Submission and wait continue through the actual authenticated API after restart.
Bounded ordinary image/ApplicationVersion UPDATE uses the same pipeline after an
exact Applied baseline. No caller supplies receipts, effect authority or Provider
resolution. No Resource UPDATE, deletion, rotation, rebinding or new acquisition mode
is introduced.

This API boundary does not complete parent #317, CLI/operator rehearsal #320, broader
settings/configuration #321, ADR-052 or P3. Exact-source test and hosted/installed
qualification evidence is reported separately; local simulated Kubernetes observations
do not establish actual Kubernetes transport behavior.
