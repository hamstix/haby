using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using ApplicationId = Haby.Domain.ApplicationId;
using Haby.Manifest;
using Haby.Operator.Contracts;
using Target = Haby.Domain.ProfileAssignmentTarget;
using WireTarget = Haby.Operator.Contracts.ProfileAssignmentTarget;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override Task<ProfileAssignmentReply> ValidateProfileAssignment(ProfilePayloadRequest request, ServerCallContext context)
        => SafeProfileOperation(() =>
        {
            var parsed = ProfileAssignmentAdministration.Validate(request.Payload.Memory, context.CancellationToken);
            return ValueTask.FromResult(parsed.IsSuccess ? new ProfileAssignmentReply
            { Status = ProfileAssignmentStatus.Valid, Target = AssignmentTarget(parsed.Declaration!.Target) } : InvalidAssignment(parsed));
        }, context.CancellationToken);

    public override Task<ProfileAssignmentReply> GetProfileAssignment(GetProfileAssignmentRequest request, ServerCallContext context)
        => SafeProfileOperation(async () =>
        {
            Target target;
            try { target = ReadAssignmentTarget(request.Target); }
            catch (ArgumentException) { return new() { Status = ProfileAssignmentStatus.InvalidContext }; }
            return AssignmentReply(await assignments.GetAsync(request.Environment, target, context.CancellationToken));
        }, context.CancellationToken);

    public override Task<ProfileAssignmentReply> ApplyProfileAssignment(ApplyProfileAssignmentRequest request, ServerCallContext context)
        => SafeProfileOperation(async () =>
        {
            AssignmentPrecondition precondition;
            try
            {
                precondition = request.PreconditionCase switch
                {
                    ApplyProfileAssignmentRequest.PreconditionOneofCase.ExpectedAbsence when request.ExpectedAbsence => new AssignmentPrecondition.ExpectedAbsence(),
                    ApplyProfileAssignmentRequest.PreconditionOneofCase.ExpectedConcurrencyToken => new AssignmentPrecondition.ExpectedConcurrencyToken(AssignmentToken.Parse(request.ExpectedConcurrencyToken)),
                    _ => throw new ArgumentException("Missing precondition.")
                };
            }
            catch (ArgumentException) { return new() { Status = ProfileAssignmentStatus.InvalidContext }; }
            return AssignmentReply(await assignments.ApplyAsync(request.Payload.Memory, request.Environment,
                request.HasApplicationVersion ? request.ApplicationVersion : null, precondition,
                OperatorHostApplication.TechnicalActorId, context.CancellationToken));
        }, context.CancellationToken);

    private static Target ReadAssignmentTarget(WireTarget? target) => target switch
    {
        { Kind: "environment", ApplicationId: "", ComponentRef: "" } => new Target.Environment(),
        { Kind: "application", ComponentRef: "" } => new Target.Application(ApplicationId.Create(target.ApplicationId)),
        { Kind: "component" } => new Target.Component(ApplicationId.Create(target.ApplicationId), ManifestLocalId.Create(target.ComponentRef)),
        _ => throw new ArgumentException("Invalid target.")
    };
    private static WireTarget AssignmentTarget(Target target) => target switch
    {
        Target.Environment => new() { Kind = "environment" },
        Target.Application a => new() { Kind = "application", ApplicationId = a.ApplicationId.Value },
        Target.Component c => new() { Kind = "component", ApplicationId = c.ApplicationId.Value, ComponentRef = c.ComponentRef.Value },
        _ => throw new ArgumentException("Invalid target.")
    };
    private static ProfileAssignmentReply AssignmentReply(AssignmentResult result)
    {
        if (result.Report is { } report) return InvalidAssignment(report);
        var reply = new ProfileAssignmentReply
        {
            Status = result.Outcome switch
            {
                AssignmentOutcome.Created => ProfileAssignmentStatus.Created,
                AssignmentOutcome.Updated => ProfileAssignmentStatus.Updated,
                AssignmentOutcome.Unchanged => ProfileAssignmentStatus.Unchanged,
                AssignmentOutcome.Found => ProfileAssignmentStatus.Found,
                AssignmentOutcome.NotFound => ProfileAssignmentStatus.NotFound,
                AssignmentOutcome.Conflict => ProfileAssignmentStatus.Conflict,
                AssignmentOutcome.InvalidContext => ProfileAssignmentStatus.InvalidContext,
                AssignmentOutcome.EnvironmentNotFound => ProfileAssignmentStatus.EnvironmentNotFound,
                AssignmentOutcome.ApplicationNotFound => ProfileAssignmentStatus.ApplicationNotFound,
                AssignmentOutcome.ApplicationVersionNotFound => ProfileAssignmentStatus.ApplicationVersionNotFound,
                AssignmentOutcome.ComponentNotFound => ProfileAssignmentStatus.ComponentNotFound,
                AssignmentOutcome.ProfileRevisionNotFound => ProfileAssignmentStatus.ProfileRevisionNotFound,
                _ => throw new InvalidOperationException("Unknown assignment outcome.")
            }
        };
        if (result.Assignment is { } state)
        {
            reply.Target = AssignmentTarget(state.Declaration.Target);
            reply.Assignment = new()
            {
                Target = reply.Target.Clone(),
                Environment = state.Environment.Name.Value,
                EnvironmentRecordId = state.Environment.RecordId.ToString(),
                ConcurrencyToken = state.Token.ToString(),
                TechnicalActorId = state.Audit.TechnicalActorId,
                ChangedAtUtc = state.Audit.ChangedAtUtc.ToString("O"),
                CheckedRevision = state.Audit.CheckedRevision is { } r ? new()
                { ApplicationRecordId = r.ApplicationRecordId.ToString(), RevisionNumber = r.RevisionNumber.Value } : null
            };
            foreach (var decision in state.Declaration.Decisions.OrderBy(x => x.ProfileId.Value, StringComparer.Ordinal))
            {
                var item = new ProfileAssignmentDecision { ProfileId = decision.ProfileId.Value };
                if (decision is ProfileDecision.Use use) item.UseRevision = use.Reference.Revision;
                else item.Disable = true;
                reply.Assignment.Decisions.Add(item);
            }
        }
        return reply;
    }
    private static ProfileAssignmentReply InvalidAssignment(ProfileWireResult<ConfigurationProfileAssignment> report)
    {
        // Assignment paths contain only closed fields/indexes. Arbitrary input members stay at root.
        var diagnostics = report.Diagnostics.Select(x => (x.Code, Path: x.Path.Length <= 1024 && x.Path.Split('/').All(s =>
            s is "" or "apiVersion" or "kind" or "spec" or "target" or "applicationId" or "componentRef" or
                "decisions" or "profileRef" or "profileId" or "revision" || s.Length <= 3 && s.All(char.IsAsciiDigit)) ? x.Path : ""))
            .Distinct().OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal).ToArray();
        var reply = new ProfileAssignmentReply
        {
            Status = ProfileAssignmentStatus.InvalidDeclaration,
            ErrorCount = report.ErrorCount,
            DiagnosticsTruncated = report.ErrorCount > diagnostics.Length
        };
        reply.Diagnostics.AddRange(diagnostics.Select(x => new ProfileDiagnostic { Code = x.Code, Path = x.Path }));
        return reply;
    }
}
