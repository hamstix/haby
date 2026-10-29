using Grpc.Net.Client;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal interface IEndpointSource
{
    string? GetAddress();
    string? GetSkipTlsValidation();
}

internal sealed class EnvironmentEndpointSource : IEndpointSource
{
    public string? GetAddress() => Environment.GetEnvironmentVariable("HABY_OPERATOR_URL");
    public string? GetSkipTlsValidation() => Environment.GetEnvironmentVariable("HABY_OPERATOR_SKIP_TLS_VALIDATION");
}

internal interface IRegistryClient : IDisposable
{
    Task<ProviderReply> ApplyProviderAsync(ApplyProviderRequest request, CancellationToken cancellationToken);
    Task<ProviderReply> GetProviderAsync(GetProviderRequest request, CancellationToken cancellationToken);
    Task<ListProvidersReply> ListProvidersAsync(ListProvidersRequest request, CancellationToken cancellationToken);
    Task<SecretReply> CreateSecretAsync(CreateSecretRequest request, CancellationToken cancellationToken);
    Task<SecretReply> GetSecretAsync(GetSecretRequest request, CancellationToken cancellationToken);
    Task<ListSecretsReply> ListSecretsAsync(ListSecretsRequest request, CancellationToken cancellationToken);
    Task<EnvironmentReply> CreateEnvironmentAsync(EnvironmentNameRequest request, CancellationToken cancellationToken);
    Task<EnvironmentReply> GetEnvironmentAsync(EnvironmentNameRequest request, CancellationToken cancellationToken);
    Task<ListEnvironmentsReply> ListEnvironmentsAsync(ListEnvironmentsRequest request, CancellationToken cancellationToken);
    Task<RecoveryReply> InspectOperationRecoveryAsync(OperationRequest request, CancellationToken cancellationToken);
    Task<RecoveryReply> AssignExistingOperationAsync(AssignExistingOperationRequest request, CancellationToken cancellationToken);
    Task<OperationReply> ExecuteOperationAsync(OperationRequest request, CancellationToken cancellationToken);
    Task<OperationReply> GetOperationAsync(OperationRequest request, CancellationToken cancellationToken);
    Task<CommitReply> CommitDesiredStateAsync(CommitDesiredStateRequest request, CancellationToken cancellationToken);
    Task<CommitReply> GetCommitAsync(ChangeSetRequest request, CancellationToken cancellationToken);
    Task<CommitReply> GetGenerationAsync(GenerationRequest request, CancellationToken cancellationToken);
    Task<PlanningReply> CreateChangeSetAsync(CreateChangeSetRequest request, CancellationToken cancellationToken);
    Task<PlanningReply> GetChangeSetAsync(ChangeSetRequest request, CancellationToken cancellationToken);
    Task<PlanningReply> CreatePlanAsync(ChangeSetRequest request, CancellationToken cancellationToken);
    Task<PlanningReply> GetPlanAsync(PlanRequest request, CancellationToken cancellationToken);
    Task<PlanningReply> CheckPlanAsync(PlanRequest request, CancellationToken cancellationToken);
    Task<PlanningReply> ClosePlanAsync(PlanRequest request, CancellationToken cancellationToken);
    Task<ValidateReply> ValidateAsync(EnvelopeRequest request, CancellationToken cancellationToken);
    Task<ImportReply> ImportAsync(EnvelopeRequest request, CancellationToken cancellationToken);
    Task<GetRevisionReply> GetRevisionAsync(GetRevisionRequest request, CancellationToken cancellationToken);
    Task<PreviewDatabaseReply> PreviewDatabaseAsync(PreviewDatabaseRequest request, CancellationToken cancellationToken);
}

internal interface IRegistryClientFactory
{
    IRegistryClient Create(Uri address, bool skipTlsValidation);
}

internal sealed class GrpcRegistryClientFactory : IRegistryClientFactory
{
    public IRegistryClient Create(Uri address, bool skipTlsValidation)
    {
        var channel = skipTlsValidation && address.Scheme == Uri.UriSchemeHttps
            ? GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = CliApplication.CreateHttpHandler(true) })
            : GrpcChannel.ForAddress(address);
        return new GrpcRegistryClient(channel);
    }
}

internal sealed class GrpcRegistryClient(GrpcChannel channel) : IRegistryClient
{
    private readonly ManifestOperator.ManifestOperatorClient _client = new(channel);

    public async Task<RecoveryReply> InspectOperationRecoveryAsync(OperationRequest request, CancellationToken cancellationToken) =>
        await _client.InspectOperationRecoveryAsync(request, cancellationToken: cancellationToken);

    public async Task<RecoveryReply> AssignExistingOperationAsync(AssignExistingOperationRequest request, CancellationToken cancellationToken) =>
        await _client.AssignExistingOperationAsync(request, cancellationToken: cancellationToken);

    public async Task<ValidateReply> ValidateAsync(EnvelopeRequest request, CancellationToken cancellationToken) =>
        await _client.ValidateAsync(request, cancellationToken: cancellationToken);

