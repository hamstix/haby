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
    Task<OperatorAccessReply> CreateOperatorAccessAsync(CreateOperatorAccessRequest request, CancellationToken token);
    Task<OperatorAccessReply> GetOperatorAccessAsync(OperatorAccessRequest request, CancellationToken token);
    Task<ListOperatorAccessReply> ListOperatorAccessAsync(ListOperatorAccessRequest request, CancellationToken token);
    Task<OperatorAccessReply> RevokeOperatorAccessAsync(OperatorAccessRequest request, CancellationToken token);
    Task<ConfigurationAccessReply> CreateConfigurationAccessAsync(CreateConfigurationAccessRequest request, CancellationToken cancellationToken);
    Task<ConfigurationAccessReply> GetConfigurationAccessAsync(ConfigurationAccessRequest request, CancellationToken cancellationToken);
    Task<ConfigurationAccessReply> RevokeConfigurationAccessAsync(ConfigurationAccessRequest request, CancellationToken cancellationToken);
    Task<ConfigurationDocumentReply> MaterializeConfigurationAsync(ConfigurationDocumentRequest request, CancellationToken cancellationToken);
    Task<ConfigurationDocumentReply> GetConfigurationAsync(ConfigurationDocumentRequest request, CancellationToken cancellationToken);
    Task<ConfigurationDocumentReply> PreviewConfigurationAsync(ConfigurationDocumentRequest request, CancellationToken cancellationToken);
    Task<ProfileAssignmentReply> ValidateProfileAssignmentAsync(ProfilePayloadRequest request, CancellationToken cancellationToken);
    Task<ProfileAssignmentReply> GetProfileAssignmentAsync(GetProfileAssignmentRequest request, CancellationToken cancellationToken);
    Task<ProfileAssignmentReply> ApplyProfileAssignmentAsync(ApplyProfileAssignmentRequest request, CancellationToken cancellationToken);
    Task<ProfileReply> GetProfileRevisionAsync(ProfileRevisionRequest request, CancellationToken cancellationToken);
    Task<ProfileReply> ImportProfileAsync(ProfilePayloadRequest request, CancellationToken cancellationToken);
    Task<ProfileReply> ValidateProfileAsync(ProfilePayloadRequest request, CancellationToken cancellationToken);
    Task<ProviderAccessReply> CheckProviderAsync(CheckProviderRequest request, CancellationToken cancellationToken);
    Task<ProviderAccessReply> GetProviderAliasAsync(GetProviderAliasRequest request, CancellationToken cancellationToken);
    Task<ProviderAccessReply> SetProviderAliasAsync(SetProviderAliasRequest request, CancellationToken cancellationToken);

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
    Task<GenerationExecutionReply> ExecuteGenerationAsync(GenerationRequest request, CancellationToken cancellationToken);
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
    IRegistryClient Create(Uri address, bool skipTlsValidation, string? credential = null);
}

internal sealed class GrpcRegistryClientFactory : IRegistryClientFactory
{
    public IRegistryClient Create(Uri address, bool skipTlsValidation, string? credential = null)
    {
        var channel = skipTlsValidation && address.Scheme == Uri.UriSchemeHttps
            ? GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = CliApplication.CreateHttpHandler(true) })
            : GrpcChannel.ForAddress(address);
        var configurationChannel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            MaxReceiveMessageSize = 8 * 1024 * 1024,
            HttpHandler = CliApplication.CreateHttpHandler(skipTlsValidation && address.Scheme == Uri.UriSchemeHttps)
        });
        return new GrpcRegistryClient(channel, configurationChannel, credential);
    }
}

