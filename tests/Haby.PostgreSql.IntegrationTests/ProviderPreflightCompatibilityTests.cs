using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Haby.Application;
using Haby.Domain;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

[Collection("Provider alias isolated")]
public sealed class ProviderPreflightCompatibilityTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Persisted_historical_format_uses_explicit_reader_with_current_exact_handler_and_alias_CAS(bool sameClrType)
    {
        var connection = await Database();
        IProviderConfigurationCodec legacy = sameClrType ? new QueueCodec<CurrentQueueSettings>(CompatibilityJson.Default.CurrentQueueSettings, 1, "queue.settings/v1") : Legacy();
        string originalPayload;
        await using (var oldHost = Host(connection, legacy, [legacy]))
        {
            await oldHost.Services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            await oldHost.Services.GetRequiredService<IEnvironmentCatalog>().CreateOrGetAsync(EnvironmentId.Create("pilot"), default);
            Assert.Equal(ProviderOutcome.Created, (await Apply(oldHost.Services, 1)).Outcome);
            originalPayload = (await Rows(connection)).Single().Payload;
        }
        legacy = sameClrType ? new QueueCodec<CurrentQueueSettings>(CompatibilityJson.Default.CurrentQueueSettings, 1, "queue.settings/v1", true) : Legacy(true);
        var current = Current();
        await using (var upgraded = Host(connection, current, [legacy, current]))
        {
            var access = upgraded.Services.GetRequiredService<ProviderAccessAdministration>();
            var check = await access.CheckAsync("pilot", new("queue", null, 1), default);
            Assert.Equal(ProviderPreflightOutcome.Ready, check.Preflight!.Value.Outcome);
            Assert.Equal(ProviderOutcome.Created, (await Apply(upgraded.Services, 2)).Outcome);
            Assert.Equal(ProviderOutcome.ExistingEquivalent, (await Apply(upgraded.Services, 1)).Outcome);
            Assert.Equal(ProviderPreflightOutcome.Ready, (await access.CheckAsync("pilot", new("queue", null, 2), default)).Preflight!.Value.Outcome);
            var environment = (await upgraded.Services.GetRequiredService<IEnvironmentQuery>().FindAsync(EnvironmentId.Create("pilot"), default))!;
            var provider = (await upgraded.Services.GetRequiredService<ProviderAdministration>().GetAsync("pilot", "queue", 1, default)).Provider!;
            var store = upgraded.Services.GetRequiredService<IProviderConfigurationStore>();
            var oldQueue = sameClrType
                ? (await store.FindRevisionAsync<CurrentQueueSettings>(environment, provider.InstanceId, ProviderConfigurationRevisionNumber.Create(1), default))!.Configuration.Queue
                : (await store.FindRevisionAsync<LegacyQueueSettings>(environment, provider.InstanceId, ProviderConfigurationRevisionNumber.Create(1), default))!.Configuration.Queue;
            Assert.Equal("cluster", oldQueue);
            Assert.Equal("cluster", (await store.FindRevisionAsync<CurrentQueueSettings>(environment, provider.InstanceId, ProviderConfigurationRevisionNumber.Create(2), default))!.Configuration.Queue);
            if (sameClrType)
                await store.AddRevisionAsync(environment, provider.InstanceId, ProviderConfigurationRevisionNumber.Create(3), new CurrentQueueSettings("cluster"), default);
            Assert.Equal(ProviderAccessOutcome.Changed, (await access.SetAliasAsync("pilot", "queue.primary", new("queue", null, 1), true, null, "actor", default)).Outcome);
            Assert.Equal(ProviderAccessOutcome.Changed, (await access.SetAliasAsync("pilot", "queue.primary", new("queue", null, 2), false, new("queue", null, 1), "actor", default)).Outcome);
            Assert.Equal(ProviderAccessOutcome.Changed, (await access.SetAliasAsync("pilot", "queue.primary", new("queue", null, 1), false, new("queue", null, 2), "actor", default)).Outcome);
        }
        await using (var restarted = Host(connection, current, [legacy, current]))
        {
            Assert.Equal(ProviderPreflightOutcome.Ready, (await restarted.Services.GetRequiredService<ProviderAccessAdministration>().CheckAsync("pilot", new("queue", null, 1), default)).Preflight!.Value.Outcome);
            Assert.Equal(1, (await restarted.Services.GetRequiredService<ProviderAccessAdministration>().GetAliasAsync("pilot", "queue.primary", default)).Alias!.Binding.RevisionNumber.Value);
        }
        foreach (var omitReader in new[] { true, false })
        {
            await using var missingReader = Host(connection, current, omitReader ? [current] : [legacy, current], [current]);
            var admin = missingReader.Services.GetRequiredService<ProviderAccessAdministration>();
            Assert.Equal(ProviderOutcome.Found, (await missingReader.Services.GetRequiredService<ProviderAdministration>().GetAsync("pilot", "queue", 1, default)).Outcome);
            Assert.Equal(ProviderPreflightOutcome.ConfigurationUnavailable, (await admin.CheckAsync("pilot", new("queue", null, 1), default)).Preflight!.Value.Outcome);
            Assert.Equal(ProviderPreflightOutcome.Ready, (await admin.CheckAsync("pilot", new("queue", null, 2), default)).Preflight!.Value.Outcome);
            Assert.Equal(ProviderAccessOutcome.PreflightFailed, (await admin.SetAliasAsync("pilot", "queue.primary", new("queue", null, 1), false, new("queue", null, 1), "actor", default)).Outcome);
        }
        var rows = await Rows(connection);
        Assert.Equal(sameClrType ? new[] { 1, 2, 2 } : [1, 2], rows.Select(x => x.Format));
        Assert.Equal(originalPayload, rows[0].Payload);
        await using var assertion = Context(connection);
        Assert.Equal(3, await assertion.ProviderAliasAudits.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task One_handler_can_explicitly_support_distinct_contracts_without_identity_overlap()
    {
        var connection = await Database();
        var current = Current();
        var other = new QueueCodec<OtherQueueSettings>(CompatibilityJson.Default.OtherQueueSettings, 1, "queue.other/v1");
        await using var host = Host(connection, current, [current, other]);
        await host.Services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        await host.Services.GetRequiredService<IEnvironmentCatalog>().CreateOrGetAsync(EnvironmentId.Create("pilot"), default);
        Assert.Equal(ProviderOutcome.Created, (await Apply(host.Services, 1)).Outcome);
        var otherDeclaration = Declaration.Replace("REVISION", "2", StringComparison.Ordinal).Replace("queue.settings/v1", "queue.other/v1", StringComparison.Ordinal);
        Assert.Equal(ProviderOutcome.Created, (await host.Services.GetRequiredService<ProviderAdministration>().ApplyAsync(Encoding.UTF8.GetBytes(otherDeclaration), null, false, null, "actor", default)).Outcome);
        var access = host.Services.GetRequiredService<ProviderAccessAdministration>();
        foreach (var revision in new[] { 1, 2 })
            Assert.Equal(ProviderPreflightOutcome.Ready, (await access.CheckAsync("pilot", new("queue", null, revision), default)).Preflight!.Value.Outcome);
        Assert.Equal(ProviderAccessOutcome.Changed, (await access.SetAliasAsync("pilot", "queue.primary", new("queue", null, 2), true, null, "actor", default)).Outcome);
        var registry = host.Services.GetRequiredService<ProviderPreflightRegistry>();
        Assert.Same(registry.Find(new("queue/v1", "queue.settings/v1", "queue.impl/v1")),
            registry.Find(new("queue/v1", "queue.other/v1", "queue.impl/v1")));
    }

    [Theory]
    [InlineData("private-reader")]
    [InlineData("duplicate-format")]
    [InlineData("multiple-writers")]
    [InlineData("reader-as-writer")]
    public void Invalid_reader_or_writer_registrations_still_fail_startup(string scenario)
    {
        var writer = Current();
        var legacy = Legacy(true);
        const string unused = "Host=127.0.0.1;Database=unused;Username=unused";
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var host = scenario switch
            {
                "private-reader" => Host(unused, writer, [writer, legacy], [writer, Legacy(true)]),
                "duplicate-format" => Host(unused, writer, [writer, legacy, Legacy(true)]),
                "multiple-writers" => Host(unused, writer, [writer, new QueueCodec<CurrentQueueSettings>(CompatibilityJson.Default.CurrentQueueSettings, 1, "queue.settings/v1")]),
                _ => Host(unused, legacy, [legacy])
            };
        });
    }

    private static QueueCodec<LegacyQueueSettings> Legacy(bool compatibility = false) => new(CompatibilityJson.Default.LegacyQueueSettings, 1, "queue.settings/v1", compatibility);
    private static QueueCodec<CurrentQueueSettings> Current() => new(CompatibilityJson.Default.CurrentQueueSettings, 2, "queue.settings/v1");
    private static Microsoft.AspNetCore.Builder.WebApplication Host(string connection, IProviderConfigurationCodec writer, IProviderConfigurationCodec[] readers, IProviderConfigurationCodec[]? acceptedReaders = null) =>
        OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0"], services =>
        {
            foreach (var reader in readers) services.AddSingleton<IProviderConfigurationCodec>(reader);
            services.AddSingleton<IProviderDeclarationAdapter>(new QueueDeclarationAdapter(writer));
            foreach (var other in readers.Where(reader => !reader.IsCompatibilityReader && reader.ShapeId != writer.ShapeId))
                services.AddSingleton<IProviderDeclarationAdapter>(new QueueDeclarationAdapter(other));
            services.AddSingleton<IProviderPreflightHandler>(new QueuePreflight(acceptedReaders ?? readers));
        });
    private static ValueTask<ProviderResult> Apply(IServiceProvider services, int revision) =>
        services.GetRequiredService<ProviderAdministration>().ApplyAsync(Encoding.UTF8.GetBytes(Declaration.Replace("REVISION", revision.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)), null, false, null, "actor", default);
    private const string Declaration = """
        {"apiVersion":"haby.dev/v1alpha1","kind":"Provider","metadata":{"name":"queue","environment":"pilot"},
        "spec":{"revision":REVISION,"providerKind":"queue/v1","configurationContract":"queue.settings/v1","implementationId":"queue.impl/v1",
        "properties":{"queue":"cluster"},"secretRefs":{}}}
        """;
    private async Task<string> Database()
    {
        var name = "haby_compat_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection).ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name, Pooling = true, MaxPoolSize = 16 }.ConnectionString;
    }
    private static ManifestCatalogDbContext Context(string connection) => new(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
    private static async Task<(int Format, string Payload)[]> Rows(string connection)
    {
        await using var context = Context(connection);
        var rows = await context.ProviderConfigurationRevisions.AsNoTracking().OrderBy(x => x.RevisionNumber).Select(x => new { Format = x.PayloadFormatVersion, Payload = x.PayloadJson }).ToArrayAsync();
        return rows.Select(x => (x.Format, x.Payload)).ToArray();
    }
}

