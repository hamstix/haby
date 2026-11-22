using System.Text.Json;
using Haby.Domain;
using Haby.Manifest;
using Haby.PostgreSql;
using Xunit;

namespace Haby.Application.Tests;

public sealed class PostgreSqlImportReplayTests
{
    [Fact]
    public async Task Unsupported_workloads_refuse_preparation_before_provider_access_or_private_ownership()
    {
        var candidate = Assert.IsType<ManifestCandidateResult.Valid>(ManifestCandidatePipeline.Prepare("""
            {"applicationVersion":"1","manifest":{"apiVersion":"haby.dev/v1alpha1","kind":"Application","metadata":{"id":"sample"},
              "spec":{"resources":{"db":{"type":"postgresql.database/v1alpha1","providerRef":"main","properties":{"name":"derived_db"}},
              "login":{"type":"postgresql.application-login/v1alpha1","providerRef":"main","properties":{"databaseRef":"db","schema":"public","privilegePolicy":"database-owner/v1"}}},
              "components":{"worker":{"workloads":{"run":{"type":"sample.workload/v1","providerRef":"main","properties":{}}}}}}}}
            """u8.ToArray(), new([PostgreSqlDatabaseContract.Definition, PostgreSqlApplicationLoginContract.Definition],
                [new(WorkloadTypeId.Create("sample.workload/v1"), "{}")], [], [])));
        var provider = new NoProvider();
        var result = await new PostgreSqlImportPreparer(provider, null!).PrepareAsync(new(Guid.NewGuid(), EnvironmentRecordId.New(),
            ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1)),
            PostgreSqlImportPreparer.Encode(new("legacy_db", "legacy_login", "public"))), candidate.Candidate.Declaration.Spec, default);
        Assert.Equal(ResourceImportPreparationStatus.ImportNotSupported, result.Status);
        Assert.Equal(0, provider.Reads);
        Assert.Null(result.Binding);
    }

    private sealed class NoProvider : IProviderAliasResolver
    {
        internal int Reads;
        public ValueTask<PersistedProviderAliasResolution?> ResolveAsync(EnvironmentRecordId environment, ProviderAlias alias, CancellationToken cancellationToken)
        { Reads++; return ValueTask.FromResult<PersistedProviderAliasResolution?>(null); }
    }

    [Theory]
    [InlineData("same", true)]
    [InlineData("changed-name", false)]
    [InlineData("client-oid", false)]
    [InlineData("duplicate-name", false)]
    [InlineData("prepared-input", false)]
    [InlineData("mixed-pins", false)]
    public void Replay_compares_closed_names_to_one_trusted_prepared_pair_without_provider_io(string change, bool expected)
    {
        var environment = EnvironmentRecordId.New();
        var revision = ManifestRevisionRef.Create(ApplicationRecordId.New(), ManifestRevisionNumber.Create(1));
        var names = new PostgreSqlImportNames("legacy_db", "legacy_login", "public");
        var pair = new PostgreSqlExistingPairInput("db", "login", names.DatabaseName, 40, names.LoginName, 41, names.Schema, 42, 42);
        var prepared = new PostgreSqlPreparedImportInput(pair, environment.ToString(), ProviderInstanceId.New().ToString(), 1,
            PostgreSqlDatabaseCheckpointCapability.ImplementationId);
        var payload = Encode(prepared);
        var binding = new OperatorImportRequestBinding(Guid.NewGuid(), environment, revision, ManifestLocalId.Create("login"),
            new([new(ManifestLocalId.Create("db"), payload), new(ManifestLocalId.Create("login"), change == "mixed-pins" ? Encode(prepared with { ProviderRevision = 2 }) : payload)]));
        var input = PostgreSqlImportPreparer.Encode(names);
        input = change switch
        {
            "changed-name" => PostgreSqlImportPreparer.Encode(names with { DatabaseName = "different_db" }),
            "client-oid" => input with { Json = input.Json[..^1] + ",\"databaseOid\":40}" },
            "duplicate-name" => input with { Json = input.Json[..^1] + ",\"databaseName\":\"legacy_db\"}" },
            "prepared-input" => payload,
            _ => input
        };
        // Pure replay does not require either I/O dependency to be available.
        var adapter = new PostgreSqlImportPreparer(null!, null!);
        Assert.Equal(expected, adapter.MatchesPreparedRequest(new(binding.RequestId, environment, revision, input), binding));
    }

    private static DurableActionPayload Encode(PostgreSqlPreparedImportInput input) => new(PostgreSqlImportPreparer.PreparedFormat,
        JsonSerializer.Serialize(input, PostgreSqlImportPreparationJsonContext.Default.PostgreSqlPreparedImportInput));
}
