using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlApplicationLoginTests
{
    private sealed class BlockedProducerSecretStore : ISecretStore
    {
        public int Resolutions;
        public ValueTask<SecretMaterial?> ResolveAsync(SecretDefinition definition, SecretRef reference, CancellationToken token)
        {
            Resolutions++;
            throw new InvalidOperationException("Blocked producer must not resolve material.");
        }
        public ValueTask<SecretMaterial?> ResolveBoundedAsync(SecretDefinition definition, SecretRef reference, int maximumBytes, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<EnvironmentRecordId> CreateEnvironmentAsync(EnvironmentId id, CancellationToken token) => throw new NotSupportedException();
        public ValueTask CreateAsync(SecretDefinition definition, SecretRef reference, byte[] material, CancellationToken token) => throw new NotSupportedException();
        public ValueTask AddVersionAsync(EnvironmentRecordId environment, SecretRef reference, byte[] material, CancellationToken token) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Blocked_generated_producer_refuses_before_resolving_material()
    {
        var scenario = await Prepare(full: true);
        await using var services = OwnerServices(scenario);
        var producer = new DurableOperationKey(scenario.Commit.DeploymentId, scenario.Commit.Generation, OperationId.Create("app-login-material"));
        var input = (await services.GetRequiredService<IGeneratedCredentialOperations>().FindInputAsync(producer, default))!;
        var slot = await services.GetRequiredService<IGeneratedCredentialSlots>().GetOrCreateAsync(new(input.Owner, input.SlotId), input.GenerationProfile, default);
        Assert.NotNull(slot.Reference);
        var store = new BlockedProducerSecretStore();
        var outputs = new PostgreSqlGeneratedCredentialOperations(services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), store);
        Assert.False(await outputs.CompleteAsync(producer, Guid.CreateVersion7(), new(slot.Reference), DateTimeOffset.UtcNow, default));
        Assert.Equal(0, store.Resolutions);
        await using var verify = Context(scenario.Connection);
        Assert.Empty(await verify.DurableOperationAttempts.ToArrayAsync());
        Assert.Empty(await verify.OperationOutputs.ToArrayAsync());
        Assert.Empty(await verify.GenerationStageCheckpoints.ToArrayAsync());
    }
}
