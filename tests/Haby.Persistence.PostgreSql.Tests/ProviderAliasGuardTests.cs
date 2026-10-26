using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Xunit;

namespace Haby.Persistence.PostgreSql.Tests;

public sealed class ProviderAliasGuardTests
{
    [Fact]
    public async Task Every_attempt_preflights_exact_target_before_storage_and_returns_only_typed_outcome()
    {
        var preflight = new RejectingPreflight();
        var store = new PostgreSqlProviderAliasStore(null!, preflight, TimeProvider.System);
        var environment = EnvironmentRecordId.New();
        var alias = ProviderAlias.Create("postgresql.primary");
        var first = new ProviderAliasBinding(ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(1));
        var second = new ProviderAliasBinding(ProviderInstanceId.New(), ProviderConfigurationRevisionNumber.Create(2));

        var initial = await store.ChangeAsync(environment, alias, null, first, "test-runner", default);
        var switchResult = await store.ChangeAsync(environment, alias, first, second, "test-runner", default);

        Assert.Equal(ProviderAliasChangeOutcome.PreflightFailed, initial.Outcome);
        Assert.Equal(ProviderAliasChangeOutcome.PreflightFailed, switchResult.Outcome);
        Assert.Equal(new ProviderPreflightResult(ProviderPreflightOutcome.AuthenticationFailed), initial.Preflight);
        Assert.Equal(new ProviderPreflightResult(ProviderPreflightOutcome.AuthenticationFailed), switchResult.Preflight);
        Assert.Equal([first, second], preflight.Targets);
        Assert.All(preflight.Environments, value => Assert.Equal(environment, value));
        Assert.DoesNotContain("Password", string.Join(',', typeof(ProviderPreflightResult).GetProperties()
            .Select(property => property.Name)));
    }

    private sealed class RejectingPreflight : IProviderRevisionPreflight
    {
        public List<ProviderAliasBinding> Targets { get; } = [];
        public List<EnvironmentRecordId> Environments { get; } = [];
        public ValueTask<ProviderPreflightResult> CheckAsync(EnvironmentRecordId environment,
            ProviderAliasBinding target, CancellationToken cancellationToken)
        {
            Environments.Add(environment);
            Targets.Add(target);
            return ValueTask.FromResult(new ProviderPreflightResult(ProviderPreflightOutcome.AuthenticationFailed));
        }
    }
}
