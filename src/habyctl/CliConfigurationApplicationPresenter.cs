using System.Text;
using System.Text.Json;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliConfigurationApplicationTarget(string DeploymentId, long Generation);
internal sealed record CliConfigurationApplicationItem(CliConfigurationDocumentTarget Target, string Outcome, CliConfigurationDocumentArtifact? Artifact);
internal sealed record CliConfigurationApplicationData(CliConfigurationApplicationTarget? Target, int SelectedCount, CliConfigurationApplicationItem[] Items);

internal static class ConfigurationApplicationPresentation
{
    internal const int MaxItems = 512;
    internal const int MaxBytes = 8 * 1024 * 1024;
    internal static (string Outcome, int Exit, CliConfigurationApplicationData Data) Check(ConfigurationApplicationRequest request, ConfigurationApplicationReply reply)
    {
        var processed = reply.Status is ConfigurationApplicationStatus.Succeeded or ConfigurationApplicationStatus.Failed;
        if (!Equals(request, reply.Selector) || !Enum.IsDefined(reply.Status) || reply.Status == ConfigurationApplicationStatus.Unspecified ||
            reply.CalculateSize() > MaxBytes || reply.SelectedCount != reply.Items.Count || reply.Items.Count > MaxItems ||
            processed && reply.Target is null || !processed && reply.Items.Count != 0)
            throw new InvalidDataException("Invalid Application configuration reply.");
        CliConfigurationApplicationTarget? target = null;
        if (reply.Target is { } t)
        {
            if (!Guid.TryParseExact(t.DeploymentId, "D", out var id) || id == Guid.Empty || id.Version != 7 || t.Generation <= 0 ||
                request.HasGeneration && t.Generation != request.Generation) throw new InvalidDataException("Foreign Application configuration target.");
            target = new(t.DeploymentId, t.Generation);
        }
        var items = new List<CliConfigurationApplicationItem>(reply.Items.Count);
        var failed = false;
        (string Component, string Document)? previous = null;
        var revisions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in reply.Items)
        {
            if (item.Target is not { } exact || target is null || exact.DeploymentId != target.DeploymentId || exact.Generation != target.Generation ||
                !LocalId(exact.ComponentId) || !LocalId(exact.DocumentId)) throw new InvalidDataException("Foreign Application configuration item.");
            if (previous is { } p && (string.CompareOrdinal(p.Component, exact.ComponentId) > 0 || p.Component == exact.ComponentId && string.CompareOrdinal(p.Document, exact.DocumentId) >= 0))
                throw new InvalidDataException("Invalid Application configuration inventory order.");
            previous = (exact.ComponentId, exact.DocumentId);
            var success = item.Status is ConfigurationDocumentStatus.Created or ConfigurationDocumentStatus.Existing;
            if (failed != (item.Status == ConfigurationDocumentStatus.NotAttempted) || item.Status is ConfigurationDocumentStatus.Found or ConfigurationDocumentStatus.Unspecified ||
                !Enum.IsDefined(item.Status) || success != (item.Artifact is not null)) throw new InvalidDataException("Invalid Application configuration outcome sequence.");
            CliConfigurationDocumentArtifact? artifact = null;
            if (success)
            {
                var selector = new ConfigurationDocumentRequest
                {
                    Environment = request.Environment,
                    ApplicationId = request.ApplicationId,
                    ComponentId = exact.ComponentId,
                    DocumentId = exact.DocumentId,
                    Generation = target.Generation
                };
                artifact = ConfigurationDocumentPresentation.Check("materialize", selector, new()
                { Selector = selector.Clone(), Target = exact, Status = item.Status, Artifact = item.Artifact }).Data.Artifact;
                if (!revisions.Add(artifact!.RevisionId)) throw new InvalidDataException("Duplicate Application configuration revision.");
            }
            if (!success) failed = true;
            var status = item.Status.ToString();
            items.Add(new(new(exact.DeploymentId, exact.Generation, exact.ComponentId, exact.DocumentId), char.ToLowerInvariant(status[0]) + status[1..], artifact));
        }
        if (processed && (reply.Status == ConfigurationApplicationStatus.Failed) != failed)
            throw new InvalidDataException("Incomplete Application configuration result.");
        var outcome = reply.Status.ToString();
        return (char.ToLowerInvariant(outcome[0]) + outcome[1..], reply.Status == ConfigurationApplicationStatus.Succeeded ? 0 : 1,
            new(target, reply.SelectedCount, items.ToArray()));
    }

    private static bool LocalId(string text) => text.Length is > 0 and <= 63 && text[0] is >= 'a' and <= 'z' &&
        text[^1] != '-' && !text.Contains("--", StringComparison.Ordinal) && text.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}

internal sealed partial class CliCommandContext
{
    internal Task<int> ExecuteConfigurationApplicationAsync(string application, string? environment, long? generation, CancellationToken token)
    {
        if (generation <= 0) return ExecuteRevisionAsync(0, (_, _) => Task.FromResult(0), token);
        return ExecuteSecretAsync(environment, async (client, env, ct) =>
        {
            var request = new ConfigurationApplicationRequest { Environment = env, ApplicationId = application };
            if (generation.HasValue) request.Generation = generation.Value;
            var reply = await client.MaterializeApplicationConfigurationAsync(request, ct);
            var result = ConfigurationApplicationPresentation.Check(request, reply);
            var json = JsonSerializer.Serialize(new CliEnvelope<CliConfigurationApplicationData>(1, "configuration materialize", result.Outcome, result.Data, []),
                CliOutputJsonContext.Default.CliEnvelopeCliConfigurationApplicationData);
            var text = new StringBuilder().AppendLine($"configuration materialize --all: {result.Outcome}; {env}/{application}; generation={result.Data.Target?.Generation}; deployment={result.Data.Target?.DeploymentId}; documents={result.Data.SelectedCount}");
            foreach (var item in result.Data.Items)
                text.AppendLine($"{item.Target.ComponentId}/{item.Target.DocumentId}: {item.Outcome}; revision={item.Artifact?.RevisionId}");
            var rendered = jsonOutput() ? json + output.NewLine : text.ToString();
            if (Encoding.UTF8.GetByteCount(rendered) > ConfigurationApplicationPresentation.MaxBytes)
                throw new InvalidDataException("Oversized Application configuration response.");
            output.Write(rendered);
            return result.Exit;
        }, token);
    }
}
