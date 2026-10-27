using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.Persistence.PostgreSql.Tests;

public sealed class DurablePlannerGuardTests
{
    [Fact]
    public void Operation_envelope_has_a_composite_key_and_no_provider_payload_columns()
    {
        using var context = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>()
            .UseNpgsql().Options);
        var entity = context.Model.FindEntityType(typeof(DurableCreateOperationRow))!;
        Assert.Equal(["DeploymentId", "Generation", "ResourceId", "Action"],
            entity.FindPrimaryKey()!.Properties.Select(property => property.Name).ToArray());
        Assert.Equal(["Action", "CreatedAt", "DeploymentId", "Generation", "ResourceId", "State"],
            entity.GetProperties().Select(property => property.Name).OrderBy(name => name).ToArray());
    }
    [Fact]
    public void Registration_supplies_a_clock_without_replacing_an_explicit_clock()
    {
        using var defaultServices = new ServiceCollection().AddPostgreSqlDurableDeploymentPlanner()
            .BuildServiceProvider();
        Assert.Same(TimeProvider.System, defaultServices.GetRequiredService<TimeProvider>());

        var supplied = new FixedClock();
        using var customServices = new ServiceCollection().AddSingleton<TimeProvider>(supplied)
            .AddPostgreSqlDurableDeploymentPlanner().BuildServiceProvider();
        Assert.Same(supplied, customServices.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Actor_validation_rejects_empty_oversized_and_control_character_values()
    {
        PostgreSqlDurableDeploymentPlanner.ValidateActor(new string('a', 256));
        Assert.Throws<ArgumentNullException>(() => PostgreSqlDurableDeploymentPlanner.ValidateActor(null!));
        Assert.Throws<ArgumentException>(() => PostgreSqlDurableDeploymentPlanner.ValidateActor(""));
        Assert.Throws<ArgumentException>(() => PostgreSqlDurableDeploymentPlanner.ValidateActor(new string('a', 257)));
        Assert.Throws<ArgumentException>(() => PostgreSqlDurableDeploymentPlanner.ValidateActor("actor\nsecret"));
    }

    [Fact]
    public void Fingerprint_is_versioned_deterministic_and_changes_with_exact_input()
    {
        var application = ApplicationRecordId.New();
        var inputs = new DurablePlanningInputs(EnvironmentRecordId.New(), EnvironmentId.Create("pilot"),
            ManifestRevisionRef.Create(application, ManifestRevisionNumber.Create(1)),
            ProviderAlias.Create("postgresql.primary"), ProviderInstanceId.New(),
            ProviderConfigurationRevisionNumber.Create(1), "haby.postgresql.database/v1alpha1");
        var fingerprint = PostgreSqlDurableDeploymentPlanner.Fingerprint(inputs);

        Assert.StartsWith("v1:", fingerprint);
        Assert.Equal(67, fingerprint.Length);
        Assert.Equal(fingerprint, PostgreSqlDurableDeploymentPlanner.Fingerprint(inputs));
        Assert.NotEqual(fingerprint, PostgreSqlDurableDeploymentPlanner.Fingerprint(inputs with
        { ConfigurationRevision = ProviderConfigurationRevisionNumber.Create(2) }));
        Assert.NotEqual(fingerprint, PostgreSqlDurableDeploymentPlanner.Fingerprint(inputs with
        { ImplementationId = "other/v1" }));
        Assert.NotEqual(fingerprint, PostgreSqlDurableDeploymentPlanner.Fingerprint(inputs with
        { Revision = ManifestRevisionRef.Create(application, ManifestRevisionNumber.Create(2)) }));
    }

    [Fact]
    public async Task Read_and_close_operations_reject_missing_typed_ids_before_storage()
    {
        // These dependencies are deliberately unavailable: argument guards must run before I/O.
        var planner = new PostgreSqlDurableDeploymentPlanner(null!, null!, null!, [], null!, TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await planner.FindChangeSetAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await planner.FindPlanAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await planner.ClosePlanAsync(null!, "actor", default));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await planner.CheckPlanAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await planner.ClosePlanAsync(DeploymentPlanId.New(), "invalid\nactor", default));
    }

    private sealed class FixedClock : TimeProvider { }
}