internal sealed class GrpcRegistryClient(GrpcChannel channel, GrpcChannel? configurationChannel = null, string? credential = null) : IRegistryClient
{
    private readonly Grpc.Core.Metadata? headers = credential is null ? null : new() { { "authorization", "Bearer " + credential } };
    public async Task<OperatorAccessReply> CreateOperatorAccessAsync(CreateOperatorAccessRequest request, CancellationToken token) => await _client.CreateOperatorAccessAsync(request, headers, cancellationToken: token);
    public async Task<OperatorAccessReply> GetOperatorAccessAsync(OperatorAccessRequest request, CancellationToken token) => await _client.GetOperatorAccessAsync(request, headers, cancellationToken: token);
    public async Task<ListOperatorAccessReply> ListOperatorAccessAsync(ListOperatorAccessRequest request, CancellationToken token) => await _client.ListOperatorAccessAsync(request, headers, cancellationToken: token);
    public async Task<OperatorAccessReply> RevokeOperatorAccessAsync(OperatorAccessRequest request, CancellationToken token) => await _client.RevokeOperatorAccessAsync(request, headers, cancellationToken: token);
    public async Task<ConfigurationAccessReply> CreateConfigurationAccessAsync(CreateConfigurationAccessRequest request, CancellationToken cancellationToken) =>
        await _client.CreateConfigurationAccessAsync(request, headers, cancellationToken: cancellationToken);
    public async Task<ConfigurationAccessReply> GetConfigurationAccessAsync(ConfigurationAccessRequest request, CancellationToken cancellationToken) =>
        await _client.GetConfigurationAccessAsync(request, headers, cancellationToken: cancellationToken);
    public async Task<ConfigurationAccessReply> RevokeConfigurationAccessAsync(ConfigurationAccessRequest request, CancellationToken cancellationToken) =>
        await _client.RevokeConfigurationAccessAsync(request, headers, cancellationToken: cancellationToken);
    private readonly ManifestOperator.ManifestOperatorClient _client = new(channel);
    private readonly ManifestOperator.ManifestOperatorClient _configurationClient = new(configurationChannel ?? channel);
    public async Task<ConfigurationDocumentReply> MaterializeConfigurationAsync(ConfigurationDocumentRequest request, CancellationToken cancellationToken) =>
        await _configurationClient.MaterializeConfigurationAsync(request, headers, cancellationToken: cancellationToken);
    public async Task<ConfigurationDocumentReply> GetConfigurationAsync(ConfigurationDocumentRequest request, CancellationToken cancellationToken) =>
        await _configurationClient.GetConfigurationAsync(request, headers, cancellationToken: cancellationToken);
    public async Task<ConfigurationDocumentReply> PreviewConfigurationAsync(ConfigurationDocumentRequest request, CancellationToken cancellationToken) =>
        await _configurationClient.PreviewConfigurationAsync(request, headers, cancellationToken: cancellationToken);
    public async Task<ProfileAssignmentReply> ValidateProfileAssignmentAsync(ProfilePayloadRequest request, CancellationToken cancellationToken) =>
        await _client.ValidateProfileAssignmentAsync(request, headers, cancellationToken: cancellationToken);
    public async Task<ProfileAssignmentReply> GetProfileAssignmentAsync(GetProfileAssignmentRequest request, CancellationToken cancellationToken) =>
        await _client.GetProfileAssignmentAsync(request, headers, cancellationToken: cancellationToken);
    public async Task<ProfileAssignmentReply> ApplyProfileAssignmentAsync(ApplyProfileAssignmentRequest request, CancellationToken cancellationToken) =>
        await _client.ApplyProfileAssignmentAsync(request, headers, cancellationToken: cancellationToken);
    public async Task<ProfileReply> GetProfileRevisionAsync(ProfileRevisionRequest request, CancellationToken cancellationToken) =>
        await _client.GetProfileRevisionAsync(request, headers, cancellationToken: cancellationToken);
    public async Task<ProfileReply> ImportProfileAsync(ProfilePayloadRequest request, CancellationToken cancellationToken) =>
        await _client.ImportProfileAsync(request, headers, cancellationToken: cancellationToken);
    public async Task<ProfileReply> ValidateProfileAsync(ProfilePayloadRequest request, CancellationToken cancellationToken) =>
        await _client.ValidateProfileAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<RecoveryReply> InspectOperationRecoveryAsync(OperationRequest request, CancellationToken cancellationToken) =>
        await _client.InspectOperationRecoveryAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<RecoveryReply> AssignExistingOperationAsync(AssignExistingOperationRequest request, CancellationToken cancellationToken) =>
        await _client.AssignExistingOperationAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<ValidateReply> ValidateAsync(EnvelopeRequest request, CancellationToken cancellationToken) =>
        await _client.ValidateAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<ImportReply> ImportAsync(EnvelopeRequest request, CancellationToken cancellationToken) =>
        await _client.ImportAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<GetRevisionReply> GetRevisionAsync(GetRevisionRequest request, CancellationToken cancellationToken) =>
        await _client.GetRevisionAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<PreviewDatabaseReply> PreviewDatabaseAsync(PreviewDatabaseRequest request, CancellationToken cancellationToken) =>
        await _client.PreviewDatabaseAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<PlanningReply> CreateChangeSetAsync(CreateChangeSetRequest request, CancellationToken cancellationToken) =>
        await _client.CreateChangeSetAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<PlanningReply> GetChangeSetAsync(ChangeSetRequest request, CancellationToken cancellationToken) =>
        await _client.GetChangeSetAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<PlanningReply> CreatePlanAsync(ChangeSetRequest request, CancellationToken cancellationToken) =>
        await _client.CreatePlanAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<PlanningReply> GetPlanAsync(PlanRequest request, CancellationToken cancellationToken) =>
        await _client.GetPlanAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<PlanningReply> CheckPlanAsync(PlanRequest request, CancellationToken cancellationToken) =>
        await _client.CheckPlanAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<PlanningReply> ClosePlanAsync(PlanRequest request, CancellationToken cancellationToken) =>
        await _client.ClosePlanAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<CommitReply> CommitDesiredStateAsync(CommitDesiredStateRequest request, CancellationToken cancellationToken) =>
        await _client.CommitDesiredStateAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<CommitReply> GetCommitAsync(ChangeSetRequest request, CancellationToken cancellationToken) =>
        await _client.GetCommitAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<GenerationExecutionReply> ExecuteGenerationAsync(GenerationRequest request, CancellationToken cancellationToken) =>
        await _client.ExecuteGenerationAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<CommitReply> GetGenerationAsync(GenerationRequest request, CancellationToken cancellationToken) =>
        await _client.GetGenerationAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<OperationReply> ExecuteOperationAsync(OperationRequest request, CancellationToken cancellationToken) =>
        await _client.ExecuteOperationAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<OperationReply> GetOperationAsync(OperationRequest request, CancellationToken cancellationToken) =>
        await _client.GetOperationAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<EnvironmentReply> CreateEnvironmentAsync(EnvironmentNameRequest request, CancellationToken cancellationToken) =>
        await _client.CreateEnvironmentAsync(request, headers, cancellationToken: cancellationToken);
    public async Task<EnvironmentReply> GetEnvironmentAsync(EnvironmentNameRequest request, CancellationToken cancellationToken) =>
        await _client.GetEnvironmentAsync(request, headers, cancellationToken: cancellationToken);
    public async Task<ListEnvironmentsReply> ListEnvironmentsAsync(ListEnvironmentsRequest request, CancellationToken cancellationToken) =>
        await _client.ListEnvironmentsAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<SecretReply> CreateSecretAsync(CreateSecretRequest request, CancellationToken cancellationToken) =>
        await _client.CreateSecretAsync(request, headers, cancellationToken: cancellationToken);
    public async Task<SecretReply> GetSecretAsync(GetSecretRequest request, CancellationToken cancellationToken) =>
        await _client.GetSecretAsync(request, headers, cancellationToken: cancellationToken);
    public async Task<ListSecretsReply> ListSecretsAsync(ListSecretsRequest request, CancellationToken cancellationToken) =>
        await _client.ListSecretsAsync(request, headers, cancellationToken: cancellationToken);

