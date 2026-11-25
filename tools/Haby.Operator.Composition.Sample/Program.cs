using Haby.TestDiagnostics;
using System.Security.Claims;
using Grpc.Core;
using Haby.Operator.Composition.Sample.Wire;
using Haby.Operator.Host;
using Haby.Operator.Composition.Sample;

if (args.FirstOrDefault() == "operator-token-issue")
    return await LocalOperatorIssuance.RunAsync(args[1..], Console.Out, Console.Error);

await using var app = await OperatorHostApplication.BuildComposedMigratedAsync(args, (services, configuration) =>
{
    ReferenceAdapter.Register(services);
    if (CompositionExceptionDiagnostics.FromEnvironment() is { } diagnostics)
        services.AddLogging(logging => logging.AddProvider(diagnostics).AddFilter<CompositionExceptionDiagnostics>((category, level) => true));
    services.AddSingleton(provider => new SampleValue(
        configuration["Sample:Value"] ?? throw new InvalidOperationException("Sample:Value is required."),
        provider.GetRequiredService<TimeProvider>()));
});
app.MapGrpcService<ProbeService>();
// This verification executable exposes only process lifecycle coordination, never a network fault API.
Console.WriteLine("composition-built");
if (await Console.In.ReadLineAsync() != "start") return 2;
await app.StartAsync();
Console.WriteLine("composition-started");
if (await Console.In.ReadLineAsync() != "stop") return 2;
await app.StopAsync();
Console.WriteLine("composition-stopped");
return 0;

internal sealed record SampleValue(string Value, TimeProvider Clock);
internal sealed class ProbeService(SampleValue value) : CompositionProbe.CompositionProbeBase
{
    public override Task<ReadReply> Read(ReadRequest request, ServerCallContext context) => Task.FromResult(new ReadReply
    {
        Value = value.Value,
        Actor = context.GetHttpContext().User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            throw new InvalidOperationException("Authenticated operator actor is required."),
        UtcYear = value.Clock.GetUtcNow().Year
    });
}
