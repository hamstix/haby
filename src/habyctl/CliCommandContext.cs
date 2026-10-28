using Grpc.Core;

namespace Haby.Cli;

internal sealed class CliCommandContext(
    IEndpointSource endpointSource,
    IRegistryClientFactory clientFactory,
    CliTextPresenter presenter,
    TextWriter error)
{
    internal CliTextPresenter Presenter => presenter;

    internal int MissingSubcommand()
    {
        error.WriteLine("A command is required. Use --help to see available commands.");
        return CliApplication.UsageError;
    }

    internal Task<int> ExecuteRevisionAsync(long number, Func<IRegistryClient, CancellationToken, Task<int>> action,
        CancellationToken cancellationToken)
    {
        if (number > 0) return ExecuteAsync(action, cancellationToken);
        error.WriteLine("Revision number must be positive.");
        return Task.FromResult(CliApplication.UsageError);
    }

    internal async Task<int> ExecuteAsync(Func<IRegistryClient, CancellationToken, Task<int>> action,
        CancellationToken cancellationToken)
    {
        var address = CliApplication.ParseEndpoint(endpointSource.GetAddress());
        if (address is null)
        {
            error.WriteLine("Set HABY_OPERATOR_URL to a loopback HTTP or HTTPS registry endpoint.");
            return CliApplication.UsageError;
        }
        try
        {
            using var client = clientFactory.Create(address,
                CliApplication.SkipTlsCertificateValidation(endpointSource.GetSkipTlsValidation()));
            return await action(client, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("Operation canceled.");
            return CliApplication.TransportError;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or RpcException or
            HttpRequestException or InvalidDataException)
        {
            error.WriteLine(CliApplication.SafeError(exception));
            return exception is RpcException or HttpRequestException
                ? CliApplication.TransportError : CliApplication.UsageError;
        }
    }
}
