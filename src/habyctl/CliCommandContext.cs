using Grpc.Core;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed partial class CliCommandContext(
    IEndpointSource endpointSource,
    IRegistryClientFactory clientFactory,
    CliTextPresenter presenter,
    CliJsonPresenter jsonPresenter,
    TextWriter output,
    TextWriter error,
    CliConfigurationService configuration,
    Func<(string? File, string? Context)> options,
    Func<bool> jsonOutput, ISecretTerminal secretTerminal,
    ICliContextResolver? contextResolver = null, ICliConfigurationAdapter? configAdapter = null,
    Func<CliInvocationContext>? invocationContext = null, Func<string>? commandPrefix = null)
{
    internal int PrintEnvironment(string command, EnvironmentReply reply) =>
        jsonOutput() ? jsonPresenter.PrintEnvironment(command, reply) : presenter.PrintEnvironment(command, reply);
    internal int PrintEnvironments(ListEnvironmentsReply reply) =>
        jsonOutput() ? jsonPresenter.PrintEnvironments(reply) : presenter.PrintEnvironments(reply);

    internal int Print(ValidateReply reply) => jsonOutput() ? jsonPresenter.Print(reply) : presenter.Print(reply);
    internal int Print(ImportReply reply) => jsonOutput() ? jsonPresenter.Print(reply) : presenter.Print(reply);
    internal int Print(GetRevisionReply reply) => jsonOutput() ? jsonPresenter.Print(reply) : presenter.Print(reply);
    internal int Print(PreviewDatabaseReply reply) => jsonOutput() ? jsonPresenter.Print(reply) : presenter.Print(reply);

    internal int PrintPlanning(string command, PlanningReply reply) =>
        jsonOutput() ? jsonPresenter.PrintPlanning(command, reply) : presenter.PrintPlanning(command, reply);

    internal int PrintCommit(string command, CommitReply reply) =>
        jsonOutput() ? jsonPresenter.PrintCommit(command, reply) : presenter.PrintCommit(command, reply);

    internal async Task<int> SubmitGenerationAsync(GenerationRequest request, CancellationToken token)
    {
        var received = false;
        var exit = await ExecuteAsync(async (client, ct) =>
        {
            var reply = await client.SubmitGenerationAsync(request, ct); received = true;
            return jsonOutput() ? jsonPresenter.PrintSubmission(reply) : presenter.PrintSubmission(reply);
        }, token);
        if (!received && exit == CliApplication.TransportError)
            error.WriteLine($"Submission response unavailable; cancellation or connection loss does not prove rollback. Inspect: {commandPrefix?.Invoke() ?? "habyctl"} deployment generation get {request.DeploymentId} {request.Generation}.");
        return exit;
    }

    internal async Task<int> ExecuteGenerationAsync(GenerationRequest request, CancellationToken token)
    {
        var responseReceived = false;
        var exit = await ExecuteAsync(async (client, ct) =>
        {
            var reply = await client.ExecuteGenerationAsync(request, ct);
            responseReceived = true;
            return jsonOutput() ? jsonPresenter.PrintGeneration(reply) : presenter.PrintGeneration(reply);
        }, token);
        if (exit == CliApplication.TransportError && !responseReceived)
            error.WriteLine($"Execution response unavailable; cancellation or connection loss does not prove rollback. Inspect: {commandPrefix?.Invoke() ?? "habyctl"} deployment generation get {request.DeploymentId} {request.Generation}. Do not automatically repeat execution.");
        return exit;
    }

    internal int PrintOperation(string command, OperationReply reply) =>
        jsonOutput() ? jsonPresenter.PrintOperation(command, reply) : presenter.PrintOperation(command, reply);

    internal int PrintRecovery(string command, RecoveryReply reply) =>
        jsonOutput() ? jsonPresenter.PrintRecovery(command, reply) : presenter.PrintRecovery(command, reply);

    internal void ShowRecoverySummary(RecoveryReply reply) => new CliTextPresenter(error).PrintRecovery("operation recovery inspect", reply);

    internal int InvalidRecoveryInput()
    {
        error.WriteLine("Explicit assignment acknowledgment and controlled recovery reason are required.");
        return CliApplication.UsageError;
    }

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
        string? credential = null;
        var skipTls = false;
        try
        {
            var selected = options();
            var resolved = await (contextResolver ?? new DefaultCliContextResolver(configuration, endpointSource))
                .ResolveAsync(new(selected.File, selected.Context), cancellationToken);
            var endpoint = resolved.Endpoint.AbsoluteUri;
            var allowHttp = resolved.AllowInsecureHttp;
            environmentId = resolved.Environment;
            if (requireContext && string.IsNullOrWhiteSpace(environmentId))
            { error.WriteLine("Durable planning requires a selected context with an Environment ID."); return CliApplication.UsageError; }
            skipTls = resolved.SkipTlsCertificateValidation;
            var selectedAddress = CliApplication.ParseEndpoint(endpoint, allowHttp);
            if (selectedAddress is null)
            {
                error.WriteLine("Select an HTTPS registry endpoint, loopback HTTP or an explicitly isolated HTTP instance.");
                return CliApplication.UsageError;
            }
            address = selectedAddress;
            cancellationToken.ThrowIfCancellationRequested();
            credential = resolved.Credentials is null ? null : await resolved.Credentials.GetCredentialAsync(cancellationToken);
            if (credential is not null && !CliOperatorCredential.Valid(credential)) throw new InvalidDataException();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("Operation canceled.");
            return CliApplication.TransportError;
        }
        catch (InvalidRegistryEndpointException)
        { error.WriteLine("Select an HTTPS registry endpoint, loopback HTTP or an explicitly isolated HTTP instance."); return CliApplication.UsageError; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or FormatException or NotSupportedException)
        {
            error.WriteLine("Invalid CLI configuration.");
            return CliApplication.UsageError;
        }
        catch (Exception exception)
        { error.WriteLine(CliApplication.SafeError(exception)); return CliApplication.TransportError; }

        try
        {
            using var client = clientFactory.Create(address,
                skipTls, credential);
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
            if (configAdapter is not null)
            {
                var selection = new CliContextSelection(selected.File, selected.Context);
                if (currentOnly)
                {
                    var selectedCurrent = await configAdapter.GetCurrentContextAsync(selection, cancellationToken);
                    if (jsonOutput()) jsonPresenter.PrintCurrentContext(selectedCurrent); else output.WriteLine(selectedCurrent ?? "(none)");
                }
                else await configAdapter.WriteSafeViewAsync(selection, invocationContext!(), cancellationToken);
                return CliApplication.Success;
            }
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
                output.WriteLine($"  {name}: {instance.Endpoint}; token={(instance.Token is null ? "(none)" : "[redacted]")}; tokenFile={(instance.TokenFile is null ? "(none)" : "[redacted]")}; allowInsecureHttp={instance.AllowInsecureHttp}");
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or FormatException or NotSupportedException)
        {
            error.WriteLine("Invalid CLI configuration.");
            return CliApplication.UsageError;
        }
        catch (Exception exception)
        { error.WriteLine(CliApplication.SafeError(exception)); return CliApplication.TransportError; }
    }
}
