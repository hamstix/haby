using Grpc.Core;

namespace Haby.Cli;

internal sealed class CliCommandContext(
    IEndpointSource endpointSource,
    IRegistryClientFactory clientFactory,
    CliTextPresenter presenter,
    TextWriter output,
    TextWriter error,
    CliConfigurationService configuration,
    Func<(string? File, string? Context)> options)
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
        try
        {
            var selected = options();
            var config = await configuration.LoadAsync(selected.File, cancellationToken);
            var contextName = selected.Context ?? config.CurrentContext;
            string? endpoint;
            if (contextName is not null)
            {
                if (!config.Contexts.TryGetValue(contextName, out var context) ||
                    !config.Instances.TryGetValue(context.Instance, out var instance))
                    throw new InvalidDataException("Invalid CLI configuration.");
                endpoint = instance.Endpoint;
            }
            else endpoint = endpointSource.GetAddress();
            var address = CliApplication.ParseEndpoint(endpoint);
            if (address is null)
            {
                error.WriteLine("Select a loopback HTTP or HTTPS registry endpoint.");
                return CliApplication.UsageError;
            }
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

    internal async Task<int> ViewConfigurationAsync(bool currentOnly, CancellationToken cancellationToken)
    {
        try
        {
            var selected = options();
            var config = await configuration.LoadAsync(selected.File, cancellationToken);
            var current = selected.Context ?? config.CurrentContext;
            if (current is not null && !config.Contexts.ContainsKey(current))
                throw new InvalidDataException("Invalid CLI configuration.");
            if (currentOnly)
            {
                output.WriteLine(current ?? "(none)");
                return CliApplication.Success;
            }
            output.WriteLine("formatVersion: 1");
            output.WriteLine($"currentContext: {current ?? "(none)"}");
            output.WriteLine("instances:");
            foreach (var (name, instance) in config.Instances)
                output.WriteLine($"  {name}: {instance.Endpoint}");
            output.WriteLine("contexts:");
            foreach (var (name, context) in config.Contexts)
                output.WriteLine($"  {name}: instance={context.Instance}, environmentId={context.EnvironmentId}");
            return CliApplication.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("Operation canceled.");
            return CliApplication.TransportError;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error.WriteLine("Invalid CLI configuration.");
            return CliApplication.UsageError;
        }
    }
}
