using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed partial class CliCommandContext
{
    internal int PrintNaming(string command, ResourceNamingReply reply)
    {
        if (!Enum.IsDefined(reply.Status) || reply.Status == ResourceNamingStatus.Unspecified)
            throw new InvalidDataException("Invalid naming reply.");
        var success = reply.Status is ResourceNamingStatus.Created or ResourceNamingStatus.Found or ResourceNamingStatus.Changed or ResourceNamingStatus.Unchanged;
        if (success && (reply.Mode is not ("exact" or "prefixed") || reply.Mode == "exact" && reply.HasPrefix ||
            reply.Mode == "prefixed" && (!reply.HasPrefix || reply.Prefix.Length is 0 or > 128) ||
            reply.HasName != reply.HasRevision || reply.HasRevision && reply.Revision <= 0 ||
            reply.HasToken && (!Guid.TryParseExact(reply.Token, "D", out var token) || token == Guid.Empty)))
            throw new InvalidDataException("Invalid naming state.");
        var outcome = reply.Status.ToString();
        outcome = char.ToLowerInvariant(outcome[0]) + outcome[1..];
        if (jsonOutput()) output.WriteLine(JsonSerializer.Serialize(new CliEnvelope<CliNamingState>(1, command, outcome,
            success ? new(reply.HasName ? reply.Name : null, reply.HasRevision ? reply.Revision : null, reply.Mode,
                reply.HasPrefix ? reply.Prefix : null, reply.HasToken ? reply.Token : null, reply.Actor, reply.ChangedAtUtc) : null, []), CliNamingJsonContext.Default.CliEnvelopeCliNamingState));
        else
        {
            output.WriteLine($"{command}: {outcome}");
            if (success)
            {
                output.WriteLine(reply.HasName ? $"Policy: {reply.Name}@{reply.Revision}" : "Policy: canonical exact default");
                output.WriteLine($"Mode: {reply.Mode}");
                if (reply.HasPrefix) output.WriteLine($"Prefix: {reply.Prefix}");
                if (reply.HasToken) output.WriteLine($"Token: {reply.Token}");
            }
        }
        return success ? 0 : reply.Status == ResourceNamingStatus.StoreUnavailable ? CliApplication.TransportError : 1;
    }

    internal Task<int> CreateNamingAsync(string? environment, string name, long revision, string file, CancellationToken token) =>
        ExecuteSecretAsync(environment, async (client, env, ct) =>
        {
            var bytes = await CliApplication.ReadEnvelopeAsync(file, ct);
            if (bytes.Length > 1024) throw new InvalidDataException("Naming declaration exceeds 1024 bytes.");
            return PrintNaming("resource-naming create", await client.CreateResourceNamingAsync(new()
            { Reference = new() { Environment = env, Name = name, Revision = revision }, DeclarationJson = new UTF8Encoding(false, true).GetString(bytes) }, ct));
        }, token);

    internal async Task<int> SelectNamingAsync(string? environment, string alias, string name, long revision, bool absent, string? match, CancellationToken token)
    {
        if (absent && match is not null || match is not null && (!Guid.TryParseExact(match, "D", out var id) || id == Guid.Empty))
            return CliApplication.UsageError;
        var started = false;
        var result = await ExecuteSecretAsync(environment, async (client, env, ct) =>
        {
            var binding = await client.GetProviderAliasAsync(new() { Environment = env, Alias = alias }, ct);
            _ = ProviderAccessPresentation.Data("provider alias get", binding);
            if (binding.Binding is not null && binding.Binding.Alias != alias) throw new InvalidDataException("Foreign binding reply.");
            if (binding.Binding is null) return PrintProviderAccess("provider alias get", binding);
            var request = new SelectResourceNamingRequest
            {
                Binding = new() { Environment = env, Alias = alias },
                Name = name,
                Revision = revision,
                ExpectedProviderInstanceId = binding.Binding.InstanceId,
                ExpectedConfigurationRevision = binding.Binding.Revision
            };
            if (absent) request.ExpectedAbsence = true;
            else if (match is not null) request.ExpectedToken = match;
            else
            {
                var current = await client.GetResourceNamingSelectionAsync(new() { Environment = env, Alias = alias }, ct);
                if (current.Status != ResourceNamingStatus.Found) return PrintNaming("resource-naming selection", current);
                if (current.HasToken) request.ExpectedToken = current.Token;
                else request.ExpectedAbsence = true;
            }
            started = true;
            return PrintNaming("resource-naming select", await client.SelectResourceNamingAsync(request, ct));
        }, token);
        if (started && result == CliApplication.TransportError)
            error.WriteLine("Selection outcome is uncertain. Read resource-naming selection and compare the exact revision. Do not blindly resubmit; equality proves current state, not which request wrote it.");
        return result;
    }
}

internal sealed record CliNamingState(string? Name, long? Revision, string Mode, string? Prefix, string? Token, string Actor, string ChangedAtUtc);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CliEnvelope<CliNamingState>))]
internal partial class CliNamingJsonContext : JsonSerializerContext;
