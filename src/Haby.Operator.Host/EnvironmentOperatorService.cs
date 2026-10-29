using Grpc.Core;
using Haby.Application;
using Haby.Operator.Contracts;

namespace Haby.Operator.Host;

public sealed partial class ManifestOperatorService
{
    public override async Task<EnvironmentReply> CreateEnvironment(EnvironmentNameRequest request, ServerCallContext context) =>
        EnvironmentReply(await environmentAdministration.CreateAsync(request.Name, context.CancellationToken));

    public override async Task<EnvironmentReply> GetEnvironment(EnvironmentNameRequest request, ServerCallContext context) =>
        EnvironmentReply(await environmentAdministration.GetAsync(request.Name, context.CancellationToken));

    public override async Task<ListEnvironmentsReply> ListEnvironments(ListEnvironmentsRequest request, ServerCallContext context)
    {
        var result = await environmentAdministration.ListAsync(request.PageSize, request.Cursor, context.CancellationToken);
        var reply = new ListEnvironmentsReply { Status = EnvironmentStatusOf(result.Outcome), NextCursor = result.NextCursor ?? "" };
        reply.Items.AddRange(result.Items.Select(EnvironmentMetadataOf));
        return reply;
    }

    private static EnvironmentReply EnvironmentReply(EnvironmentResult result) => new()
    {
        Status = EnvironmentStatusOf(result.Outcome),
        Environment = result.Environment is null ? null : EnvironmentMetadataOf(result.Environment)
    };

    private static Haby.Operator.Contracts.EnvironmentMetadata EnvironmentMetadataOf(Haby.Application.EnvironmentMetadata value) =>
        new() { RecordId = value.RecordId.ToString(), Name = value.Name.Value };

    private static EnvironmentStatus EnvironmentStatusOf(EnvironmentOutcome outcome) => outcome switch
    {
        EnvironmentOutcome.Created => EnvironmentStatus.Created,
        EnvironmentOutcome.Existing => EnvironmentStatus.Existing,
        EnvironmentOutcome.Found => EnvironmentStatus.Found,
        EnvironmentOutcome.NotFound => EnvironmentStatus.NotFound,
        EnvironmentOutcome.InvalidName => EnvironmentStatus.InvalidName,
        EnvironmentOutcome.Listed => EnvironmentStatus.Listed,
        EnvironmentOutcome.InvalidCursor => EnvironmentStatus.InvalidCursor,
        EnvironmentOutcome.InvalidPageSize => EnvironmentStatus.InvalidPageSize,
        _ => throw new InvalidOperationException("Unknown Environment outcome.")
    };
}
