using Grpc.Core;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.PostgreSql;
using Haby.PostgreSql.IntegrationTests.Wire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

namespace Haby.PostgreSql.IntegrationTests;

// Entire composition root is test-only. The administrative connection never crosses the RPC boundary.
internal sealed class CheckpointTestState
{
    private static readonly ManifestContractCatalog Catalog = new([PostgreSqlDatabaseContract.Definition], [], [], []);
    private readonly InMemoryManifestImportStore _revisions = new();
    private readonly InMemoryPostgreSqlConfigurationCatalog _configurations = new();
    private readonly Dictionary<string, ManifestRevisionRef> _revisionHandles = [];
    private readonly Dictionary<string, DeploymentChangeSetId> _changeHandles = [];
    private readonly Dictionary<string, DeploymentPlanId> _planHandles = [];
    private readonly Dictionary<string, ApplicationDeploymentGeneration> _generationHandles = [];
    private readonly PostgreSqlDatabaseProvisioner _provider;
    private readonly InMemoryDesiredStateStore _desired;
    private readonly PostgreSqlCheckpointExecutor _executor;
    private readonly EnvironmentRecordId _environment;
    private readonly ImportApplicationManifest _import;

    public CheckpointTestState(string administrativeConnection)
    {
        _configurations.Add("provider", 1, administrativeConnection);
        _provider = new PostgreSqlDatabaseProvisioner(_configurations);
        _desired = new InMemoryDesiredStateStore(_revisions, TimeProvider.System, _provider);
        _environment = _desired.AddEnvironment(EnvironmentId.Create("test-env"), "Test").RecordId;
        _desired.SetProviderAlias(_environment, ProviderAlias.Create("main"),
            new ProviderResolution("provider", 1, PostgreSqlDatabaseProvisioner.ImplementationId));
        _executor = new PostgreSqlCheckpointExecutor(_desired, _revisions, _provider);
        _import = new ImportApplicationManifest(_revisions, Catalog, TimeProvider.System);
    }

    public async Task<ImportReply> Import(byte[] originalEnvelope, CancellationToken cancellationToken)
    {
        var result = await _import.ExecuteAsync(originalEnvelope, "checkpoint-client", cancellationToken);
        if (result is not ImportApplicationManifestResult.Succeeded success)
            return new ImportReply
            {
                Outcome = result switch
                {
                    ImportApplicationManifestResult.InvalidRequest => "InvalidRequest",
                    ImportApplicationManifestResult.InvalidManifest => "InvalidManifest",
                    ImportApplicationManifestResult.ApplicationVersionContentConflict => "ContentConflict",
                    _ => "Rejected"
                }
            };
        var reference = success.ManifestRevisionRef;
        var handle = $"{reference.ApplicationRecordId}:{reference.RevisionNumber.Value}";
        _revisionHandles[handle] = reference;
        return new ImportReply
        {
            Outcome = success.Disposition.ToString(),
            Revision = handle,
            Application = reference.ApplicationRecordId.ToString(),
            RevisionNumber = reference.RevisionNumber.Value
        };
    }

    public ChangeSetReply CreateChangeSet(ChangeSetRequest request, CancellationToken cancellationToken)
    {
        if (!_revisionHandles.TryGetValue(request.Revision, out var revision) ||
            request.Application != revision.ApplicationRecordId.ToString() || request.ExpectedGeneration < 0)
            return new ChangeSetReply { Outcome = "InvalidReference" };
        DeploymentPrecondition precondition = request.ExpectedGeneration == 0
            ? new DeploymentPrecondition.ExpectedAbsence()
            : new DeploymentPrecondition.ExpectedGeneration(DeploymentGeneration.Create(request.ExpectedGeneration));
        var result = _desired.CreateChangeSet(_environment, revision.ApplicationRecordId, revision,
            precondition, "checkpoint-client", cancellationToken);
        if (result is not CreateChangeSetResult.Created created)
            return new ChangeSetReply { Outcome = result.GetType().Name };
        var handle = created.ChangeSet.Id.ToString();
        _changeHandles[handle] = created.ChangeSet.Id;
        return new ChangeSetReply { Outcome = "Created", ChangeSet = handle };
    }

