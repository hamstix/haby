# Reference configuration delivery boundary

The bounded #275-D implementation connects confirmed Reference outputs to the
existing ConfigurationProfile/Component Binding, document materialization and
static/runtime ConfigurationDelivery pipelines. Exact-source review and full
qualification are pending. The closed typed adapter in integration tests is not a
production Reference registration. Reference retirement and #275/P3 completion
remain separate work.

Provider revisions own connectivity and exact credential-use bindings. Frozen
renderer pins retain adapter defaults, effective options, their origins, original
producer/output identity and formatter identity. Precedence remains defaults <
Profile < Binding with whole top-level replacement. Delivery does not follow
current aliases, Profiles or defaults and does not call the Resource adapter to
replan, verify or render again.

Materialization saves unresolved confidential nodes and immutable metadata linking
only actually created document projections to their own original association,
attempt and exact SharedApplicationCredential ApplicationDelivery binding. Preview
redacts each whole confidential node. No material, material hash or Resource-owned
credential copy is saved in the document or dependency history.

Each request first establishes its static document grant or runtime identity and
permission, then shares one application-use authorization boundary. That boundary
checks the saved document/projection, its own currently active association,
original confirmed output/attempt and exact unrevoked use binding. Runtime identity
origin validation retains historical Reference evidence; it does not turn an
inactive peer association into a failure to authenticate an independent consumer.
Generated and Import credential evidence retains its existing checks.

`IConfigurationFormatter` returns `ConfidentialConfigurationValue`, which contains
a complete typed JSON value. Extensions must rebuild against the current contract;
ADR-033 does not retain a shim for the superseded alpha formatter interface.

An authorized request resolves each exact version once, uses the pinned pure
formatter to produce a complete typed JSON value and buffers the complete response.
Resolution, decoding, formatting or size failure returns no partial document.
Material is not cached across requests. Revocation denies subsequent requests,
including old valid grants; an already authorized in-flight request may finish.
A new Provider revision or grant cannot reactivate an old revoked artifact.

Safe operator inspection reports document/projection dependencies from immutable
created provenance and keeps their history after revocation. It does not invent
relations from a Plan, expose material or add a removal API. Provider-specific
consumer choices and output semantics remain in closed adapter contracts; the
catalog model stays Provider-neutral.

The existing internal Workload preparation may reuse the complete confirmed
Reference Resource set without new Resource effects. General Reference updates,
new Reference Resource effects in internal preparation and Reference removal stay
unsupported. Existing Provision/Import retained-document and static Profile-secret
projection policies remain applicable.
