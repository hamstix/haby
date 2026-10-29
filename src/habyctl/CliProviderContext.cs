using Google.Protobuf;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed partial class CliCommandContext
{
    internal int PrintProvider(string command, ProviderReply reply) =>
        jsonOutput() ? jsonPresenter.PrintProvider(command, reply) : presenter.PrintProvider(command, reply);
    internal int PrintProviders(ListProvidersReply reply) =>
        jsonOutput() ? jsonPresenter.PrintProviders(reply) : presenter.PrintProviders(reply);

    internal Task<int> ApplyProviderAsync(string file, string? environment, bool confirm, long? previous, CancellationToken token)
    {
        if (confirm ? previous is null or <= 0 : previous is not null)
        {
            error.WriteLine("Identity confirmation requires --expected-previous-revision with a positive revision.");
            return Task.FromResult(CliApplication.UsageError);
        }
        return ExecuteAsync(async (client, ct) =>
        {
            var request = new ApplyProviderRequest
            { Declaration = ByteString.CopyFrom(await CliApplication.ReadEnvelopeAsync(file, ct)), ConfirmLogicalIdentity = confirm };
            if (environment is not null) request.Environment = environment;
            if (previous is { } baseline) request.ExpectedPreviousRevision = baseline;
            // The server validates the mandatory file Environment. Local context never supplies it.
            return PrintProvider("provider apply", await client.ApplyProviderAsync(request, ct));
        }, token);
    }
    internal int PrintProviderAccess(string command, ProviderAccessReply reply) =>
        jsonOutput() ? jsonPresenter.PrintProviderAccess(command, reply) : presenter.PrintProviderAccess(command, reply);

    internal Task<int> SetProviderAliasAsync(string? environment, string alias, string? provider, string? instance, long revision,
        bool absent, string? previousProvider, string? previousInstance, long? previousRevision, CancellationToken token)
    {
        if ((provider is null) == (instance is null) || revision <= 0 ||
            (absent ? previousProvider is not null || previousInstance is not null || previousRevision is not null :
                (previousProvider is null) == (previousInstance is null) || previousRevision is null or <= 0))
        {
            error.WriteLine("Select one Provider and exactly one expectation: --expect-absent or --expected-provider/--expected-instance-id with --expected-revision.");
            return Task.FromResult(CliApplication.UsageError);
        }
        return ExecuteSecretAsync(environment, async (client, env, ct) =>
        {
            var target = new ProviderSelector { Revision = revision };
            if (provider is not null) target.Name = provider; else target.InstanceId = instance!;
            var request = new SetProviderAliasRequest { Environment = env, Alias = alias, Target = target };
            if (absent) request.ExpectedAbsent = true;
            else
            {
                request.Expected = new ProviderSelector { Revision = previousRevision!.Value };
                if (previousProvider is not null) request.Expected.Name = previousProvider; else request.Expected.InstanceId = previousInstance!;
            }
            return PrintProviderAccess("provider alias set", await client.SetProviderAliasAsync(request, ct));
        }, token);
    }
}
