using Grpc.Core;
using Haby.Application;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override async Task<ProviderReply> ApplyProvider(ApplyProviderRequest request, ServerCallContext context) =>
        ProviderReplyOf(await providerAdministration.ApplyAsync(request.Declaration.Memory,
            request.HasEnvironment ? request.Environment : null, request.ConfirmLogicalIdentity,
            request.HasExpectedPreviousRevision ? request.ExpectedPreviousRevision : null,
            OperatorHostApplication.TechnicalActorId, context.CancellationToken));

    public override async Task<ProviderReply> GetProvider(GetProviderRequest request, ServerCallContext context) =>
        ProviderReplyOf(await providerAdministration.GetAsync(request.Environment, request.Name,
            request.HasRevision ? request.Revision : null, context.CancellationToken));

    public override async Task<ListProvidersReply> ListProviders(ListProvidersRequest request, ServerCallContext context)
    {
        var result = await providerAdministration.ListAsync(request.Environment, request.PageSize, request.Cursor, context.CancellationToken);
        var reply = new ListProvidersReply { Status = ProviderStatusOf(result.Outcome), NextCursor = result.NextCursor ?? "" };
        reply.Items.AddRange(result.Items.Select(ProviderMetadataOf));
        return reply;
    }
    private static ProviderReply ProviderReplyOf(ProviderResult result)
    {
        var reply = new ProviderReply { Status = ProviderStatusOf(result.Outcome), Provider = result.Provider is null ? null : ProviderMetadataOf(result.Provider) };
        if (result.PreviousRevision is { } previous) reply.PreviousRevision = previous;
        return reply;
    }
    private static Haby.Operator.Contracts.ProviderMetadata ProviderMetadataOf(Haby.Application.ProviderMetadata value)
    {
        var result = new Haby.Operator.Contracts.ProviderMetadata
        { EnvironmentRecordId = value.Environment.ToString(), InstanceId = value.InstanceId.ToString(), ProviderKind = value.ProviderKind };
        if (value.Name is not null) result.Name = value.Name;
        if (value.Revision is { } revision)
        {
            result.Revision = new Haby.Operator.Contracts.ProviderRevisionMetadata
            { Revision = revision.Revision, ConfigurationContract = revision.ConfigurationContract, ImplementationId = revision.ImplementationId };
            result.Revision.SecretReferences.AddRange(revision.SecretReferences.Select(x => new ProviderSecretReference
            { Slot = x.Slot, SecretId = x.Reference.SecretId.ToString(), SecretVersionId = x.Reference.SecretVersionId.ToString() }));
        }
        return result;
    }
    private static ProviderStatus ProviderStatusOf(ProviderOutcome value) => value switch
    {
        ProviderOutcome.Created => ProviderStatus.Created,
        ProviderOutcome.ExistingEquivalent => ProviderStatus.ExistingEquivalent,
        ProviderOutcome.InvalidRequest => ProviderStatus.InvalidRequest,
        ProviderOutcome.EnvironmentMismatch => ProviderStatus.EnvironmentMismatch,
        ProviderOutcome.EnvironmentNotFound => ProviderStatus.EnvironmentNotFound,
        ProviderOutcome.UnsupportedConfiguration => ProviderStatus.UnsupportedConfiguration,
        ProviderOutcome.ConfigurationUnavailable => ProviderStatus.ConfigurationUnavailable,
        ProviderOutcome.SecretReferenceUnavailable => ProviderStatus.SecretReferenceUnavailable,
        ProviderOutcome.RevisionContentConflict => ProviderStatus.RevisionContentConflict,
        ProviderOutcome.RevisionNotIncreasing => ProviderStatus.RevisionNotIncreasing,
        ProviderOutcome.IdentityConfirmationRequired => ProviderStatus.IdentityConfirmationRequired,
        ProviderOutcome.StaleBaseline => ProviderStatus.StaleBaseline,
        ProviderOutcome.ProviderKindConflict => ProviderStatus.ProviderKindConflict,
        ProviderOutcome.StorageUnavailable => ProviderStatus.StorageUnavailable,
        ProviderOutcome.Found => ProviderStatus.Found,
        ProviderOutcome.NotFound => ProviderStatus.NotFound,
        ProviderOutcome.Listed => ProviderStatus.Listed,
        ProviderOutcome.InvalidCursor => ProviderStatus.InvalidCursor,
        ProviderOutcome.InvalidPageSize => ProviderStatus.InvalidPageSize,
        _ => throw new InvalidOperationException("Unknown Provider outcome.")
    };
}
