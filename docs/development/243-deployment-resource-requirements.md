# Deployment CPU and memory requirements

Issue [#243](https://github.com/hamstix/haby/issues/243) carries the existing typed
`resources` contract through both supported initial CREATE shapes: a Workload-only
Application, or the first Workload addition over an unchanged, fully applied
Resource baseline. Document-free and runtime-configured Deployments use the same
projection. This does not admit durable Workload UPDATE or a changing generation.

## Select and freeze requirements

An exact KubernetesPlacement revision can supply operational defaults:

```json
{
  "apiVersion": "haby.dev/v1alpha1",
  "kind": "KubernetesPlacement",
  "metadata": { "name": "standard", "revision": 1 },
  "spec": {
    "namespace": "applications",
    "resources": {
      "requests": { "cpu": "250m", "memory": "128Mi" },
      "limits": { "cpu": "1", "memory": "512Mi" }
    }
  }
}
```

A Workload can replace that entire resources object in its properties:

```json
{
  "containerName": "orders",
  "image": { "reference": "registry.example/orders:1.0.0" },
  "overrides": { "resources": { "limits": { "memory": "1Gi" } } }
}
```

This override inherits neither CPU nor requests from the Placement. Omitting
`overrides.resources` inherits the whole Placement value; omitting both leaves
requirements absent. Unknown members, explicit null and empty quantity objects
refuse. CPU remains positive decimal cores with at most three fractional digits,
or integer millicores, at most 10000 cores. Memory remains positive integer bytes
or Ki/Mi/Gi/Ti, at most 1 PiB. Each explicit request must be <= its matching limit.
No new manifest grammar, implicit resource values or generic PodSpec is introduced.

Use the existing authenticated Plan -> Check -> Commit -> submit -> wait path in
[the operator submission guide](229-operator-workload-submission.md) and
[initial Applied completion guide](237-initial-retirement-applied.md). Planning
freezes the exact selected Provider/Placement and complete Workload override.
Subsequent Placement edits do not retarget accepted inputs. Intent, receipt,
restart and readiness retain that same effective typed value, including absent
requests; execution does not look up the current Placement.

## Owned leaves and API normalization

Ownership is limited to CPU/memory leaves in `requests` and `limits` on the exact
named main container. Unspecified dimensions stay outside ownership. Other
resource dimensions, injected containers, unowned container fields and unrelated
metadata are preserved. Haby never replaces the containers list or the complete
resources map during an update.

Readback compares exact quantities, using integer/rational arithmetic rather than
raw spelling or double conversion: `250m` equals `0.25`, and `128Mi` equals
`134217728`. Kubernetes may default an omitted request to its supplied limit.
Confirmation requires that effective default while preserving the original absent
request in the accepted projection. A missing owned value, unsupported observed
shape or admission mutation of an explicit accepted value cannot confirm success.

The stateless conditional kernel may add/change/remove the owned leaves. It first
checks previously confirmed state and writes UID/resourceVersion and keyed
container-name tests. An existing unowned CPU/memory leaf cannot be claimed through
an update, even if equal. Removal deletes only previously owned leaves. Removing
an explicit request while retaining its limit permits the API-defaulted request
equal to that limit; removing the dimension completely requires absence. Restored
removed values cannot confirm a write. Manual drift conflicts before dispatch.
This kernel capability does not expose durable UPDATE admission; configured kernel
UPDATE also remains unsupported.

Lost CREATE responses retain the original intent and permit observation only.
Owner, operation marker, UID rules and complete projection still govern recovery.
Submission is distinct from readiness; resource drift also blocks readiness and
retirement-aware Applied completion. Unsupported field groups and companion
objects retain their existing refusals.

## Current alpha format and validation

Deployment intent/receipt payloads and their contracts are **v3**, including the
configured owned-object receipt format. Formats 1/2 refuse; there is no old reader,
conversion, reset or successful-proof backfill. Catalogs with superseded Deployment
intents/receipts require explicit fresh initialization under ADR-033 before
using the new executor. A reset does not remove external resources. Shared catalog
schema, portable manifest/Placement versions and durable input/fingerprint formats
remain unchanged: exact resource inputs already participate in frozen manifest and
Placement payloads, and confirmation hashes cover the extended receipt.

Fast tests cover admission/projection round trips, invalid reloads, normalization,
defaulting, drift and keyed patch preservation/removal. Existing PostgreSQL cases
exercise Plan/Check/Commit, both initial shapes, configured publication, restart,
Placement changes and Applied closure with requirements. Existing real API cases
inspect actual main-container requests/limits, defaulted requests and normalized
quantities; configured startup retains ServiceAccount/bootstrap/delivery assertions.
Inventories describe obligations; only actual current-source results prove passing.
The consumer image graph is unchanged; ordinary qualification reuses its reviewed
content-addressed binding. Full solution, PostgreSQL, API, generated-client/native,
dependency and formatting gates remain required. No load qualification or full P3
completion is claimed.
