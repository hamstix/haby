using Grpc.Core;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed class CliCommandContext(
    IEndpointSource endpointSource,
    IRegistryClientFactory clientFactory,
    CliTextPresenter presenter,
    CliJsonPresenter jsonPresenter,
    TextWriter output,
    TextWriter error,
    CliConfigurationService configuration,
    Func<(string? File, string? Context)> options,
    Func<bool> jsonOutput)
{
    internal int Print(ValidateReply reply) => jsonOutput() ? jsonPresenter.Print(reply) : presenter.Print(reply);
    internal int Print(ImportReply reply) => jsonOutput() ? jsonPresenter.Print(reply) : presenter.Print(reply);
    internal int Print(GetRevisionReply reply) => jsonOutput() ? jsonPresenter.Print(reply) : presenter.Print(reply);
    internal int Print(PreviewDatabaseReply reply) => jsonOutput() ? jsonPresenter.Print(reply) : presenter.Print(reply);

    internal int PrintPlanning(string command, PlanningReply reply) =>
        jsonOutput() ? jsonPresenter.PrintPlanning(command, reply) : presenter.PrintPlanning(command, reply);

    internal Task<int> ExecutePlanningAsync(
        Func<IRegistryClient, string, CancellationToken, Task<int>> action, CancellationToken cancellationToken) =>
        ExecuteResolvedAsync(action, requireContext: true, cancellationToken);

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

    internal Task<int> ExecuteAsync(Func<IRegistryClient, CancellationToken, Task<int>> action,
        CancellationToken cancellationToken) =>
        ExecuteResolvedAsync((client, _, token) => action(client, token), requireContext: false, cancellationToken);

    private async Task<int> ExecuteResolvedAsync(Func<IRegistryClient, string, CancellationToken, Task<int>> action,
        bool requireContext, CancellationToken cancellationToken)
    {
        Uri address;
        string environmentId = "";
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
                environmentId = context.EnvironmentId;
            }
            else
            {
                if (requireContext)
                {
                    error.WriteLine("Durable planning requires a selected context with an Environment ID.");
                    return CliApplication.UsageError;
                }
                endpoint = endpointSource.GetAddress();
            }
            var selectedAddress = CliApplication.ParseEndpoint(endpoint);
            if (selectedAddress is null)
            {
                error.WriteLine("Select a loopback HTTP or HTTPS registry endpoint.");
                return CliApplication.UsageError;
            }
            address = selectedAddress;
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

        try
        {
            using var client = clientFactory.Create(address,
                CliApplication.SkipTlsCertificateValidation(endpointSource.GetSkipTlsValidation()));
            return await action(client, environmentId, cancellationToken);
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
                if (jsonOutput()) jsonPresenter.PrintCurrentContext(current);
                else output.WriteLine(current ?? "(none)");
                return CliApplication.Success;
            }
            if (jsonOutput())
            {
                jsonPresenter.PrintConfiguration(config, current);
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