    public async Task<ImportReply> ImportAsync(EnvelopeRequest request, CancellationToken cancellationToken) =>
        await _client.ImportAsync(request, cancellationToken: cancellationToken);

    public async Task<GetRevisionReply> GetRevisionAsync(GetRevisionRequest request, CancellationToken cancellationToken) =>
        await _client.GetRevisionAsync(request, cancellationToken: cancellationToken);

    public async Task<PreviewDatabaseReply> PreviewDatabaseAsync(PreviewDatabaseRequest request, CancellationToken cancellationToken) =>
        await _client.PreviewDatabaseAsync(request, cancellationToken: cancellationToken);

    public async Task<PlanningReply> CreateChangeSetAsync(CreateChangeSetRequest request, CancellationToken cancellationToken) =>
        await _client.CreateChangeSetAsync(request, cancellationToken: cancellationToken);

    public async Task<PlanningReply> GetChangeSetAsync(ChangeSetRequest request, CancellationToken cancellationToken) =>
        await _client.GetChangeSetAsync(request, cancellationToken: cancellationToken);

    public async Task<PlanningReply> CreatePlanAsync(ChangeSetRequest request, CancellationToken cancellationToken) =>
        await _client.CreatePlanAsync(request, cancellationToken: cancellationToken);

    public async Task<PlanningReply> GetPlanAsync(PlanRequest request, CancellationToken cancellationToken) =>
        await _client.GetPlanAsync(request, cancellationToken: cancellationToken);

    public async Task<PlanningReply> CheckPlanAsync(PlanRequest request, CancellationToken cancellationToken) =>
        await _client.CheckPlanAsync(request, cancellationToken: cancellationToken);

    public async Task<PlanningReply> ClosePlanAsync(PlanRequest request, CancellationToken cancellationToken) =>
        await _client.ClosePlanAsync(request, cancellationToken: cancellationToken);

    public async Task<CommitReply> CommitDesiredStateAsync(CommitDesiredStateRequest request, CancellationToken cancellationToken) =>
        await _client.CommitDesiredStateAsync(request, cancellationToken: cancellationToken);

    public async Task<CommitReply> GetCommitAsync(ChangeSetRequest request, CancellationToken cancellationToken) =>
        await _client.GetCommitAsync(request, cancellationToken: cancellationToken);

    public async Task<CommitReply> GetGenerationAsync(GenerationRequest request, CancellationToken cancellationToken) =>
        await _client.GetGenerationAsync(request, cancellationToken: cancellationToken);

    public async Task<OperationReply> ExecuteOperationAsync(OperationRequest request, CancellationToken cancellationToken) =>
        await _client.ExecuteOperationAsync(request, cancellationToken: cancellationToken);

    public async Task<OperationReply> GetOperationAsync(OperationRequest request, CancellationToken cancellationToken) =>
        await _client.GetOperationAsync(request, cancellationToken: cancellationToken);

    public async Task<EnvironmentReply> CreateEnvironmentAsync(EnvironmentNameRequest request, CancellationToken cancellationToken) =>
        await _client.CreateEnvironmentAsync(request, cancellationToken: cancellationToken);
    public async Task<EnvironmentReply> GetEnvironmentAsync(EnvironmentNameRequest request, CancellationToken cancellationToken) =>
        await _client.GetEnvironmentAsync(request, cancellationToken: cancellationToken);
    public async Task<ListEnvironmentsReply> ListEnvironmentsAsync(ListEnvironmentsRequest request, CancellationToken cancellationToken) =>
        await _client.ListEnvironmentsAsync(request, cancellationToken: cancellationToken);

    public async Task<SecretReply> CreateSecretAsync(CreateSecretRequest request, CancellationToken cancellationToken) =>
        await _client.CreateSecretAsync(request, cancellationToken: cancellationToken);
    public async Task<SecretReply> GetSecretAsync(GetSecretRequest request, CancellationToken cancellationToken) =>
        await _client.GetSecretAsync(request, cancellationToken: cancellationToken);
    public async Task<ListSecretsReply> ListSecretsAsync(ListSecretsRequest request, CancellationToken cancellationToken) =>
        await _client.ListSecretsAsync(request, cancellationToken: cancellationToken);

    public async Task<ProviderReply> ApplyProviderAsync(ApplyProviderRequest request, CancellationToken cancellationToken) =>
        await _client.ApplyProviderAsync(request, cancellationToken: cancellationToken);
    public async Task<ProviderReply> GetProviderAsync(GetProviderRequest request, CancellationToken cancellationToken) =>
        await _client.GetProviderAsync(request, cancellationToken: cancellationToken);
    public async Task<ListProvidersReply> ListProvidersAsync(ListProvidersRequest request, CancellationToken cancellationToken) =>
        await _client.ListProvidersAsync(request, cancellationToken: cancellationToken);
    public void Dispose() => channel.Dispose();
}