    public async Task<ProviderReply> ApplyProviderAsync(ApplyProviderRequest request, CancellationToken cancellationToken) =>
        await _client.ApplyProviderAsync(request, headers, cancellationToken: cancellationToken);
    public async Task<ProviderReply> GetProviderAsync(GetProviderRequest request, CancellationToken cancellationToken) =>
        await _client.GetProviderAsync(request, headers, cancellationToken: cancellationToken);
    public async Task<ListProvidersReply> ListProvidersAsync(ListProvidersRequest request, CancellationToken cancellationToken) =>
        await _client.ListProvidersAsync(request, headers, cancellationToken: cancellationToken);
    public void Dispose() { configurationChannel?.Dispose(); channel.Dispose(); }
    public async Task<ProviderAccessReply> CheckProviderAsync(CheckProviderRequest request, CancellationToken cancellationToken) =>
        await _client.CheckProviderAsync(request, headers, cancellationToken: cancellationToken);
    public async Task<ProviderAccessReply> GetProviderAliasAsync(GetProviderAliasRequest request, CancellationToken cancellationToken) =>
        await _client.GetProviderAliasAsync(request, headers, cancellationToken: cancellationToken);
    public async Task<ProviderAccessReply> SetProviderAliasAsync(SetProviderAliasRequest request, CancellationToken cancellationToken) =>
        await _client.SetProviderAliasAsync(request, headers, cancellationToken: cancellationToken);
}
