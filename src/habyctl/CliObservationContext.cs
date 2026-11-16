using Google.Protobuf;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed partial class CliCommandContext
{
    internal Task<int> ObserveGenerationAsync(string deployment, long generation, string? suppliedRequest,
        GenerationObservationSettings? settings, string[]? members, CancellationToken token)
    {
        var request = Guid.NewGuid();
        var references = new List<GenerationResumeMember>();
        if (suppliedRequest is not null && (!Guid.TryParse(suppliedRequest, out request) || request == Guid.Empty)) return Task.FromResult(Invalid());
        if (generation <= 0) return Task.FromResult(Invalid());
        if (settings is not null && (settings.HasMaximumCalls && settings.MaximumCalls is < 1 or > 512 ||
            settings.HasMaximumRounds && settings.MaximumRounds is < 1 or > 512 ||
            settings.HasReadTimeoutMilliseconds && settings.ReadTimeoutMilliseconds is < 100 or > 10000 ||
            settings.HasPollMilliseconds && settings.PollMilliseconds is < 100 or > 10000 ||
            settings.HasRequestMilliseconds && settings.RequestMilliseconds is < 1 or > 300000)) return Task.FromResult(Invalid());
        if (members is not null)
        {
            if (members.Length is < 1 or > 512) return Task.FromResult(Invalid());
            foreach (var member in members)
            {
                var parts = member.Split('=');
                if (parts.Length != 2 || !Guid.TryParse(parts[1], out var attempt) || attempt == Guid.Empty ||
                    parts[0].Split('/') is not ["Workload", { Length: > 0 }, { Length: > 0 }] ||
                    references.Any(x => x.Subject == parts[0])) return Task.FromResult(Invalid());
                references.Add(new() { Subject = parts[0], ExpectedAttemptId = attempt.ToString("D") });
            }
        }
        return Invoke();
        int Invalid()
        {
            error.WriteLine("Invalid observation input. Use a nonempty request UUID, bounded settings and unique exact Workload subject=attempt UUID members.");
            return CliApplication.UsageError;
        }
        async Task<int> Invoke()
        {
            var key = request.ToString("D"); var received = false;
            var exit = await ExecuteAsync(async (client, ct) =>
            {
                GenerationReadinessReply reply;
                if (members is null)
                {
                    var wire = new GenerationWaitRequest { DeploymentId = deployment, Generation = generation, RequestId = key, Settings = settings };
                    if (wire.CalculateSize() > 131072) return Invalid();
                    reply = await client.WaitGenerationAsync(wire, ct);
                }
                else
                {
                    var wire = new GenerationResumeRequest { DeploymentId = deployment, Generation = generation, RequestId = key };
                    wire.Members.AddRange(references);
                    if (wire.CalculateSize() > 131072) return Invalid();
                    reply = await client.ResumeGenerationAsync(wire, ct);
                }
                if ((reply.CurrentApplied || reply.OriginalRequestApplied) && (!reply.HasAppliedGeneration || reply.AppliedGeneration != generation))
                    throw new InvalidDataException("Completion generation mismatch.");
                if (reply.RequestId != key) throw new InvalidDataException("Observation request identity mismatch.");
                if ((reply.CurrentApplied || reply.OriginalRequestApplied) && (!reply.HasAppliedGeneration || reply.AppliedGeneration != generation))
                    throw new InvalidDataException("Completion generation identity mismatch.");
                var result = jsonOutput() ? jsonPresenter.PrintObservation(members is not null, reply) : presenter.PrintObservation(members is not null, reply);
                received = true; return result;
            }, token);
            if (!received)
                error.WriteLine($"Observation response unavailable. RequestId: {key}. No completion or renewal is confirmed. Repeat the same command with identical inputs and --request-id {key} to inspect/replay that request; do not generate a replacement key to infer the old outcome.");
            return exit;
        }
    }
}
