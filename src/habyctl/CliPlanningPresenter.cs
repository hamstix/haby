using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliChangeSet(string ChangeSetId, string EnvironmentRecordId, CliReference Reference,
    string Precondition, string CreatedAtUtc, string TechnicalActorId);
internal sealed record CliPlan(string PlanId, string ChangeSetId, string EnvironmentRecordId, string EnvironmentId,
    CliReference Reference, string ProviderAlias, string ProviderInstanceId, long ConfigurationRevision,
    string ImplementationId, string Fingerprint, string CreatedAtUtc, string ExpiresAtUtc,
    string TechnicalActorId, string? ClosedAtUtc, string? ClosedByTechnicalActorId, CliPlanDescription IntendedWork, CliPlannedOperation[] Operations);
internal sealed record CliPlannedOperation(string OperationId, string ResourceId, string ActionContract, string[] Dependencies);
internal sealed record CliPlanDescription(string Status, CliIntendedAction? Action);
internal sealed record CliIntendedAction(string Kind, string ResourceId, string ResourceType,
    CliIntendedActionTarget Target);
internal sealed record CliIntendedActionTarget(string Name);
internal sealed record CliPlanningData(CliChangeSet? ChangeSet, CliPlan? Plan)
{
    public string[] ConfigurationErrorCodes { get; init; } = [];
}

internal static class PlanningPresentation
{
    internal static string Outcome(PlanningStatus status) => status switch
    {
        PlanningStatus.Created => "created",
        PlanningStatus.Found => "found",
        PlanningStatus.Planned => "planned",
        PlanningStatus.Current => "current",
        PlanningStatus.Closed => "closed",
        PlanningStatus.AlreadyClosed => "alreadyClosed",
        PlanningStatus.InvalidRequest => "invalidRequest",
        PlanningStatus.EnvironmentNotFound => "environmentNotFound",
        PlanningStatus.RevisionNotFound => "revisionNotFound",
        PlanningStatus.RevisionNotOwnedByApplication => "revisionNotOwnedByApplication",
        PlanningStatus.UnsupportedPrecondition => "unsupportedPrecondition",
        PlanningStatus.ChangeSetNotFound => "changeSetNotFound",
        PlanningStatus.ChangeSetStale => "changeSetStale",
        PlanningStatus.AliasNotResolved => "aliasNotResolved",
        PlanningStatus.ProviderRevisionUnavailable => "providerRevisionUnavailable",
        PlanningStatus.ProviderCapabilityMismatch => "providerCapabilityMismatch",
        PlanningStatus.UnsupportedShape => "unsupportedShape",
        PlanningStatus.ConfigurationRefused => "configurationRefused",
        PlanningStatus.PlanNotFound => "planNotFound",
        PlanningStatus.Expired => "expired",
        PlanningStatus.PlanStale => "planStale",
        PlanningStatus.PlanInputUnavailable => "planInputUnavailable",
        _ => throw new InvalidDataException("Unknown server outcome.")
    };

    internal static int Exit(string command, PlanningReply reply)
    {
        _ = Outcome(reply.Status);
        if (reply.Status == PlanningStatus.ConfigurationRefused && (reply.Plan is not null || reply.ChangeSet is not null))
            throw new InvalidDataException("Configuration refusal contains forbidden content.");
        if (reply.ConfigurationErrorCodes.Count > 100 ||
            reply.Status != PlanningStatus.ConfigurationRefused && reply.ConfigurationErrorCodes.Count != 0 ||
            reply.ConfigurationErrorCodes.Any(code => code is not (
                "profile.binding.unsupported" or "profile.component.duplicate" or "profile.contribution.duplicate" or
                "profile.document.duplicate" or "profile.document.file-conflict" or "profile.document.invalid-file-name" or
                "profile.document.not-found" or "profile.document.unsupported" or "profile.revision.not-found" or
                "render.renderer.unknown" or "render.renderer.unavailable" or "render.formatter.unavailable" or
                "render.output.incompatible" or "render.options.invalid" or "profile.rule.duplicate" or "profile.secret-projections.unsupported" or "profile.selection.duplicate" or
                "profile.slot.ambiguous" or "profile.slot.duplicate" or "profile.slot.not-found" or "profile.value.unsupported" or
                "render.projection.invalid-path" or "render.projection.template-collision" or "render.projection.path-conflict")))
            throw new InvalidDataException("Invalid configuration refusal metadata.");
        // Reading an existing immutable Plan succeeds even when checking it would reject execution.
        if (command == "plan get" && reply.Plan is not null) return CliApplication.Success;
        return reply.Status is PlanningStatus.Created or PlanningStatus.Found or PlanningStatus.Planned or PlanningStatus.Current ||
            command == "plan close" && reply.Status is PlanningStatus.Closed or PlanningStatus.AlreadyClosed
            ? CliApplication.Success : CliApplication.Rejected;
    }

    internal static CliPlanningData Data(PlanningReply reply) => new(
        reply.ChangeSet is not { } change ? null : new CliChangeSet(change.ChangeSetId, change.EnvironmentRecordId,
            new CliReference(change.Reference.ApplicationRecordId, change.Reference.RevisionNumber),
            change.Precondition == PlanningPrecondition.ExpectedAbsence ? "expectedAbsence"
                : throw new InvalidDataException("Unsupported server precondition."), change.CreatedAtUtc, change.TechnicalActorId),
        reply.Plan is not { } plan ? null : new CliPlan(plan.PlanId, plan.ChangeSetId, plan.EnvironmentRecordId,
            plan.EnvironmentId, new CliReference(plan.Reference.ApplicationRecordId, plan.Reference.RevisionNumber),
            plan.ProviderAlias, plan.ProviderInstanceId, plan.ConfigurationRevision, plan.ImplementationId,
            plan.Fingerprint, plan.CreatedAtUtc, plan.ExpiresAtUtc, plan.TechnicalActorId,
            plan.HasClosedAtUtc ? plan.ClosedAtUtc : null,
            plan.HasClosedByTechnicalActorId ? plan.ClosedByTechnicalActorId : null, Description(plan.IntendedWork), Operations(plan.Operations)))
    { ConfigurationErrorCodes = reply.ConfigurationErrorCodes.ToArray() };

