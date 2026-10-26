using Haby.Domain;

namespace Haby.Application;

/// <summary>One exact, Environment-local Provider configuration selection.</summary>
public sealed record ProviderAliasBinding(ProviderInstanceId InstanceId,
    ProviderConfigurationRevisionNumber RevisionNumber);

/// <summary>Ordinary reads contain identities only, never configuration or secret material.</summary>
public sealed record PersistedProviderAliasResolution(ProviderAlias Alias, ProviderAliasBinding Binding,
    string ImplementationId);

public enum ProviderPreflightOutcome
{
    Ready,
    RevisionUnavailable,
    SecretUnavailable,
    AuthenticationFailed,
    ServerUnavailable,
    InsufficientPrivilege,
    ConfigurationUnavailable,
    DeadlineExceeded
}

/// <summary>Bounded, value-free point-in-time evidence. No driver detail crosses this boundary.</summary>
public readonly record struct ProviderPreflightResult(ProviderPreflightOutcome Outcome)
{
    public bool IsReady => Outcome == ProviderPreflightOutcome.Ready;
}

public interface IProviderRevisionPreflight
{
    ValueTask<ProviderPreflightResult> CheckAsync(EnvironmentRecordId environment,
        ProviderAliasBinding target, CancellationToken cancellationToken);
}

public enum ProviderAliasChangeOutcome
{
    Changed,
    Unchanged,
    PreflightFailed,
    StaleExpectation,
    TargetUnavailable,
    InstanceAlreadyBound,
    StoreUnavailable
}

public sealed record ProviderAliasChangeResult(ProviderAliasChangeOutcome Outcome,
    ProviderPreflightResult? Preflight = null);

/// <summary>Trusted internal caller supplies the technical actor; no application authentication is implied.</summary>
public interface IProviderAliasResolver
{
    ValueTask<PersistedProviderAliasResolution?> ResolveAsync(EnvironmentRecordId environment, ProviderAlias alias,
        CancellationToken cancellationToken);
}

public interface IProviderAliasStore : IProviderAliasResolver
{
    ValueTask<ProviderAliasChangeResult> ChangeAsync(EnvironmentRecordId environment, ProviderAlias alias,
        ProviderAliasBinding? expected, ProviderAliasBinding proposed, string technicalActor,
        CancellationToken cancellationToken);
}
