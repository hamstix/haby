# Kubernetes Workload and Placement v1alpha1

Issue #177, ADR-046/047/049/054/055. This matrix precedes implementation.
Contracts and pure resolution do not enable durable Workload execution. Every
Kubernetes effect, Placement administration, publication, readiness, retirement
and corrective generation remains a later slice.

## Closed field matrix

Every object below is closed: unknown members and explicit null refuse. Optional
means absent, never null. Arrays are bounded, unique by their stated identity,
and replace as a whole; composite overrides also replace as a whole. Defaults
apply only in pure planning, never during extension-property normalization.
All integer numbers use exact mathematical semantics (1, 1.0 and 1e0 agree).
Strings are ordinal, without trimming, expansion, templating or conversion.

| Location | Fields, types and bounds | Required, defaults and semantics |
| --- | --- | --- |
| Core Workload | type, providerRef, placementRef, properties | Exact type kubernetes.workload/v1alpha1; providerRef required; placementRef optional local ID; obsolete profile refuses. |
| Workload properties | image, containerName, nameOverride, ports, env, probes, volumes, mounts, routes, configuration, overrides | image required; containerName defaults app; other fields optional; one Deployment/main container only. |
| image composed | name (1..128 lower-case repository path), tag (1..128), tagFrom | name and exactly one of tag or tagFrom=applicationVersion. No repository alias. |
| image complete | reference (1..512) | Exclusive with name/tag/tagFrom; explicit registry/path and tag or sha256 digest required. |
| containerName | DNS label, 1..63 | Independent of image and object name; future managed rename refuses. |
| nameOverride | DNS subdomain, 1..253 | Replaces applicationId-componentId-workloadId completely; no truncation/hash/random fallback. Validate every selected kind separately. |
| ports | map, 0..32 named entries | Names 1..15 lower-case labels starting with a letter, without adjacent hyphens; each port has containerPort integer 1..65535 and optional protocol TCP/UDP (default TCP). |
| env | map, 0..128 variable names | Name [A-Za-z_][A-Za-z0-9_]*, max 128. Each value is exactly one of value string (0..4096), configMapKey or secretKey. |
| key reference | name, key, optional | Existing DNS-subdomain name; key 1..253 [A-Za-z0-9._-], excluding dot, dot-dot and every leading double dot; optional boolean defaults false. No ownership or envFrom. |
| probes | startup, liveness, readiness | Optional individual probe objects. Absent probes stay absent. |
| probe | httpGet or tcpSocket; initialDelaySeconds, periodSeconds, timeoutSeconds, successThreshold, failureThreshold | Exactly one action. Delay 0..3600 default 0; period 1..3600 default 10; timeout 1..3600 default 1; success 1..100 default 1; failure 1..100 default 3. Startup/liveness success must be 1. |
| httpGet | port, path, scheme | Named declared TCP port; absolute path 1..2048; scheme HTTP/HTTPS default HTTP. No arbitrary headers. |
| tcpSocket | port | Named declared TCP port. |
| volumes | map, 0..32 DNS-label entries | Each exactly one of configMap, secret, persistentVolumeClaim. |
| ConfigMap/Secret volume | name, optional | Existing DNS-subdomain name; optional defaults false. Entire referenced object mounted; no ownership. |
| PVC volume | name, readOnly | Existing claim; readOnly defaults false. |
| mounts | array, 0..64 | volume, mountPath required; readOnly defaults true for ConfigMap/Secret, false for PVC; optional subPath. Unique mountPath. |
| mount paths | mountPath 1..1024; subPath 1..1024 | Absolute normalized Linux mountPath other than /; relative subPath; no empty/dot/dot-dot segments, backslash or NUL. ConfigMap/Secret cannot be writable; readOnly PVC cannot be mounted writable. Haby runtime-token mounts are not user declarations. |
| routes | array, 0..32 | Unique (hostLabel,path); hostLabel DNS label; path absolute 1..2048, forbidding //, /./, /../, %2f/%2F and trailing /. or /..; port references declared TCP port; pathType Exact/Prefix defaults Prefix. |
| configuration | documents | Required nonempty unique array 1..128 Component-local IDs. No body, bearer credential, merge order or implicit all-documents. Absent block means no Haby retrieval/binding/token projection. |
| overrides | replicas, strategy, minReadySeconds, rolloutSeconds, resources, service, headlessService, ingress | Only these operational fields; no Provider, namespace, image, env or nodeSelector override. |
| Placement envelope | apiVersion, kind, metadata, spec | apiVersion haby.dev/v1alpha1, kind KubernetesPlacement; metadata name local ID and revision positive integer <=2147483647. Portable content carries no internal UUID. |
| Placement spec | namespace, registry, repositoryPrefix, nodeSelector, imagePullSecrets, replicas, strategy, minReadySeconds, rolloutSeconds, resources, service, headlessService, ingress | namespace required DNS label; no default namespace. Exact Environment/Provider binding supplied separately to pure resolution. |
| registry | address string 1..253 | Required for composed image only. Unambiguous lower-case dotted hostname/IP, localhost, or single-label host with explicit decimal port 1..65535. No URL scheme, path, credentials or implicit Docker Hub. |
| repositoryPrefix | string 1..128 | Lower-case repository path, optional; no leading/trailing slash. |
| nodeSelector | map 0..64 | Kubernetes qualified label keys (optional DNS prefix plus name); values 0..63 alphanumeric with interior -_.; absent empty. |
| imagePullSecrets | array 0..32 | Unique existing DNS-subdomain names; absent empty. |
| replicas | integer 1..10000 | Default 1; zero is not suspend. |
| strategy | type; maxSurge; maxUnavailable | Whole object replacement. Type RollingUpdate/Recreate required. RollingUpdate defaults each bound to 25%; Recreate forbids both. |
| surge/unavailable | integer 0..10000 or percent string 0%..100% | Both cannot be zero. No floating coercion. |
| minReadySeconds | integer 0..3600 | Default 0. |
| rolloutSeconds | integer 1..86400 | Default 600; observation budget, distinct from client/API deadlines. |
| resources | requests, limits | Optional whole objects; each optional cpu and memory strings. At least one field in each present object; requests <= limits for each shared dimension. |
| cpu | decimal cores or integer millicores | Positive <=10000 cores; at most three fractional core digits; no exponent or rounding. |
| memory | positive integer with optional Ki/Mi/Gi/Ti suffix | <= 1 PiB exact bytes; no decimal/double rounding. |
| service/headlessService | enabled boolean | Default disabled independently; if enabled use all declared named ports, service port equals containerPort. Ordinary Service is ClusterIP. Both may coexist. |
| ingress | enabled, className, domain, tlsSecret | enabled required; disabled forbids other fields. Enabled requires className DNS-subdomain and domain DNS hostname; optional existing TLS Secret. Whole policy replacement. Requires ordinary Service and nonempty valid routes; host = hostLabel.domain. No wildcard/template/annotations/shared routing. |

