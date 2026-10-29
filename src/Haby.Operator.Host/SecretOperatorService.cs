using Grpc.Core;
using Haby.Application;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override async Task<SecretReply> CreateSecret(CreateSecretRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.RequestId, out var requestId) || requestId == Guid.Empty ||
            request.Material.Length is < 1 or > SecretAdministration.MaxBytes)
            return new SecretReply { Status = SecretStatus.InvalidRequest };
        var bytes = request.Material.ToByteArray();
        try
        {
            using var material = new SecretMaterial(bytes);
            return SecretReplyOf(await secretAdministration.CreateAsync(request.Environment, request.Name, requestId,
                material, OperatorHostApplication.TechnicalActorId, context.CancellationToken));
        }
        finally { Array.Clear(bytes); }
    }

    public override async Task<SecretReply> GetSecret(GetSecretRequest request, ServerCallContext context) =>
        SecretReplyOf(await secretAdministration.GetAsync(request.Environment, request.Name, request.SecretId, context.CancellationToken));

    public override async Task<ListSecretsReply> ListSecrets(ListSecretsRequest request, ServerCallContext context)
    {
        var result = await secretAdministration.ListAsync(request.Environment, request.PageSize, request.Cursor, context.CancellationToken);
        var reply = new ListSecretsReply { Status = SecretStatusOf(result.Outcome), NextCursor = result.NextCursor ?? "" };
        reply.Items.AddRange(result.Items.Select(SecretMetadataOf));
        return reply;
    }

    private static SecretReply SecretReplyOf(SecretResult result)
    {
        var reply = new SecretReply { Status = SecretStatusOf(result.Outcome), Secret = result.Secret is null ? null : SecretMetadataOf(result.Secret) };
        if (result.RequestId is { } id) reply.RequestId = id.ToString();
        return reply;
    }

    private static Haby.Operator.Contracts.SecretMetadata SecretMetadataOf(Haby.Application.SecretMetadata value)
    {
        var result = new Haby.Operator.Contracts.SecretMetadata
        { EnvironmentRecordId = value.Environment.ToString(), SecretId = value.SecretId.ToString(), Purpose = value.Purpose };
        if (value.Name is not null) result.Name = value.Name;
        if (value.CreatedAt is { } time) result.CreatedAtUtc = time.ToString("O");
        foreach (var version in value.Versions)
        {
            var item = new Haby.Operator.Contracts.SecretVersionMetadata { VersionId = version.VersionId.ToString() };
            if (version.Number is { } number) item.Number = number;
            if (version.CreatedAt is { } created) item.CreatedAtUtc = created.ToString("O");
            result.Versions.Add(item);
        }
        return result;
    }

    private static SecretStatus SecretStatusOf(SecretOutcome value) => value switch
    {
        SecretOutcome.Created => SecretStatus.Created,
        SecretOutcome.ExistingEquivalent => SecretStatus.ExistingEquivalent,
        SecretOutcome.InvalidRequest => SecretStatus.InvalidRequest,
        SecretOutcome.EnvironmentNotFound => SecretStatus.EnvironmentNotFound,
        SecretOutcome.RequestContentConflict => SecretStatus.RequestContentConflict,
        SecretOutcome.SecretStorageUnavailable => SecretStatus.SecretStorageUnavailable,
        SecretOutcome.Found => SecretStatus.Found,
        SecretOutcome.NotFound => SecretStatus.NotFound,
        SecretOutcome.Listed => SecretStatus.Listed,
        SecretOutcome.InvalidCursor => SecretStatus.InvalidCursor,
        SecretOutcome.InvalidPageSize => SecretStatus.InvalidPageSize,
        _ => throw new InvalidOperationException("Unknown secret outcome.")
    };
}
