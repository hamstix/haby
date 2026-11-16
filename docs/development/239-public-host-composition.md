# Trusted public Host composition

Issue [#239](https://github.com/hamstix/haby/issues/239) adds a narrow build-time
composition boundary for a downstream executable. Reference
`src/Haby.Operator.Host/Haby.Operator.Host.csproj` from a .NET 10 Web SDK project.
This alpha boundary does not package an SDK or extend Provider catalogs, runtime
plugin loading or CLI commands.

```csharp
await using var app = await OperatorHostApplication.BuildComposedMigratedAsync(
    args, (services, configuration) =>
    {
        services.AddSingleton(provider => new MyApplicationService(
            provider.GetRequiredService<TimeProvider>(),
            configuration["MyApplication:Value"]!));
    }, cancellationToken);

app.MapGrpcService<MyGeneratedGrpcService>();
await app.RunAsync();
```

The required callback runs exactly once, after all stock registrations and before
provider construction, eager registry validation and catalog migration. It receives
the actual Host `IConfiguration`, including the normal configuration precedence.
Register services and factories in the callback; do not construct another provider.
`BuildComposed` provides the same registration boundary without migration.
The existing `Build` and `BuildMigratedAsync` entrypoints remain available; their
default cancellation-token calls remain unambiguous.

The callback is trusted additive application composition. Replacing authentication,
authorization, engine registrations or Provider/renderer catalogs is outside this
contract. It is not a plugin sandbox and does not police service descriptors.

## Configuration and publication

The downstream executable owns its configuration and content root. Select its
content directory explicitly with `--contentRoot=<absolute-directory>` when the
working directory differs. Configure the normal `Haby` catalog, TLS and endpoint
settings there or through the normal environment/command-line sources. Do not
embed credentials in source or publish production settings with the executable.

Both Web SDK executables can contribute `appsettings.json` to publication. The
[verification executable](../../tools/Haby.Operator.Composition.Sample/Haby.Operator.Composition.Sample.csproj)
excludes only the upstream Host settings from its own publish list, retaining its
own file. This addresses the observed `NETSDK1152` collision without extracting
a hosting library or suppressing duplicate-file errors globally. Its integration
scenario launches the published output from an unrelated directory with a separate
content root and checks the configured value through its generated RPC client.

## Authentication, actor and bootstrap

Map an ordinary generated gRPC service on the returned app before starting it.
The stock fallback policy protects the custom endpoint with operator-admin
authentication. Do not apply `AllowAnonymous` or disable authorization. A delivery
credential grants no operator authority; stock configuration delivery keeps its
independent document authorization. Reflection remains protected under its existing
policy and configuration.

Use the authenticated `ClaimsPrincipal` through the gRPC request's HTTP context
or `IHttpContextAccessor`. `ClaimTypes.NameIdentifier` contains the stable audit
actor derived from the authenticated operator grant UUID, not its display name.
Never accept an actor from a request or substitute it for that claim. Grant
revocation and expiry are checked on subsequent authentication; already-authorized
work may finish. No new actor or token abstraction is required.

For explicit first issuance or recovery, the downstream entrypoint can delegate
its local `operator-token-issue` command to public `LocalOperatorIssuance.RunAsync`.
It requires privileged catalog access and never starts a listener. Follow the
[operator access guide](150-operator-access-tokens.md) for secure transfer,
rotation and recovery. Ordinary Host startup does not issue tokens.

## Lifecycle and validation

The migrated factory returns only after stock validation and normal migration
succeed. It has not started a listener. The caller maps endpoints and owns
successful Start/Run/Stop/Dispose. Use `await using` to dispose async dependencies.
Construction/validation/migration failure or cancellation disposes factory-owned
resources and preserves the primary error if cleanup also throws. Synchronous
construction failure joins async cleanup off the caller's synchronization context.
The factory-loaded TLS certificate has one idempotent owner. DI owns services
created through its factories/types; caller-created singleton instances remain
the caller's responsibility. This API cannot dispose arbitrary external objects.

Publish the verification executable, then run the existing PostgreSQL integration
suite using `scripts/test-postgresql-measured.ps1 -Docker -Suite full` with a fresh
results directory and the documented native CLI prerequisites. The runner publishes
the sample and requires its absolute DLL path through `HABY_COMPOSITION_SAMPLE_PATH`.
There is no skipped or in-process fallback for the publication scenario. It proves
fresh migration, second startup/reuse, no listener before Start, generated custom
and stock RPCs, actor integrity, denial and caller-driven shutdown. Focused tests
cover callback ordering and failure ownership. The normal Linux PostgreSQL gate
archives the published bytes and records their SHA-256; existing Windows/Linux
Native AOT CLI/runtime-consumer gates remain mandatory. No Host Native AOT support
is claimed.
