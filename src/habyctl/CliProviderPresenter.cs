using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliProviderSecretReference(string Slot, string SecretId, string SecretVersionId);
internal sealed record CliProviderRevision(long Revision, string ConfigurationContract, string ImplementationId, CliProviderSecretReference[] SecretReferences);
internal sealed record CliProviderMetadata(string EnvironmentRecordId, string InstanceId, string? Name, string ProviderKind, CliProviderRevision? Revision);
internal sealed record CliProviderData(CliProviderMetadata? Provider, long? PreviousRevision);
internal sealed record CliProviderListData(CliProviderMetadata[] Items, string? NextCursor);

internal static class ProviderPresentation
{
    internal static string Outcome(ProviderStatus status) => status switch
    {
        ProviderStatus.Created => "created",
        ProviderStatus.ExistingEquivalent => "existingEquivalent",
        ProviderStatus.InvalidRequest => "invalidRequest",
        ProviderStatus.EnvironmentMismatch => "environmentMismatch",
        ProviderStatus.EnvironmentNotFound => "environmentNotFound",
        ProviderStatus.UnsupportedConfiguration => "unsupportedConfiguration",
        ProviderStatus.ConfigurationUnavailable => "configurationUnavailable",
        ProviderStatus.SecretReferenceUnavailable => "secretReferenceUnavailable",
        ProviderStatus.RevisionContentConflict => "revisionContentConflict",
        ProviderStatus.RevisionNotIncreasing => "revisionNotIncreasing",
        ProviderStatus.IdentityConfirmationRequired => "identityConfirmationRequired",
        ProviderStatus.StaleBaseline => "staleBaseline",
        ProviderStatus.ProviderKindConflict => "providerKindConflict",
        ProviderStatus.StorageUnavailable => "storageUnavailable",
        ProviderStatus.Found => "found",
        ProviderStatus.NotFound => "notFound",
        ProviderStatus.Listed => "listed",
        ProviderStatus.InvalidCursor => "invalidCursor",
        ProviderStatus.InvalidPageSize => "invalidPageSize",
        _ => throw new InvalidDataException("Unknown Provider outcome.")
    };
    internal static int Exit(ProviderStatus status) => status is ProviderStatus.Created or ProviderStatus.ExistingEquivalent or ProviderStatus.Found or ProviderStatus.Listed ? 0 : 1;
    internal static void Validate(string command, ProviderReply reply)
    {
        _ = Outcome(reply.Status);
        var apply = command == "provider apply";
        if (apply ? reply.Status is ProviderStatus.Found or ProviderStatus.NotFound or ProviderStatus.Listed or ProviderStatus.InvalidCursor or ProviderStatus.InvalidPageSize :
            reply.Status is not (ProviderStatus.Found or ProviderStatus.NotFound or ProviderStatus.EnvironmentNotFound or ProviderStatus.InvalidRequest))
            throw new InvalidDataException("Unexpected Provider outcome.");
        if (Exit(reply.Status) == 0 && (reply.Provider is null || (apply && reply.Provider.Revision is null)))
            throw new InvalidDataException("Incomplete Provider result.");
        if (reply.Provider is not null) _ = Metadata(reply.Provider);
        if (reply.HasPreviousRevision && reply.PreviousRevision <= 0) throw new InvalidDataException("Invalid Provider baseline.");
    }
    internal static void Validate(ListProvidersReply reply)
    {
        if (reply.Status is not (ProviderStatus.Listed or ProviderStatus.InvalidRequest or ProviderStatus.EnvironmentNotFound or ProviderStatus.InvalidCursor or ProviderStatus.InvalidPageSize) ||
            reply.Items.Count > 100 || reply.NextCursor.Length > 128) throw new InvalidDataException("Invalid Provider page.");
        foreach (var item in reply.Items) _ = Metadata(item);
    }
    internal static CliProviderMetadata Metadata(ProviderMetadata value)
    {
        if (!Identity(value.EnvironmentRecordId) || !Identity(value.InstanceId) || !SafeId(value.ProviderKind) ||
            (value.HasName && (value.Name.Length is < 1 or > 64 || value.Name.Any(char.IsControl)))) throw new InvalidDataException("Invalid Provider metadata.");
        CliProviderRevision? revision = null;
        if (value.Revision is { } r)
        {
            if (r.Revision <= 0 || !SafeId(r.ConfigurationContract) || !SafeId(r.ImplementationId) || r.SecretReferences.Count > 64 ||
                r.SecretReferences.Any(x => !SafeId(x.Slot) || !Identity(x.SecretId) || !Identity(x.SecretVersionId))) throw new InvalidDataException("Invalid revision metadata.");
            revision = new(r.Revision, r.ConfigurationContract, r.ImplementationId,
                [.. r.SecretReferences.Select(x => new CliProviderSecretReference(x.Slot, x.SecretId, x.SecretVersionId))]);
        }
        return new(value.EnvironmentRecordId, value.InstanceId, value.HasName ? value.Name : null, value.ProviderKind, revision);
    }
    private static bool SafeId(string value) => value.Length is > 0 and <= 128 && !value.Any(char.IsControl);
    private static bool Identity(string value) => Guid.TryParseExact(value, "D", out var id) && id.Version == 7 &&
        value[19] is ('8' or '9' or 'a' or 'b') && value == id.ToString();
}

