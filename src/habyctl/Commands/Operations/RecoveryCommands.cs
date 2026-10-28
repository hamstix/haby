using System.CommandLine;
using System.Text;
using Haby.Operator.Contracts;

namespace Haby.Cli.Commands.Operations;

internal static class RecoveryCommands
{
    internal static Command Create(CliCommandContext context)
    {
        var group = new Command("recovery", "Inspect or explicitly assign an existing target after uncertainty.");
        group.SetAction(_ => context.MissingSubcommand());
        foreach (var assign in new[] { false, true })
        {
            var deployment = new Argument<string>("deployment-id");
            var generation = new Argument<long>("generation");
            var resource = new Argument<string>("resource-id");
            var action = new Option<string>("--action") { Required = true, Description = "Exact action (create)." };
            var command = new Command(assign ? "assign-existing" : "inspect", assign
                ? "Accept provenance responsibility for one exact attempt. Never dispatches Create."
                : "Read the safe target, uncertainty and receipt without changing state.")
                { deployment, generation, resource, action };
            var environment = new Option<string>("--environment-record-id") { Required = true };
            var attempt = new Option<string>("--expected-attempt-id") { Required = true };
            var acknowledgment = new Option<bool>("--acknowledge-assignment")
            {
                Required = true,
                Description = "Affirmatively accept assignment and provenance responsibility."
            };
            var reason = new Option<string>("--reason")
            {
                Required = true,
                Description = "Controlled reason: operatorAcceptedExistingDatabase."
            };
            var note = new Option<string>("--note-file")
            {
                Required = true,
                Description = "UTF-8 audit note, at most 2048 bytes and 512 characters. Exclude secrets."
            };
            if (assign)
            {
                command.Options.Add(environment); command.Options.Add(attempt); command.Options.Add(acknowledgment);
                command.Options.Add(reason); command.Options.Add(note);
            }
            command.SetAction((parse, token) =>
            {
                if (parse.GetRequiredValue(action) != "create") return Task.FromResult(context.InvalidOperationAction());
                var key = new OperationRequest
                {
                    DeploymentId = parse.GetRequiredValue(deployment),
                    Generation = parse.GetRequiredValue(generation),
                    ResourceId = parse.GetRequiredValue(resource),
                    Action = OperationAction.Create
                };
                if (assign && (!parse.GetValue(acknowledgment) || parse.GetRequiredValue(reason) != "operatorAcceptedExistingDatabase"))
                    return Task.FromResult(context.InvalidRecoveryInput());
                return context.ExecuteAsync(async (client, ct) =>
                {
                    if (!assign) return context.PrintRecovery("operation recovery inspect", await client.InspectOperationRecoveryAsync(key, ct));
                    var request = new AssignExistingOperationRequest
                    {
                        Key = key,
                        EnvironmentRecordId = parse.GetRequiredValue(environment),
                        ExpectedAttemptId = parse.GetRequiredValue(attempt),
                        Acknowledged = true,
                        Reason = RecoveryReason.OperatorAcceptedExistingDatabase,
                        Note = await ReadNoteAsync(parse.GetRequiredValue(note), ct)
                    };
                    var preview = await client.InspectOperationRecoveryAsync(key, ct);
                    // Never replace caller-supplied expected identities with those observed in a newer preview.
                    context.ShowRecoverySummary(preview);
                    if (preview.Status != RecoveryStatus.Found)
                        return context.PrintRecovery("operation recovery assign-existing", preview);
                    if (!key.Equals(preview.Preview?.Operation?.Key) || preview.Preview.Operation.EnvironmentRecordId != request.EnvironmentRecordId ||
                        (preview.Preview.HasUncertainAttemptId ? preview.Preview.UncertainAttemptId : preview.Receipt?.AttemptId) != request.ExpectedAttemptId)
                        return context.PrintRecovery("operation recovery assign-existing", new RecoveryReply { Status = RecoveryStatus.Rejected });
                    return context.PrintRecovery("operation recovery assign-existing", await client.AssignExistingOperationAsync(request, ct));
                }, token);
            });
            group.Subcommands.Add(command);
        }
        return group;
    }

    private static async Task<string> ReadNoteAsync(string path, CancellationToken cancellationToken)
    {
        // Bound the actual read, including files that grow after opening or have no useful Length.
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 2049, useAsync: true);
        var bytes = new byte[2049];
        var length = 0;
        while (length < bytes.Length)
        {
            var count = await file.ReadAsync(bytes.AsMemory(length), cancellationToken);
            if (count == 0) break;
            length += count;
        }
        if (length is 0 or > 2048) throw new InvalidDataException("Invalid recovery note file.");
        string note;
        try { note = new UTF8Encoding(false, true).GetString(bytes, 0, length); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Invalid recovery note file."); }
        if (note.Length > 512) throw new InvalidDataException("Invalid recovery note file.");
        return note;
    }
}