    public PlanReply Plan(PlanRequest request, CancellationToken cancellationToken)
    {
        if (!_changeHandles.TryGetValue(request.ChangeSet, out var changeSet))
            return new PlanReply { Outcome = "InvalidReference" };
        var result = _desired.Plan(changeSet, "checkpoint-client", cancellationToken);
        if (result is not PlanChangeSetResult.Planned planned)
            return new PlanReply { Outcome = result.GetType().Name };
        var handle = planned.Plan.Id.ToString();
        _planHandles[handle] = planned.Plan.Id;
        return new PlanReply { Outcome = "Planned", Plan = handle };
    }

    public CommitReply Commit(CommitRequest request, CancellationToken cancellationToken)
    {
        if (!_changeHandles.TryGetValue(request.ChangeSet, out var changeSet) ||
            !_planHandles.TryGetValue(request.Plan, out var plan))
            return new CommitReply { Outcome = "InvalidReference" };
        var result = _desired.Commit(changeSet, plan, "checkpoint-client", cancellationToken);
        if (result is not CommitDesiredStateResult.Committed committed)
            return new CommitReply { Outcome = result.GetType().Name };
        var committedPlan = _desired.Plans.Single(item => item.Id == committed.Commit.PlanId);
        var snapshot = _desired.Generations.Single(item =>
            item.Generation == committed.Commit.Generation &&
            item.Inputs.EnvironmentRecordId == committedPlan.EnvironmentRecordId &&
            item.ManifestRevisionRef.ApplicationRecordId == committedPlan.Member.ApplicationRecordId);
        var handle = $"{snapshot.DeploymentId}:{snapshot.Generation.Value}";
        _generationHandles[handle] = snapshot;
        return new CommitReply
        {
            Outcome = "Committed",
            Generation = handle,
            DesiredGeneration = committed.Commit.Generation.Value,
            NoOp = committed.Commit.NoOp
        };
    }

    public async Task<ExecuteReply> Execute(ExecuteRequest request, CancellationToken cancellationToken)
    {
        if (!_generationHandles.TryGetValue(request.Generation, out var snapshot))
            return new ExecuteReply { State = "Rejected", Failure = "InvalidReference" };
        var result = await _executor.ExecuteAsync(snapshot, cancellationToken);
        return new ExecuteReply { State = result.State.ToString(), Failure = result.Failure.ToString() };
    }

    public StateReply ReadState(string application)
    {
        var deployment = _desired.Deployments.SingleOrDefault(item =>
            item.ApplicationRecordId.ToString() == application);
        return new StateReply
        {
            Revisions = _revisions.Revisions.Count,
            Generations = _desired.Generations.Count,
            DesiredGeneration = deployment?.DesiredGeneration.Value ?? 0,
            AppliedGeneration = deployment?.AppliedGeneration?.Value ?? 0
        };
    }
}

internal sealed class CheckpointService(CheckpointTestState state) : Checkpoint.CheckpointBase
{
    public override async Task<ImportReply> Import(ImportRequest request, ServerCallContext context) =>
        await state.Import(request.OriginalEnvelope.ToByteArray(), context.CancellationToken);
    public override Task<ChangeSetReply> CreateChangeSet(ChangeSetRequest request, ServerCallContext context) =>
        Task.FromResult(state.CreateChangeSet(request, context.CancellationToken));
    public override Task<PlanReply> Plan(PlanRequest request, ServerCallContext context) =>
        Task.FromResult(state.Plan(request, context.CancellationToken));
    public override Task<CommitReply> CommitDesiredState(CommitRequest request, ServerCallContext context) =>
        Task.FromResult(state.Commit(request, context.CancellationToken));
    public override Task<ExecuteReply> ExecuteCommittedGeneration(ExecuteRequest request, ServerCallContext context) =>
        state.Execute(request, context.CancellationToken);
    public override Task<StateReply> ReadState(StateRequest request, ServerCallContext context) =>
        Task.FromResult(state.ReadState(request.Application));
}

internal sealed class CheckpointTestHost(WebApplication app) : IAsyncDisposable
{
    public string Address => app.Urls.Single();

    public static async Task<CheckpointTestHost> StartAsync(string administrativeConnection)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(new CheckpointTestState(administrativeConnection));
        var app = builder.Build();
        app.MapGrpcService<CheckpointService>();
        await app.StartAsync();
        return new CheckpointTestHost(app);
    }

    public ValueTask DisposeAsync() => app.DisposeAsync();
}
