using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliSecretVersionData(string VersionId, int? Number, string? CreatedAtUtc);
internal sealed record CliSecretMetadataData(string EnvironmentRecordId, string SecretId, string? Name, string Purpose,
    string? CreatedAtUtc, CliSecretVersionData[] Versions);
internal sealed record CliSecretData(string? RequestId, CliSecretMetadataData? Secret);
internal sealed record CliSecretListData(CliSecretMetadataData[] Items, string? NextCursor);

internal static class SecretPresentation
{
    internal static void Validate(string command, SecretReply reply)
    {
        var create = command == "secret create";
        var success = create ? reply.Status is SecretStatus.Created or SecretStatus.ExistingEquivalent : reply.Status == SecretStatus.Found;
        var rejection = create ? reply.Status is SecretStatus.InvalidRequest or SecretStatus.EnvironmentNotFound or
            SecretStatus.RequestContentConflict or SecretStatus.SecretStorageUnavailable :
            reply.Status is SecretStatus.InvalidRequest or SecretStatus.EnvironmentNotFound or SecretStatus.NotFound;
        if (!success && !rejection) throw new InvalidDataException("Unknown secret outcome.");
        if (success)
        {
            if (reply.Secret is null) throw new InvalidDataException("Incomplete secret metadata.");
            _ = Metadata(reply.Secret);
            if (create && (!reply.Secret.HasName || reply.Secret.Versions.Count != 1 || reply.Secret.Versions[0].Number != 1))
                throw new InvalidDataException("Incomplete creation metadata.");
        }
    }

    internal static void Validate(ListSecretsReply reply)
    {
        if (reply.Status is not (SecretStatus.Listed or SecretStatus.InvalidRequest or SecretStatus.EnvironmentNotFound or
            SecretStatus.InvalidCursor or SecretStatus.InvalidPageSize)) throw new InvalidDataException("Unknown secret outcome.");
        if (reply.Items.Count > 100) throw new InvalidDataException("Unbounded secret metadata.");
        foreach (var item in reply.Items) _ = Metadata(item);
    }

    internal static string Outcome(SecretStatus value) => value switch
    {
        SecretStatus.Created => "created",
        SecretStatus.ExistingEquivalent => "existingEquivalent",
        SecretStatus.InvalidRequest => "invalidRequest",
        SecretStatus.EnvironmentNotFound => "environmentNotFound",
        SecretStatus.RequestContentConflict => "requestContentConflict",
        SecretStatus.SecretStorageUnavailable => "secretStorageUnavailable",
        SecretStatus.Found => "found",
        SecretStatus.NotFound => "notFound",
        SecretStatus.Listed => "listed",
        SecretStatus.InvalidCursor => "invalidCursor",
        SecretStatus.InvalidPageSize => "invalidPageSize",
        _ => throw new InvalidDataException("Unknown secret outcome.")
    };
    internal static int Exit(SecretStatus value)
    {
        _ = Outcome(value);
        return value is SecretStatus.Created or SecretStatus.ExistingEquivalent or SecretStatus.Found or SecretStatus.Listed ? 0 : 1;
    }
    internal static CliSecretMetadataData Metadata(SecretMetadata value)
    {
        if (!Identity(value.EnvironmentRecordId) || !Identity(value.SecretId) || value.Purpose != "ProviderAdministrator" ||
            value.Versions.Count > 1 || value.Versions.Any(v => !Identity(v.VersionId) || (v.HasNumber && v.Number < 1)))
            throw new InvalidDataException("Incomplete secret metadata.");
        return new(value.EnvironmentRecordId, value.SecretId,
            value.HasName ? value.Name : null, value.Purpose, value.HasCreatedAtUtc ? value.CreatedAtUtc : null,
            [.. value.Versions.Select(v => new CliSecretVersionData(v.VersionId, v.HasNumber ? v.Number : null, v.HasCreatedAtUtc ? v.CreatedAtUtc : null))]);
    }

    private static bool Identity(string value) => Guid.TryParseExact(value, "D", out var id) && id.Version == 7 &&
        value[19] is ('8' or '9' or 'a' or 'b') && value == id.ToString();
}

internal sealed partial class CliJsonPresenter
{
    internal int PrintSecret(string command, SecretReply reply)
    {
        SecretPresentation.Validate(command, reply);
        var exit = SecretPresentation.Exit(reply.Status);
        Write(command, SecretPresentation.Outcome(reply.Status), new CliSecretData(reply.HasRequestId ? reply.RequestId : null,
            reply.Secret is null ? null : SecretPresentation.Metadata(reply.Secret)), NoDiagnostics, CliOutputJsonContext.Default.CliEnvelopeCliSecretData);
        return exit;
    }
    internal int PrintSecrets(ListSecretsReply reply)
    {
        SecretPresentation.Validate(reply);
        var exit = SecretPresentation.Exit(reply.Status);
        Write("secret list", SecretPresentation.Outcome(reply.Status), new CliSecretListData([.. reply.Items.Select(SecretPresentation.Metadata)],
            string.IsNullOrEmpty(reply.NextCursor) ? null : reply.NextCursor), NoDiagnostics, CliOutputJsonContext.Default.CliEnvelopeCliSecretListData);
        return exit;
    }
}

internal sealed partial class CliTextPresenter
{
    internal int PrintSecret(string command, SecretReply reply)
    {
        SecretPresentation.Validate(command, reply);
        var exit = SecretPresentation.Exit(reply.Status);
        _output.WriteLine(SecretPresentation.Outcome(reply.Status));
        if (reply.Secret is { } secret) PrintSecretMetadata(secret);
        return exit;
    }
    internal int PrintSecrets(ListSecretsReply reply)
    {
        SecretPresentation.Validate(reply);
        var exit = SecretPresentation.Exit(reply.Status);
        _output.WriteLine(SecretPresentation.Outcome(reply.Status));
        foreach (var secret in reply.Items) PrintSecretMetadata(secret);
        if (!string.IsNullOrEmpty(reply.NextCursor)) _output.WriteLine($"nextCursor: {reply.NextCursor}");
        return exit;
    }
    private void PrintSecretMetadata(SecretMetadata secret)
    {
        _output.WriteLine($"{(secret.HasName ? secret.Name : "(unnamed)")} {secret.SecretId} {secret.Purpose}");
        foreach (var version in secret.Versions)
            _output.WriteLine($"  version {(version.HasNumber ? version.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "(unnumbered)")} {version.VersionId}");
    }
}