internal sealed partial class CliJsonPresenter
{
    internal int PrintProvider(string command, ProviderReply reply)
    {
        ProviderPresentation.Validate(command, reply);
        Write(command, ProviderPresentation.Outcome(reply.Status), new CliProviderData(reply.Provider is null ? null : ProviderPresentation.Metadata(reply.Provider),
            reply.HasPreviousRevision ? reply.PreviousRevision : null), NoDiagnostics, CliOutputJsonContext.Default.CliEnvelopeCliProviderData);
        return ProviderPresentation.Exit(reply.Status);
    }
    internal int PrintProviders(ListProvidersReply reply)
    {
        ProviderPresentation.Validate(reply);
        Write("provider list", ProviderPresentation.Outcome(reply.Status), new CliProviderListData([.. reply.Items.Select(ProviderPresentation.Metadata)],
            string.IsNullOrEmpty(reply.NextCursor) ? null : reply.NextCursor), NoDiagnostics, CliOutputJsonContext.Default.CliEnvelopeCliProviderListData);
        return ProviderPresentation.Exit(reply.Status);
    }
}
internal sealed partial class CliTextPresenter
{
    internal int PrintProvider(string command, ProviderReply reply)
    {
        ProviderPresentation.Validate(command, reply);
        _output.WriteLine(ProviderPresentation.Outcome(reply.Status));
        if (reply.Provider is { } provider) PrintProviderMetadata(provider);
        if (reply.HasPreviousRevision) _output.WriteLine($"previousRevision: {reply.PreviousRevision}");
        return ProviderPresentation.Exit(reply.Status);
    }
    internal int PrintProviders(ListProvidersReply reply)
    {
        ProviderPresentation.Validate(reply);
        _output.WriteLine(ProviderPresentation.Outcome(reply.Status));
        foreach (var provider in reply.Items) PrintProviderMetadata(provider);
        if (!string.IsNullOrEmpty(reply.NextCursor)) _output.WriteLine($"nextCursor: {reply.NextCursor}");
        return ProviderPresentation.Exit(reply.Status);
    }
    private void PrintProviderMetadata(ProviderMetadata value)
    {
        _output.WriteLine($"{(value.HasName ? value.Name : "(unnamed)")} {value.InstanceId} {value.ProviderKind}");
        if (value.Revision is { } revision)
        {
            _output.WriteLine($"  revision {revision.Revision} {revision.ConfigurationContract} {revision.ImplementationId}");
            foreach (var reference in revision.SecretReferences) _output.WriteLine($"  {reference.Slot} {reference.SecretId} {reference.SecretVersionId}");
        }
    }
}