    internal static CliPlannedOperation[] Operations(IEnumerable<PlannedOperationSummary> operations) =>
        operations.Select(n => new CliPlannedOperation(n.OperationId, n.ResourceId, n.ActionContract, n.Dependencies.ToArray())).ToArray();

    private static CliPlanDescription Description(PlanDescription? description)
    {
        // Missing or unknown review metadata must not fabricate intended work.
        if (description is null) return new("unavailable", null);
        var status = description.Status switch
        {
            PlanDescriptionStatus.Available => "available",
            PlanDescriptionStatus.RevisionNotFound => "revisionNotFound",
            PlanDescriptionStatus.InputCorrupt => "inputCorrupt",
            PlanDescriptionStatus.OperationGraph => "operationGraph",
            PlanDescriptionStatus.UnsupportedShape => "unsupportedShape",
            PlanDescriptionStatus.PinnedInputMismatch => "pinnedInputMismatch",
            PlanDescriptionStatus.ActionDescriberUnavailable => "actionDescriberUnavailable",
            _ => throw new InvalidDataException("Unknown server description outcome.")
        };
        if (description.Status != PlanDescriptionStatus.Available)
        {
            if (description.Action is not null) throw new InvalidDataException("Inconsistent server description.");
            return new(status, null);
        }
        var action = description.Action;
        if (action is null || action.Kind != IntendedActionKind.Create ||
            action.Target is null ||
            string.IsNullOrEmpty(action.ResourceId) || string.IsNullOrEmpty(action.ResourceType) ||
            string.IsNullOrEmpty(action.Target.Name))
            throw new InvalidDataException("Unsupported server intended action.");
        return new(status, new("create", action.ResourceId, action.ResourceType,
            new CliIntendedActionTarget(action.Target.Name)));
    }
}

internal sealed partial class CliJsonPresenter
{
    internal int PrintPlanning(string command, PlanningReply reply)
    {
        var exit = PlanningPresentation.Exit(command, reply);
        Write(command, PlanningPresentation.Outcome(reply.Status), PlanningPresentation.Data(reply), NoDiagnostics,
            CliOutputJsonContext.Default.CliEnvelopeCliPlanningData);
        return exit;
    }
}

internal sealed partial class CliTextPresenter
{
    internal int PrintPlanning(string command, PlanningReply reply)
    {
        var exit = PlanningPresentation.Exit(command, reply);
        var data = PlanningPresentation.Data(reply);
        _output.WriteLine(PlanningPresentation.Outcome(reply.Status));
        foreach (var code in data.ConfigurationErrorCodes) _output.WriteLine(code);
        if (data.ChangeSet is { } change)
        {
            _output.WriteLine($"ChangeSet: {change.ChangeSetId}; Environment record: {change.EnvironmentRecordId}");
            _output.WriteLine($"Revision: {change.Reference.ApplicationRecordId} {change.Reference.RevisionNumber}; precondition: {change.Precondition}");
            _output.WriteLine($"Created UTC: {change.CreatedAtUtc}; technical actor: {change.TechnicalActorId}");
        }
        if (data.Plan is { } plan)
        {
            _output.WriteLine($"Plan: {plan.PlanId}; ChangeSet: {plan.ChangeSetId}");
            _output.WriteLine($"Environment: {plan.EnvironmentId}; record: {plan.EnvironmentRecordId}");
            _output.WriteLine($"Revision: {plan.Reference.ApplicationRecordId} {plan.Reference.RevisionNumber}");
            foreach (var node in plan.Operations)
                _output.WriteLine($"Operation: {node.OperationId}; Resource: {node.ResourceId}; action: {node.ActionContract}; dependencies: {string.Join(", ", node.Dependencies)}");
            _output.WriteLine($"Provider: {plan.ProviderAlias} -> {plan.ProviderInstanceId} revision {plan.ConfigurationRevision}; implementation: {plan.ImplementationId}");
            _output.WriteLine("Intended work only; no Provider preflight, proof of absence or execution guarantee. Commit validation still applies.");
            if (plan.IntendedWork.Action is { } action)
                _output.WriteLine($"Intended action: Create; Resource: {action.ResourceId}; type: {action.ResourceType}; target: {action.Target.Name}");
            else if (plan.IntendedWork.Status == "operationGraph")
                _output.WriteLine("Intended work: supported operation graph; single-action detail is not applicable. Review the operation graph above.");
            else
                _output.WriteLine($"Single-action detail unavailable: {plan.IntendedWork.Status}. Review the operation graph above.");
            _output.WriteLine($"Fingerprint: {plan.Fingerprint}");
            _output.WriteLine($"Created UTC: {plan.CreatedAtUtc}; expires UTC: {plan.ExpiresAtUtc}; technical actor: {plan.TechnicalActorId}");
            if (plan.ClosedAtUtc is not null)
                _output.WriteLine($"Closed UTC: {plan.ClosedAtUtc}; technical actor: {plan.ClosedByTechnicalActorId}");
        }
        return exit;
    }
}