internal interface IQueueSettings { string Queue { get; } }
internal sealed record LegacyQueueSettings(string Queue) : IQueueSettings;
internal sealed record CurrentQueueSettings(string Queue, int Capacity = 10) : IQueueSettings;
internal sealed record OtherQueueSettings(string Queue) : IQueueSettings;
internal sealed class QueueCodec<T>(JsonTypeInfo<T> metadata, int format, string contract, bool compatibility = false)
    : ProviderConfigurationCodec<T> where T : class, IQueueSettings
{
    public override string ProviderKindId => "queue/v1";
    public override string ShapeId => contract;
    public override int PayloadFormatVersion => format;
    public override bool IsCompatibilityReader => compatibility;
    public override PreparedProviderConfiguration Encode(T value) => new(ProviderKindId, new(1, value.Queue), "queue.impl/v1", ShapeId,
        PayloadFormatVersion, JsonSerializer.Serialize(value, metadata), []);
    public override T DecodeTyped(string payload) => JsonSerializer.Deserialize(payload, metadata) ?? throw new InvalidDataException();
}
internal sealed class QueueDeclarationAdapter(IProviderConfigurationCodec codec) : IProviderDeclarationAdapter
{
    public ProviderAdapterKey Key => new(codec.ProviderKindId, codec.ShapeId, "queue.impl/v1");
    public IProviderConfigurationCodec Codec => codec;
    public IReadOnlyList<string> RequiredSecretSlots => [];
    public object ParseProperties(JsonElement properties) => codec.Decode(properties.GetRawText());
    public object Bind(object properties, IReadOnlyDictionary<string, SecretRef> secrets) => properties;
    public bool CanReplace(ProviderAdapterKey previous) => previous.ProviderKind == "queue/v1" && previous.ImplementationId == "queue.impl/v1" &&
        previous.ConfigurationContract is "queue.settings/v1" or "queue.other/v1";
    public bool Equivalent(object previous, object candidate) => SameEndpoint(previous, candidate) && Capacity(previous) == Capacity(candidate);
    private static int Capacity(object value) => value is CurrentQueueSettings current ? current.Capacity : 10;
    public bool SameEndpoint(object previous, object candidate) => ((IQueueSettings)previous).Queue == ((IQueueSettings)candidate).Queue;
}
internal sealed class QueuePreflight(IProviderConfigurationCodec[] readers) : IProviderPreflightHandler
{
    public IReadOnlyList<IProviderConfigurationCodec> Readers => readers;
    public IReadOnlyList<ProviderAdapterKey> Keys => readers.Select(x => new ProviderAdapterKey(x.ProviderKindId, x.ShapeId, "queue.impl/v1")).Distinct().ToArray();
    public ValueTask<ProviderPreflightResult> CheckAsync(EnvironmentRecordId environment, object configuration, CancellationToken token)
    {
        Assert.IsAssignableFrom<IQueueSettings>(configuration);
        Assert.Equal("cluster", ((IQueueSettings)configuration).Queue);
        return ValueTask.FromResult(new ProviderPreflightResult(ProviderPreflightOutcome.Ready));
    }
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(LegacyQueueSettings))]
[JsonSerializable(typeof(CurrentQueueSettings))]
[JsonSerializable(typeof(OtherQueueSettings))]
internal partial class CompatibilityJson : JsonSerializerContext;
