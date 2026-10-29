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
}
