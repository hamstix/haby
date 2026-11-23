# ADR-063: Resource acquisition capabilities, typed rendering and Provider status

- Status: Accepted
- Date: 2026-11-19
- Implementation: This decision alone enables no runtime capability; shared foundation in [#267](https://github.com/hamstix/haby/issues/267), Reference in [#275](https://github.com/hamstix/haby/issues/275), bounded Provider status in [#274](https://github.com/hamstix/haby/issues/274)
- Amends: [ADR-062](0062-existing-resource-import-and-attachment.md); extends ADR-035, ADR-040, ADR-042 and ADR-057
- Order: Preserve #263 -> TLS #261 -> PostgreSQL import #267 priority; no runtime or implementation release is granted here

## Context and current implementation boundary

A Resource describes an application capability, not necessarily an external
object Haby creates. Existing services must be usable without fabricated CREATE
operations, generated credentials or lifecycle ownership. Reusable configuration
needs typed consumer choices rather than copied endpoints/passwords or patches to
unrelated configuration. Provider connectivity also needs a bounded observation
that is weaker than management preflight.

Current code registers outputs against durable producer actions and pins renderer
contracts, implementations, options and producers. Some credential consumers are
specific to generated credentials. Profile/Binding options already compose typed
values, while Provider revision preflight can test management privileges. This
ADR defines required extensions; it does not claim upstream Reference or Provider
status support already exists. Existing `provider check` and alias-switch
preflight must not be relabeled as the new status operation.

The bounded [#275-A/B kernel](../development/275-reference-kernel.md) implements
per-Resource acquisition, frozen homogeneous observation groups, common Import/Reference
acceptance and separate historical/active association proof. Explicit non-secret
sharing uses the same generic claim relation and Provider-row locks: each peer
has its own frozen adapter-authorized decision, identity and verification evidence,
and both exact registered contracts must accept compatibility. Default and
exclusive conflicts remain. Its Reference adapter and sharing registration are
test-only. Production Reference intake, shared-secret purpose/delivery authority,
renderer delivery, removal/reintroduction and Provider status remain outside this
implementation. This boundary does not claim umbrella #275 complete.

## Decisions

### 1. Resource capability without external creation

A Resource may require verification and durable execution without provisioning an
external object. Such execution confirms an association and outputs; it must not
pretend a CREATE occurred. Keep acquisition mode, provenance, usable capability
and lifecycle authority separate.

### 2. Explicit acquisition modes

- Provision creates external state under its admitted creation/lifecycle policy.
- Import verifies an existing external entity and records Imported provenance,
  retaining ADR-062's bounded use-and-retain policy.
- Reference verifies an existing service/capability for consumption without
  acquiring lifecycle authority over the external service.

These are semantic names; JSON property names and mode spellings are not frozen
by this ADR. No mode is inferred from object existence or a failed CREATE.

### 3. Per-type support and shared core

Each exact ResourceType/mode combination needs an explicitly registered handler
and closed, versioned typed schema. Unsupported combinations fail with a typed
unsupported result before effects; ImportNotSupported remains the import refusal.
There is no CREATE fallback. Shared core stores generic mode, authority and result
concepts; adapter-specific identities, policy, options and evidence remain in
validated versioned payloads. No Provider-specific shared tables, columns, enums,
branching, EAV or arbitrary JSON property bags are introduced.

### 4. One durable acceptance pipeline

Use ChangeSet -> Plan -> Commit -> Execute for all admitted modes. Freeze exact
Environment/Application/Resource identities, Provider revision, typed inputs and
SecretVersion references. Reference is not a configuration bypass around this
pipeline. Perform observational verification before atomic catalog confirmation
of the association, receipt and confirmed outputs. External observation and the
catalog transaction are not cross-system atomic.

Receipts truthfully identify the actual verification/attachment operation, never
external CREATE or generated-secret activity. Durable retries, cancellation,
uncertain/lost responses and restart must recover accepted state and outputs,
reuse exact credential references and enforce authority after reload. No usable
output escapes an incomplete association. Failed staging cleans only owned
internal records, not the external service. Changed input conflicts or requires
an explicit new accepted operation; historical verification is not perpetual
health. Relevant attachment/consumer use checks may revalidate observationally.

### 5. Explicit shared Reference semantics

Multiple Resources or Applications may Reference the same service only where the
adapter explicitly supports that sharing. Reference does not reserve exclusive
adoption by default. Record each association and its authorized consumers without
pretending global ownership. Do not use Reference to bypass ADR-062's existing
Import identity/reservation conflicts or its database/login compatibility policy.

### 6. Lifecycle boundary

Reference grants no create, mutation, delete, rotation, ownership transfer or
privilege-management authority. Removing it removes only the internal association
and corresponding delivery authorization under the accepted lifecycle; it does
not delete external state. A declaration change never implicitly grants stronger
authority. Any future transition to management needs its own explicit capability,
accepted intent and evidence. Import use-and-retain restrictions remain enforced
in subsequent update/removal/retry paths.

### 7. Provider, Resource and Profile responsibilities

The Provider revision owns the selected endpoint, `systemSecretRef` and explicit
application-deliverable `applicationSecretRef` with its delivery-purpose binding.
Resource inputs select the Provider/capability and express resource constraints;
they do not repeat the application Secret reference per Resource. Profiles define configuration
shape and consumer defaults; Component Bindings choose authorized consumer
options. Do not copy endpoint or password values into Profiles/Bindings or derive
an Environment endpoint from consumer configuration. Shared generic persistence
retains exact identities while adapters own endpoint semantics.

### 8. Explicit application delivery authority

System/administrator access and application delivery are distinct authorities.
Reference consumes the Provider revision's Environment-scoped exact
`applicationSecretRef` for shared application use only with explicit delivery-purpose
authorization and the normal Binding/consumer authorization. A selected system credential never becomes
application-deliverable implicitly. The same physical secret need not be copied
merely to represent both authorized uses; explicit purpose/reference bindings
must distinguish those uses and exact versions. This is not permission to change
an existing immutable purpose or send a ProviderAdministrator reference through
an application renderer. The shared implementation must enforce the accepted
purpose model explicitly, not add a secret-delivery workaround.

ADR-062 PostgreSQL import still binds the existing application credential to a
Resource-owned ApplicationLogin Secret with Imported provenance. It is not an
Environment Configuration secret or generated material. Static ADR-057 secret
projections retain their separate Configuration-purpose boundary. Never store
material in manifests, Plans, receipts, ordinary outputs, audit or diagnostics;
resolve it privately only for the authorized operation/delivery.

### 9. Reusable typed consumption and outputs

Equivalent capabilities expose the same typed consumption/output contract across
Provision, Import and Reference, independently of provenance and authority.
Generalize CREATE/GeneratedCredential-only assumptions where necessary; do not
invent fake producers or a second import/output framework to satisfy them.
Adapters publish validated safe values and exact SecretRefs, not plaintext.
Producer/receipt validation still proves the actual accepted mode-specific
operation. Renderer/formatter contracts and output versions remain explicit,
registered and reusable; no reflection discovery or runtime plugin loader.

### 10. Frozen typed option precedence

Apply renderer defaults < exact Profile binding-rule `options` < Component
Binding `options`. Each level replaces a whole top-level property; there is no
recursive merge. Validate each supplied layer and the effective result against
the closed adapter contract. Freeze effective normalized values, their origins,
renderer/formatter implementation identities and exact Profile revision in Plan.
Delivery must not select latest, reapply defaults or reinterpret changed options.
Binding options cannot patch arbitrary output or static JSON.

### 11. Adapter-owned consumer options

A private Redis illustration uses `dbNum`: a nonnegative mathematical integer
within the adapter's explicitly supported deployment-mode policy, and
`connectTimeout`: a positive integer number of milliseconds. The private adapter
must declare exact bounds/defaults and validate exact numeric values without a
lossy double conversion. Unknown properties and explicit null are rejected.
Manifest camelCase options may render PascalCase application properties under an
explicit typed mapping. Binding cannot override Provider endpoint, password,
username or mandatory TLS policy. These names illustrate a private closed contract,
not a new upstream Redis implementation or registered schema.

### 12. Consumer choice is not isolation or status proof

`dbNum` selects a consumer database; it does not prove isolation, widen privileges
or grant management authority. Reference attachment and separate consumer checks
may independently validate effective options, including dbNum, through bounded
observational checks without writing keys. Such checks are not Provider status
and must not be removed merely because status omits dbNum verification.

### 13. Product-neutral composition illustration

The following fragments describe a private adapter contract, not importable
current upstream declarations. Type/renderer IDs are illustrative and UNREGISTERED;
future acquisition/purpose JSON spelling is not frozen. Endpoint and secret names
are placeholders, not operational configuration. The two named Secret references
illustrate distinct authorities, not a requirement for two physical values; an
explicitly authorized shared physical secret is allowed without implicit
system-to-application delivery authority.

Provider/reference inputs conceptually bind one selected Provider revision:

```json
{
  "provider": {
    "name": "shared-cache",
    "endpoint": { "host": "cache.example.invalid", "port": 6380, "tls": true },
    "systemSecretRef": { "name": "cache-system", "version": 1 },
    "applicationSecretRef": { "name": "shared-cache-application", "version": 1 },
    "deliveryPurpose": "explicit-shared-application-login"
  },
  "resource": {
    "providerRef": "shared-cache",
    "type": "illustrative.redis-access/v1",
    "acquisitionMode": "Reference"
  }
}
```

An exact reusable Profile contains the binding rule below and is assigned once
to the Environment. Only Components declaring the `redis` Binding consume this
rule; assignment does not inject a Redis dependency into every Component.

```json
{
  "bindingRules": {
    "redis": {
      "slot": "redis",
      "rendererRef": "illustrative.redis-connection/v1",
      "options": { "dbNum": 1, "connectTimeout": 5000 },
      "documentProjections": [{ "documentRef": "appsettings", "path": "/Redis" }]
    }
  }
}
```

The `i18n` Component declares a Binding to that Resource with configuration slot
`redis` and `options: { "dbNum": 0 }`. The effective projection has this shape:

```json
{
  "Redis": {
    "EndPoints": [{ "Host": "cache.example.invalid", "Port": 6380 }],
    "DbNum": 0,
    "ConnectTimeout": 5000,
    "Password": "<resolved application-deliverable secret; redacted here>"
  }
}
```

The password marker is explanatory, not a literal stored value or diagnostic
output. Real material is resolved privately through the authorized delivery path.
Remove the old static `/Redis` contribution when adopting this binding rule;
existing path/collision rules stay unchanged. The renderer does not silently
replace an occupied contribution or merge connection credentials from static JSON.

### 14. Independent static configuration

StaticContributions and secretProjections remain available for independent
logging/telemetry configuration such as Serilog or OTEL under ADR-057. Binding
options do not patch that unrelated static tree. A configuration-only contribution
does not need a fabricated Resource/Provider/Binding; conversely, Reference
Resource outputs must not bypass durable confirmation through static projection.

### 15. Separate API-only Provider status

The future singular CLI surface is:

```text
habyctl provider status --environment staging
habyctl provider status <provider-name> --environment staging
```

The first checks all selected Providers in the Environment; the second checks one.
Ordinary operator-admin authorization is required. The server resolves each
current alias-bound Provider revision once and runs bounded authenticated probes.
An unbound Provider returns an explicit not-configured/not-selected state; do not
guess the highest or latest revision. Existing explicit exact-revision selection
may remain where supported; this ADR adds no new CLI option contract. Return safe name, alias, exact revision,
check time and typed outcome. All-provider results retain each observed result;
text and JSON presentations have consistent semantics and failures produce a
nonzero exit. CLI performs only API calls, never direct external/DB probes.
This decision does not claim these commands are implemented today.

### 16. Narrow login-only observation

Status checks host/port reachability, required TLS and authentication using the
selected SYSTEM credential only. It proves no management privileges, CREATE
ability, Resource health/existence, dbNum usability, isolation, workload health or
application delivery authorization. It changes no alias, generation or Resource.
It cannot mint or substitute an alias-switch preflight receipt. Existing stronger
preflight keeps its own policy and exact checks; status is a point-in-time
observation that does not authorize later effects.

### 17. Bounded execution and safe outcomes

The server configures concurrency and deadlines, propagates cancellation and
returns typed unsupported/handler-unavailable, timeout, transport/TLS and
authentication outcomes as appropriate. A timeout is not a bad-password verdict.
One Provider failure must not hide the others. Never expose secret material,
connection strings, raw driver exceptions or unsafe endpoint diagnostics in
text/JSON/logs. An implementation must define bounded result enumeration and
partial-result semantics without claiming unchecked Providers are healthy.

### 18. Shared delivery scope and sequencing

Extend ADR-062/#267's shared foundation rather than build another import framework.
Preserve #263, then TLS #261, then the bounded PostgreSQL import priority and its
complete accepted database-owner/schema/ACL/credential acceptance requirements.
Track shared Reference capability in
[#275](https://github.com/hamstix/haby/issues/275) and Provider status in
[#274](https://github.com/hamstix/haby/issues/274); this decision alone releases
neither implementation nor runtime work and changes no current delivery order.

Upstream Redis implementation is excluded. A private adapter may develop codecs,
closed options, renderer and login probe against these boundaries now, but cannot
fabricate durable CREATE or deliver an Environment administrator secret as a
workaround before the shared Reference capability ships. Public examples remain
product-neutral and unregistered. No Kubernetes adoption, generic SDK project or
new runtime plugin loading is introduced.

## Required implementation evidence

Future bounded implementation slices must prove exact per-type/mode admission
and unsupported refusal before effects; no CREATE fallback or external mutation
for Reference/use-and-retain; shared Reference without weakening Import conflicts;
atomic internal confirmation/output publication; concurrent retry, lost-response
and restart behavior; exact SecretVersion/purpose/delivery authorization; unchanged
fresh PostgreSQL creation and the complete ADR-062 import fixture.

Renderer tests cover all precedence layers, top-level replacement, frozen options
and origins, malformed/unknown/null/exact-number rejection, protected endpoint/TLS
fields, output equivalence across admitted modes, configuration collisions and
application delivery. Status tests independently cover login-only scope,
privilege-independent success, bounded fan-out/deadlines/partial results, typed
unsupported/timeouts, cancellation and redaction, without accepting status as
preflight or consumer/dbNum evidence. Real protocol tests belong to separately
authorized implementation qualification, not this documentation change.