## Exact selection, identities and results

Explicit supplied selections are scoped by Environment and Provider alias. Choose
placementRef, otherwise Component assignment, Application assignment, Environment
default. Each selects one exact immutable revision, never partial merge or latest.
Foreign/missing revisions and Provider/configuration/implementation mismatches
refuse. Placement is namespace authority; an optional explicit expected namespace
constraint may reject incompatibility but never supplies a fallback namespace.

The common exact Provider set keys each declared Resource or Component/Workload
input separately and retains alias, Environment, Provider instance/configuration,
implementation and opaque typed versioned naming/Placement data. It cannot query
live aliases. Pure Workload output is detached immutable data, not a persisted
executable DeploymentPlan. Durable multi-Provider storage/Commit/runner integration
is the next seam; existing pilot admission remains closed.

Deployment and Ingress names are DNS subdomains <=253. Service names are DNS labels
starting with a letter, <=63; headless adds -headless. Runtime ServiceAccount adds
-runtime (DNS subdomain <=253) only when configuration retrieval is requested.
Collisions use exact Provider instance, namespace, API group, kind and name; different
kinds may intentionally share a name. No external object existence is inferred.

Image path components use [a-z0-9]+ separated by single dot, underscore or hyphen;
slashes separate components. Tags use [A-Za-z0-9_][A-Za-z0-9_.-]{0,127}.
Complete references require an unambiguous registry token (localhost, a dotted host/IP, or a host with an explicit valid port), then /path:tag or /path@sha256:
followed by exactly 64 lowercase hex digits. Repository path is at most 255 characters, excluding registry, tag and digest. No tag-plus-digest alternative. Ambiguous forms such as team/orders:v1 and registry=registry refuse instead of silently selecting Docker Hub.
A static tag is a fixed string, not immutable bytes; a digest identifies content.
The actual selected ApplicationVersion is passed explicitly: an accepted version
such as 1.0+build can still be invalid as an image tag and must refuse.

For image.name orders, Placement registry registry.customer.local, prefix product
and actual ApplicationVersion 1.4.2, the result is
registry.customer.local/product/orders:1.4.2. containerName orders-service remains
orders-service regardless of that image or Placement. Complete references bypass
registry/prefix entirely. All reference checks use the normalized Application and
Component, after the real registered-schema parsing and snapshot round trip.

