using System.CommandLine;
using System.Text;
using Haby.Cli;
using Haby.Operator.Contracts;
using Google.Protobuf;
using Haby.Cli.Composition.Sample;

Console.OutputEncoding = new UTF8Encoding(false);
var adapter = new SampleConfiguration(new CliConfigurationPaths("HABY_SAMPLE_CONFIG", "~/.haby/config.yaml"));
var builder = new HabyCliBuilder("registry-sample", "Public CLI composition example.", adapter);
var registry = new Command("registry", "Shared registry commands.");
builder.Root.Subcommands.Add(registry);
builder.MountRegistry(registry).MountConfiguration(builder.Root, adapter);
builder.AddCommand(builder.Root, new Command("sample", "A custom offline command."), (_, context, _) =>
{
    if (context.JsonOutput) context.WriteJson(new SampleResult("custom"), SampleJsonContext.Default.SampleResult);
    else context.Output.WriteLine("custom");
    return Task.FromResult(0);
});
builder.AddCommand(builder.Root, new Command("sample-registry", "A custom command using the standard authenticated generated client."), async (_, context, token) =>
{
    using var session = await context.OpenRegistryAsync(token);
    var reply = await session.Client.ValidateAsync(new EnvelopeRequest { OriginalEnvelope = ByteString.CopyFromUtf8("{}") }, cancellationToken: token);
    var outcome = reply.Valid is null ? "rejected" : "valid";
    if (context.JsonOutput) context.WriteJson(new SampleResult(outcome), SampleJsonContext.Default.SampleResult);
    else context.Output.WriteLine(outcome);
    return reply.Valid is null ? 1 : 0;
});
return await builder.Build().RunAsync(args);
