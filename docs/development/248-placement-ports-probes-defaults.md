# Exact Placement port and probe defaults

Issue [#248](https://github.com/hamstix/haby/issues/248) extends the existing
[initial Deployment ports/probes path](245-deployment-ports-probes.md). One exact
KubernetesPlacement revision can provide the main container interface defaults:

```json
{
  "apiVersion": "haby.dev/v1alpha1",
  "kind": "KubernetesPlacement",
  "metadata": { "name": "standard-runtime", "revision": 2 },
  "spec": {
    "namespace": "apps",
    "ports": { "http": { "containerPort": 8080 } },
    "probes": {
      "startup": { "tcpSocket": { "port": "http" } },
      "readiness": { "httpGet": { "port": "http", "path": "/health" } }
    }
  }
}
```

A Workload with only `image` inherits both groups from its selected revision,
including an Environment default. Application/Component/explicit selection keeps
the existing precedence and exact Environment/Provider scope. The image must
actually listen on the declared endpoint; Haby does not configure its listener.

| Workload group | Effective group |
| --- | --- |
| absent | Selected Placement group, otherwise empty |
| `{}` | Empty, disabling that entire group |
| nonempty object | Complete replacement; missing keys/slots do not inherit |
| `null` or unknown member | Refused |

Ports and probes resolve independently. For example,
`"ports":{"http":{"containerPort":9090}}` replaces all inherited ports while
retaining inherited probes; `"probes":{}` disables all probes while retaining
inherited ports. `"ports":{},"probes":{}` disables both groups. Disabling only
ports while retaining a probe that needs one fails Plan. Explicit probes can
reference inherited ports, and explicit ports can satisfy inherited probes.
Portable Placement import does not require those references to be self-contained.
Every effective reference must resolve to a named TCP port during pure planning,
before any effect. The existing bounds, HTTP/HTTPS/TCP actions, defaults and
startup/liveness success threshold rules remain unchanged.

## Apply an immutable revision deliberately

1. Validate/import a new immutable Placement revision, using the existing
   [Placement operator path](181-placement-revisions.md).
2. Assign that exact revision through the existing conditional
   [selection administration](183-placement-selections.md), at the
   intended Environment/Application/Component/named scope.
3. Create an explicit new ChangeSet/Plan, Check freshness, and Commit using the
   [operator submission path](229-operator-workload-submission.md).
4. Explicitly submit/execute the accepted generation and observe readiness.

Editing a revision or assignment never mutates an old Plan or accepted generation.
The exact authored Workload, selected Placement revision and effective output
remain frozen through Commit, reload, configuration preparation, CREATE,
confirmation and readiness. A fresh Plan sees the newly selected defaults.
Existing deployed Workload changes still require the separate durable UPDATE
capability; an assignment edit is not an automatic rollout. This feature enables
neither Service/Ingress execution nor listener discovery, PodSpec templating,
automatic probe generation, adoption or deletion.

## Persistence and validation boundary

The existing normalized manifest snapshot and canonical portable Placement payload
already retain property presence. Absent groups stay absent on read/export/reimport;
explicit empty groups remain explicit. Presence participates in declaration
comparison, hashing and immutable-revision conflict detection. Effective groups
are computed only from exact captured inputs and stored in the existing v4
Deployment intent/receipt projection. No Provider-specific columns, new persisted
format, fresh-catalog requirement, reset, backfill or compatibility reader is
introduced. Strict current-format reload validation and generated JSON metadata
remain mandatory.

Feature tests cover independent inheritance/replacement, empty disabling,
portable presence roundtrips, invalid effective references and strict corrupted
projection refusal. Existing persisted fixtures exercise inherited and inline
paths, selection freshness, restoration and initial Applied. The existing real
configured consumer gate uses inherited defaults, a separate explicit empty peer,
and a replaced failing readiness group with actual API/controller assertions.
Execution results must be reported separately from these test definitions; the
unchanged pinned consumer image and existing gate inventories remain required.