## Implemented API and dependency direction

`Haby.Kubernetes.Contracts` references Application (and transitively Domain and
Manifest). No common project references this adapter; Host explicitly registers
`KubernetesContract.Definition`. No Kubernetes client package is installed.
The two schemas in `schemas/` are embedded in the adapter. Import validates the
registered closed Workload schema. `KubernetesContract.Decode` interprets the
normalized extension value and checks semantic image/probe/mount/resource rules;
`DecodePlacement` validates portable Placement bytes. Both use generated JSON
metadata and explicit typed mapping, without reflection discovery or fallback.

`ExactProviderResolutionSet` requires one entry for every declared Resource and
Workload. Its `ExactAdapterInput` carries versioned immutable opaque non-secret
configuration data; the common layer never casts it to a Kubernetes payload.
`PlacementInputs` copies explicit revision/assignment collections. No persistence,
latest resolution, live aliases or endpoint lookups occur. `KubernetesWorkloadPlanner.Resolve`
requires the actual selected `ApplicationVersion` separately and returns immutable
`EffectiveWorkload` values, exact Provider/Placement inputs and proposed names.
Typed declarations are available for every field in the matrix. Reference checks
that require Component documents occur here; schema acceptance alone is not an
executable capability or permission to contact a cluster.

The pure API preserves absent extension fields in the normalized declaration.
Effective operational settings are separate values. Document order is retained
for declaration equality under ADR-015 but introduces no merge precedence; every
selected document is independently mandatory. Placement default arrays are not
an override surface: only the enumerated operational overrides are accepted.

## Catalog boundary

Current normalized model and snapshot format are **3**. Version 2 is refused, not
converted. Startup refuses catalogs containing older manifest model/format rows
before applying further migrations. Exact reads and nested configuration snapshot
reads also refuse unsupported versions. A current-format Workload containing the
obsolete `profile` member refuses even if version fields were edited. There is
no alias, silent field loss, automatic reset, backfill or external-resource cleanup.
Explicitly initialize a fresh development catalog under ADR-033; plan separately
how already-created databases, roles and external resources are retained.

The core snapshot uses generated JsonObject type metadata with explicit typed
mapping. Resource export accessProfile and Configuration Profiles are unchanged.
Historical diagnostic dictionaries remain their original content, including old
idKind text; the current parser emits workload-placement for placementRef.

## Completion boundary and next slices

Portable import, schema/typed interpretation, normalization and snapshot retention,
explicit-context pure selection/default/override/image/name resolution are implemented.
The production durable planner still refuses all Workloads and unsupported multiple
Resource Provider aliases before desired-state acceptance. It does not consume the
new pure result. No AppliedGeneration, Resource provenance, credential, naming or
configuration-delivery guard is relaxed.

Next implement Placement persistence/administration, then durable exact Provider
sets and stage checkpoints with explicit freshness. Those slices must deliberately
integrate this result into Plan storage/Commit/runner. Kubernetes access/preflight,
conditional objects, runtime identity/publication/TokenReview, 150-client proof,
submission/readiness, retirement and corrective generations remain unimplemented.
Managed container/object renames and namespace/Provider relocation remain future
runtime refusals; the pure layer has no owned-object inventory and grants no adoption.

Object-name constraints follow the [Kubernetes name categories](https://kubernetes.io/docs/concepts/overview/working-with-objects/names/); Service validation does not depend on the optional relaxed-name feature gate.

## Review corrections and schema parity

Both portable schemas and typed decoding reject ambiguous registry tokens,
adjacent hyphens in named ports, prohibited selector-key prefixes, invalid
Exact/Prefix Ingress path sequences and repository paths longer than 255.
Positive controls retain localhost, explicit-port single-label registries,
`.config` selector keys, ordinary dotted keys and 255-character tagged/digest
paths. HTTP probes retain their separate path rule; Ingress-specific segment and
encoded-slash restrictions do not apply to them. Unknown fields still refuse.

These lexical restrictions follow upstream [registry recognition](https://github.com/distribution/reference/blob/main/normalize.go),
[repository-path bounds](https://github.com/distribution/reference/blob/main/reference.go),
[port/selector-key validation](https://github.com/kubernetes/apimachinery/blob/master/pkg/util/validation/validation.go)
and [Ingress path validation](https://github.com/kubernetes/kubernetes/blob/master/pkg/apis/networking/validation/validation.go).
They are pure contract checks, not real API or registry-access evidence.
