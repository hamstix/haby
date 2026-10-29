using Grpc.Core;
using Haby.Application;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override async Task<ProviderAccessReply> CheckProvider(CheckProviderRequest request, ServerCallContext context) =>
        AccessReply(await providerAccess.CheckAsync(request.Environment, new(request.Name, null, request.Revision), context.CancellationToken));
    public override async Task<ProviderAccessReply> GetProviderAlias(GetProviderAliasRequest request, ServerCallContext context) =>
        AccessReply(await providerAccess.GetAliasAsync(request.Environment, request.Alias, context.CancellationToken));
    public override async Task<ProviderAccessReply> SetProviderAlias(SetProviderAliasRequest request, ServerCallContext context)
    {
        if (request.Target is null || request.ExpectationCase == SetProviderAliasRequest.ExpectationOneofCase.None ||
            (request.ExpectationCase == SetProviderAliasRequest.ExpectationOneofCase.ExpectedAbsent && !request.ExpectedAbsent))
            return new() { Status = ProviderAccessStatus.InvalidRequest };
        return AccessReply(await providerAccess.SetAliasAsync(request.Environment, request.Alias, Selector(request.Target),
            request.ExpectedAbsent, request.Expected is null ? null : Selector(request.Expected),
            OperatorHostApplication.TechnicalActorId, context.CancellationToken));
    }
    private static Haby.Application.ProviderSelector Selector(Haby.Operator.Contracts.ProviderSelector value) => new(
        value.SelectionCase == Haby.Operator.Contracts.ProviderSelector.SelectionOneofCase.Name ? value.Name : null,
        value.SelectionCase == Haby.Operator.Contracts.ProviderSelector.SelectionOneofCase.InstanceId ? value.InstanceId : null, value.Revision);
    private static ProviderAccessReply AccessReply(ProviderAccessResult result)
    {
        var reply = new ProviderAccessReply
        {
            Status = result.Outcome switch
            {
                ProviderAccessOutcome.Ready => ProviderAccessStatus.Ready,
                ProviderAccessOutcome.Found => ProviderAccessStatus.Found,
                ProviderAccessOutcome.NotFound => ProviderAccessStatus.NotFound,
                ProviderAccessOutcome.Changed => ProviderAccessStatus.Changed,
                ProviderAccessOutcome.Unchanged => ProviderAccessStatus.Unchanged,
                ProviderAccessOutcome.InvalidRequest => ProviderAccessStatus.InvalidRequest,
                ProviderAccessOutcome.EnvironmentNotFound => ProviderAccessStatus.EnvironmentNotFound,
                ProviderAccessOutcome.TargetUnavailable => ProviderAccessStatus.TargetUnavailable,
                ProviderAccessOutcome.PreflightFailed => ProviderAccessStatus.PreflightFailed,
                ProviderAccessOutcome.StaleExpectation => ProviderAccessStatus.StaleExpectation,
                ProviderAccessOutcome.InstanceAlreadyBound => ProviderAccessStatus.InstanceAlreadyBound,
                _ => ProviderAccessStatus.StorageUnavailable
            },
            Binding = result.Alias is { } alias ? new() { Alias = alias.Alias.Value, InstanceId = alias.Binding.InstanceId.ToString(), Revision = alias.Binding.RevisionNumber.Value } : null
        };
        if (result.Preflight is { } check) reply.Preflight = check.Outcome switch
        {
            ProviderPreflightOutcome.Ready => ProviderPreflightStatus.Ready,
            ProviderPreflightOutcome.RevisionUnavailable => ProviderPreflightStatus.RevisionUnavailable,
            ProviderPreflightOutcome.SecretUnavailable => ProviderPreflightStatus.SecretUnavailable,
            ProviderPreflightOutcome.AuthenticationFailed => ProviderPreflightStatus.AuthenticationFailed,
            ProviderPreflightOutcome.ServerUnavailable => ProviderPreflightStatus.ServerUnavailable,
            ProviderPreflightOutcome.InsufficientPrivilege => ProviderPreflightStatus.InsufficientPrivilege,
            ProviderPreflightOutcome.DeadlineExceeded => ProviderPreflightStatus.DeadlineExceeded,
            ProviderPreflightOutcome.HandlerUnavailable => ProviderPreflightStatus.HandlerUnavailable,
            _ => ProviderPreflightStatus.ConfigurationUnavailable
        };
        return reply;
    }
}
