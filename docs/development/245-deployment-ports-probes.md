# Deployment named ports and probes

Issue [#245](https://github.com/hamstix/haby/issues/245) executes the existing
closed port/probe declaration on the stable named main container in both admitted
initial Deployment shapes, with and without runtime configuration. It does not
enable durable or configured UPDATE, Services, routes or port exposure.

## Declare and freeze

Add these properties beside the existing image and optional configuration block:

```json
{
  "containerName": "orders",
  "image": { "reference": "registry.example/orders:1.0.0" },
  "ports": {
    "web": { "containerPort": 8080 },
    "metrics": { "containerPort": 9090, "protocol": "TCP" }
  },
  "probes": {
    "startup": { "httpGet": { "port": "web", "path": "/health" } },
    "liveness": { "tcpSocket": { "port": "web" } },
    "readiness": {
      "httpGet": { "port": "web", "path": "/ready", "scheme": "HTTP" },
      "periodSeconds": 5,
      "failureThreshold": 3
    }
  }
}
```

Use the existing authenticated [Plan -> Check -> Commit -> submit](229-operator-workload-submission.md)
and [wait](237-initial-retirement-applied.md) commands. Pure planning freezes port
identity, protocol and the complete probe values. Later declaration or Placement
changes cannot retarget saved intent, receipt, restart or readiness proof.
Declaration map order does not affect the frozen projection.

Ports remain a map of 0..32 names (1..15 lowercase labels beginning with a letter,
without adjacent hyphens), numbers 1..65535 and TCP/UDP protocol, default TCP.
UDP declarations are allowed; probe references must select a declared TCP port.
Each optional startup/liveness/readiness slot has exactly one HTTP/HTTPS or TCP
action. HTTP requires an absolute path and defaults scheme to HTTP. No exec/gRPC,
custom HTTP headers, host override or arbitrary Kubernetes fields are admitted.
An absent probe stays absent; Haby derives neither endpoints nor image ports.

Probe defaults are delay 0, period 10, timeout 1, success 1 and failure 3 seconds/counts.
Existing ranges remain delay 0..3600, period/timeout 1..3600 and thresholds 1..100;
startup and liveness success must be 1. The application must actually implement
its selected endpoints. Port declarations alone create no Service or routing.

## Field ownership and conditional patch

Resolve exactly one main container by stable name, preserving injected containers
and other unowned fields. Own declared port entries by ordinal name and their
supported name/number/protocol fields. Preserve differently named and unnamed
foreign entries. Duplicate observed nonempty names refuse as ambiguous. Omitted
observed protocol means TCP. Unsupported extra fields on an owned entry refuse.
New names cannot adopt an existing unowned entry, even if its values are equal.
Previously owned drift conflicts before dispatch.

Each declared probe owns its closed action and five supported integer fields.
Omitted observed API defaults compare with their effective values (including HTTP root path /); a changed
explicit value conflicts. Missing required port/path and unsupported extra fields
cannot confirm an owned probe. Previously absent slots stay unowned, including
injected probes; acquiring a present unowned slot refuses even when equal.

The stateless unconfigured kernel supports addition, changes and removal under
the existing UID/resourceVersion and keyed-container tests. Port changes address
the observed named entry; descending removals guard and delete only previously
owned entries. An action switch replaces its validated closed probe slot, leaving
one handler. Probe removal deletes only a previously owned slot. Confirmation
also requires absence of every removed owned name/slot. Foreign entries survive.
This capability does not admit durable or configured UPDATE.

Lost responses retain uncertainty. Observation checks the original exact owner,
operation, UID rules and complete projection without another write. Readback
proves configured fields, not a liveness restart. Fresh Ready/Applied still needs
actual controller generation, updated/ready/available replica status and existing
completion authority. Failed readiness prevents completion; successful probes
only establish the health checks implemented by that application.

## Current alpha boundary and evidence

Deployment intent/receipt contracts and configured owned Deployment confirmations
are **v4**. Earlier payload versions refuse. Explicitly initialize a fresh
development catalog for superseded Deployment evidence under ADR-033; there is no
compatibility reader, reset, conversion, backfill or inferred successful proof.
Catalog schema, shared persistence and portable manifest/Placement versions are
unchanged. A catalog reset does not delete external objects or Resources.

Fast tests cover frozen round trips, corrupt saved values, defaults, keyed drift,
acquisition conflicts, action change and removal. PostgreSQL fixtures cover actual
admission/Commit, both initial shapes, configured publication, restart and initial
Applied with exact probes/ports; their Kubernetes status seam is deterministic,
not real controller evidence. Existing qualification covers actual API defaults,
ports/all probe slots, conditional patch and lost-response observation. The
configured contour adds same-container controller evidence for healthy and failing
readiness, retaining bootstrap, identity, delivery, resources and cleanup checks.
Only actual current-source results establish passing evidence.

The qualification consumer binds port 8080 only in its no-argument configured
mode, after complete two-document retrieval and validation. Fixed `/health`,
`/unready` and unknown paths return empty 200, 503 and 404 responses. Four fixed
workers, a 4096-byte header limit and one-second connection work budget bound
incomplete clients. No request/configuration/credential data is exposed. The
primary marker follows successful bind; the one-shot argument/native mode remains
unchanged. This is a test endpoint, with no ongoing authorization-health promise.
The existing publisher permits this exact topic branch and publishes once for the
changed content-addressed inputs. Ordinary qualification consumes the independently
reviewed immutable binding; it never builds the consumer per test.

The ledger declares 1855 fast cases, 1172 PostgreSQL cases, 104 published-native cases per platform, 29 kernel API scenarios and 27 configured assertions. The two superseded refusal rows are retained separately and replaced by positive feature cases.

All mandatory solution, PostgreSQL, real API, configured, generated-client/native,
dependency and formatting gates remain. No timeout or inventory obligation is
weakened; inventories and local compilation are not remote execution evidence.
