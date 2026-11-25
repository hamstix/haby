# Initial downstream Reference admission (275-E)

The existing authenticated operator API admits an initial Reference Resource stage
with configuration-only Components and local Bindings. It uses the shared
ChangeSet -> Plan -> Commit -> Execute pipeline and ordinary confirmed outputs.
This extends [trusted Host composition](239-public-host-composition.md) with
additive typed catalog contributions. It does not register a production Reference
adapter, package an SDK or complete #275/P3. Retirement remains unsupported under
[ADR-063 section 25](../adr/0063-resource-capabilities-rendering-and-provider-status.md#25-retirement-not-early-declaration-removal).

## Build-time composition

A downstream Web SDK executable references `Haby.Operator.Host` and calls
`OperatorHostApplication.BuildComposedMigratedAsync`. In its callback register:

- `ResourceTypeDefinition` and `OutputContractDefinition` with closed schemas.
- A single exact Provider codec instance, its `IProviderDeclarationAdapter` and
  `IProviderPreflightHandler`; declarations and reload use that same codec.
- Exact `IDurableActionAdmissionHandler` and `IReadOnlyResourceObservationHandler`
  registrations, including `ResourceAcquisitionMode.Reference` in their key.
- `IResourceReferenceOutputContract`, or explicit
  `IResourceReferenceSharingContract` when compatible sharing is supported.
- The matching `ConfigurationRendererContract`, `ConfigurationFormatterContract`,
  `ConfigurationFormatterImplementation`, `IConfigurationRenderer`,
  `IConfigurationFormatter` and `IConfigurationResolutionBinding`.

The Host assembles these with its stock definitions into one immutable catalog
snapshot. It validates current related capability registrations before listening;
conflicting schemas, ambiguous implementations and missing required capabilities
fail startup and preserve existing disposal behavior. Stock PostgreSQL/Kubernetes
registrations remain available. Do not replace authentication, engine registries
or catalog singletons. This is trusted build composition, not a security sandbox
or validation of every superseded persisted revision.

The [verification executable](../../tools/Haby.Operator.Composition.Sample/ReferenceAdapter.cs)
is a complete small example using only public engine APIs, source-generated JSON
metadata and a closed typed Reference adapter. Its external observation is
explicitly synthetic. It is not a friend assembly and does not grant production
readiness to a service integration.

### Exact preflight identity

Implement `ProviderPreflightHandler<TConfiguration>.CheckTypedAsync` with a
`ProviderPreflightContext` argument. The engine supplies its selected persisted
Environment, Provider instance and configuration revision after exact key, codec
and reload validation. Read `context.Environment` where an existing adapter only
needs Environment ownership. Do not derive these identities from configuration
properties or resolve an alias again inside the handler.

A declared shared SystemAccess slot can resolve its pinned reference through the
ordinary `ISharedCredentialSystemAccess` registration:

```csharp
public override async ValueTask<ProviderPreflightResult> CheckTypedAsync(
    ProviderPreflightContext context, Configuration configuration,
    CancellationToken cancellationToken)
{
    using var material = await system.ResolveAsync(new SharedCredentialScope(
        context.Environment, context.Provider, context.Revision, "system-access",
        SharedCredentialUse.SystemAccess, configuration.SystemAccessCredential),
        65536, cancellationToken);
    return new(material is null
        ? ProviderPreflightOutcome.SecretUnavailable
        : ProviderPreflightOutcome.Ready);
}
```

The example only demonstrates credential resolution; a real adapter still checks
its external service. Declare the same slot as `SystemAccess` in
`IProviderDeclarationAdapter.SharedCredentialSlots`. The context carries trusted
engine-selected identity, not an authorization credential or a plugin sandbox.
The resolver independently requires the complete matching persisted use binding
and checks revocation; SystemAccess never implies ApplicationDelivery authority.
Alias changes cannot retarget an invocation already selected by the engine.

This replaces the Environment-only handler callback under ADR-033. Downstream
handlers must update their override and rebuild; there is no legacy overload.
Persisted models, RPC contracts and credential authority are unchanged, so this
callback change requires no catalog migration or reset.

## Operator intake

Create Environment and protected named SharedApplicationCredential versions, then
ApplyProvider with exact SystemAccess and ApplicationDelivery slot bindings and
set its alias. Provider revisions own endpoints and credential references; do not
copy material into Resource properties, acquisition inputs or outputs. Import the
existing Application/Profile declarations and assignments normally.

Pass an explicit complete acquisition set to the existing generated RPC:

```csharp
var request = new CreateChangeSetRequest
{
    EnvironmentId = "pilot",
    Reference = importedRevision,
    Precondition = PlanningPrecondition.ExpectedAbsence,
    Acquisitions = new ResourceAcquisitionSet()
};
request.Acquisitions.Resources.Add(new ResourceAcquisitionMember
{
    Resource = "service",
    Mode = AcquisitionMode.Reference,
    Input = new AcquisitionPayload
    {
        Format = "sample.reference-input/v1",
        Json = "{\"Identity\":\"sample-service\"}"
    }
});
var change = await client.CreateChangeSetAsync(request, cancellationToken: token);
```

Include every Resource exactly once. An absent set retains the existing Provision
path; a present empty set is deliberate and valid only for a zero-Resource
declaration. Missing/unknown member modes never default to Provision. Keep the
existing mode semantics and spellings: Provision, ImportUseAndRetain, Reference.
ImportUseAndRetain is explicitly refused through this new intake; use the existing
protected Resource Import request instead. This envelope has no credential-material
or protected-owner fields.

The limit is 256 members, 128 characters per payload format, 65,536 characters per
input JSON, 65,536 UTF-8 bytes per sharing payload, and 1 MiB for the encoded durable
set. The existing 1 MiB receive limit also applies. Typed adapter schema, complete
coverage and encoded bounds are checked before accepting a ChangeSet, including
for direct public planner callers. Invalid requests return safe typed outcomes
without partial ChangeSet acceptance or external effects. Missing exact Reference
support returns ReferenceNotSupported; there is no CREATE fallback.

Continue through CreatePlan/CheckPlan, CommitDesiredState and ExecuteGeneration.
The exact observation handler verifies before atomic association/receipt/output
confirmation; retries and restart use durable accepted state. Materialize and
deliver with the existing configuration RPCs and independent document grants.
Shared delivery still requires the own active association, original confirmed
attempt/output, frozen projection/options and exact unrevoked ApplicationDelivery
binding. Preview remains redacted; delivery buffers the whole response.

## Supported preparation and closed shapes

After the Resource-only generation is Applied, a new Application revision may add
existing supported Workloads through the ordinary operator preparation path with
ExpectedGeneration. Reference Resource membership, type, properties, deletion
policy, Provider identity/revision and acquisition/sharing inputs must remain
unchanged. The existing Plan/Check/Commit gates and confirmed original evidence
still govern reuse. SubmitGeneration prepares documents, runtime identity and
publication without new Reference verification effects; it does not qualify
rollout by merely completing preparation.

Direct initial Reference plus Workloads, general Reference UPDATE, new/removal or
reintroduction of Reference Resources, credential/Provider/input substitution,
Reference retirement and general public Workload UPDATE remain unsupported.
Administrative use revocation is immediate for subsequent authorizations; planned
removal must wait for the separate safe retirement contract. No external lifecycle
authority, automatic cleanup or old-grant revival is introduced.

The published-process acceptance case exercises stock generated RPCs, real
operator authentication, sharing, exact bindings, frozen configuration, restart,
revocation and the existing preparation bridge. Its Kubernetes HTTP backend is
synthetic and intentionally stops at Deployment; refusal or bounded request
closure there carries no rollout evidence. Existing A-D negative matrices and all
applicable final-source gates remain required. No migration is introduced.
