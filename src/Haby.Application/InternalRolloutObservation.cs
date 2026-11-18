using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haby.Application;

internal enum RolloutOutcome { Ready, Pending, Conflict, Unavailable, Refused, NotSubmitted, NotObserved }
internal enum RolloutBudget { Active, Expired }
internal enum RolloutStop { Complete, CallLimit, RequestDeadline, Canceled, Interrupted }
internal sealed record RolloutObservation(RolloutOutcome Outcome, string Code, bool Temporary = false,
    string? Contract = null, string? Evidence = null);

/// <summary>Separate from uncertain-write recovery. Pure authority methods never access material or network.</summary>
internal interface IInternalReadinessHandler
{
    int ObservationSeconds(string intent, string receipt);
    bool ValidateObservation(string intent, string receipt, RolloutObservation observation);
    ValueTask<RolloutObservation> ReadinessAsync(InternalActionContext context, string intent, string receipt, CancellationToken token);
    ValueTask<RolloutObservation> ReadinessAsync(InternalActionContext context, string intent, string receipt,
        IReadinessReadAdmission admission, CancellationToken token) =>
        admission.RunAsync(ct => ReadinessAsync(context, intent, receipt, ct), token);
}

internal sealed record RolloutOptions(int MaximumCalls = 64, int MaximumRounds = 32,
    int ReadTimeoutMilliseconds = 10000, int PollMilliseconds = 1000, int RequestMilliseconds = 30000)
{
    internal void Validate()
    {
        if (MaximumCalls is < 1 or > 512 || MaximumRounds is < 1 or > 512 ||
            ReadTimeoutMilliseconds is < 100 or > 10000 || PollMilliseconds is < 100 or > 10000 ||
            RequestMilliseconds is < 1 or > 300000) throw new ArgumentException("Invalid observation bounds.");
    }
}
internal sealed record RolloutMember(string Subject, Guid? Attempt, DateTimeOffset? Deadline,
    RolloutBudget Budget, RolloutObservation Observation);
/// <summary>SavedReady is historical. Replayed results performed no fresh read; Ready belongs to the original request.</summary>
internal sealed record RolloutResult(bool Ready, Guid? Submitted, Guid? SavedReady, RolloutStop Stop, RolloutMember[] Members, bool Replayed = false, Guid? Retirement = null, bool Applied = false, long? AppliedGeneration = null, string CompletionReason = "not-ready");
internal sealed record RolloutResume(string Subject, Guid ExpectedAttempt);
internal sealed record RolloutOrigin(string Subject, Guid Managed, string Operation, Guid DispatchAttempt,
    Guid? Confirmation, string ReceiptContract, string ReceiptHash);
internal sealed record SubmittedProof(int Format, string Stage, Guid Deployment, long Generation,
    string InputFingerprint, RolloutOrigin[] Members);
internal sealed record ReadyMember(RolloutOrigin Origin, Guid Attempt, Guid Observation);
internal sealed record ReadyProof(int Format, string Stage, Guid Deployment, long Generation,
    string InputFingerprint, Guid Submitted, ReadyMember[] Members);
internal sealed record RolloutRequest(string Action, string Actor, RolloutOptions? Options, RolloutResume[] Members,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RecipientObservationOptions? Recipients = null);

internal static class RolloutJson
{
    internal static string Encode(RolloutResult value) => JsonSerializer.Serialize(value, RolloutMetadata.Default.RolloutResult);
    internal static RolloutResult Decode(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 2097152) throw new InvalidDataException("Observation result too large.");
        var result = JsonSerializer.Deserialize(json, RolloutMetadata.Default.RolloutResult)
            ?? throw new InvalidDataException("Observation result unavailable.");
        if (result.CompletionReason is not ("not-ready" or "applied" or "inventory-conflict" or "prerequisite-incomplete" or
                "old-recipients-present" or "recipient-evidence-incomplete" or "generation-superseded" or "current-authority-unavailable" or "unsupported-inventory" or "commit-unconfirmed") ||
            result.Retirement == Guid.Empty || result.AppliedGeneration is <= 0 ||
            result.Applied && (!result.Ready || result.Retirement is null || result.AppliedGeneration is null || result.CompletionReason != "applied") ||
            !result.Applied && result.CompletionReason == "applied" || !Enum.IsDefined(result.Stop) || result.Members is not { Length: > 0 and <= 512 } ||
            result.Members.Any(x => x is null || x.Subject is not { Length: > 0 and <= 256 } || x.Observation is null ||
                !Enum.IsDefined(x.Budget) || !Enum.IsDefined(x.Observation.Outcome) ||
                x.Observation.Code is not { Length: > 0 and <= 128 } || x.Observation.Code.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.')) ||
                x.Observation.Temporary && x.Observation.Outcome != RolloutOutcome.Unavailable ||
                (x.Attempt is null) != (x.Deadline is null)) ||
            result.Members.Select(x => x.Subject).Distinct(StringComparer.Ordinal).Count() != result.Members.Length ||
            result.Ready && (result.Submitted is null || result.SavedReady is null || result.Stop != RolloutStop.Complete ||
                result.Members.Any(x => x.Observation.Outcome != RolloutOutcome.Ready))) throw new InvalidDataException("Observation result invalid.");
        return result;
    }
    internal static string Encode(RolloutRequest value) => JsonSerializer.Serialize(value, RolloutMetadata.Default.RolloutRequest);
    internal static string Encode(RolloutOrigin value) => JsonSerializer.Serialize(value, RolloutMetadata.Default.RolloutOrigin);
    internal static string Encode(RolloutObservation value) => JsonSerializer.Serialize(value, RolloutMetadata.Default.RolloutObservation);
    internal static RolloutObservation Observation(string json) => JsonSerializer.Deserialize(json, RolloutMetadata.Default.RolloutObservation)
        ?? throw new InvalidDataException("Observation unavailable.");
    internal static string Encode(SubmittedProof value) => JsonSerializer.Serialize(value, RolloutMetadata.Default.SubmittedProof);
    internal static string Encode(ReadyProof value) => JsonSerializer.Serialize(value, RolloutMetadata.Default.ReadyProof);
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(RolloutResult))]
[JsonSerializable(typeof(RolloutRequest))]
[JsonSerializable(typeof(RolloutOrigin))]
[JsonSerializable(typeof(RolloutObservation))]
[JsonSerializable(typeof(SubmittedProof))]
[JsonSerializable(typeof(ReadyProof))]
internal partial class RolloutMetadata : JsonSerializerContext;
