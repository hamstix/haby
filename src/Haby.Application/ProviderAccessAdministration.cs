using Haby.Domain;

namespace Haby.Application;

public sealed record ProviderSelector(string? Name, string? InstanceId, long Revision);
public interface IProviderIdentityQuery
{
    ValueTask<ProviderAliasBinding?> ResolveAsync(EnvironmentRecordId environment, ProviderSelector selector, CancellationToken cancellationToken);
}
public enum ProviderAccessOutcome
{
    Ready, Found, NotFound, Changed, Unchanged, InvalidRequest, EnvironmentNotFound,
    TargetUnavailable, PreflightFailed, StaleExpectation, InstanceAlreadyBound, StorageUnavailable
}
public sealed record ProviderAliasSelection(ProviderAlias Alias, ProviderAliasBinding Binding);
public sealed record ProviderAccessResult(ProviderAccessOutcome Outcome, ProviderAliasSelection? Alias = null,
    ProviderPreflightResult? Preflight = null);

/// <summary>Operator name resolution delegates all alias invariants to the existing checked store.</summary>
public sealed class ProviderAccessAdministration(IEnvironmentQuery environments, IProviderIdentityQuery identities,
    IProviderRevisionPreflight preflight, IProviderAliasStore aliases)
{
    public async ValueTask<ProviderAccessResult> CheckAsync(string environment, ProviderSelector target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ProviderDeclarationInput.ValidName(environment) || !Valid(target)) return new(ProviderAccessOutcome.InvalidRequest);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var record = await environments.FindAsync(EnvironmentId.Create(environment), deadline.Token);
            if (record is null) return new(ProviderAccessOutcome.EnvironmentNotFound);
            var binding = await identities.ResolveAsync(record, target, deadline.Token);
            if (binding is null) return new(ProviderAccessOutcome.TargetUnavailable, Preflight: new(ProviderPreflightOutcome.RevisionUnavailable));
            var check = await preflight.CheckAsync(record, binding, deadline.Token);
            return new(check.IsReady ? ProviderAccessOutcome.Ready : ProviderAccessOutcome.PreflightFailed, Preflight: check);
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (Exception) when (deadline.IsCancellationRequested) { return new(ProviderAccessOutcome.PreflightFailed, Preflight: new(ProviderPreflightOutcome.DeadlineExceeded)); }
        catch (Exception) { return new(ProviderAccessOutcome.StorageUnavailable); }
    }
    public async ValueTask<ProviderAccessResult> GetAliasAsync(string environment, string alias, CancellationToken token)
    {
        if (!ProviderDeclarationInput.ValidName(environment) || !ValidAlias(alias)) return new(ProviderAccessOutcome.InvalidRequest);
        try
        {
            var record = await environments.FindAsync(EnvironmentId.Create(environment), token);
            if (record is null) return new(ProviderAccessOutcome.EnvironmentNotFound);
            var value = await aliases.ResolveAsync(record, ProviderAlias.Create(alias), token);
            return new(value is null ? ProviderAccessOutcome.NotFound : ProviderAccessOutcome.Found, value is null ? null : new(value.Alias, value.Binding));
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (Exception) { return new(ProviderAccessOutcome.StorageUnavailable); }
    }
    public async ValueTask<ProviderAccessResult> SetAliasAsync(string environment, string alias, ProviderSelector target,
        bool expectedAbsent, ProviderSelector? expected, string actor, CancellationToken token)
    {
        if (!ProviderDeclarationInput.ValidName(environment) || !ValidAlias(alias) || !Valid(target) ||
            (expectedAbsent ? expected is not null : expected is null || !Valid(expected)) ||
            string.IsNullOrWhiteSpace(actor) || actor.Length > 256) return new(ProviderAccessOutcome.InvalidRequest);
        try
        {
            var record = await environments.FindAsync(EnvironmentId.Create(environment), token);
            if (record is null) return new(ProviderAccessOutcome.EnvironmentNotFound);
            var proposed = await identities.ResolveAsync(record, target, token);
            if (proposed is null) return new(ProviderAccessOutcome.TargetUnavailable);
            var previous = expected is null ? null : await identities.ResolveAsync(record, expected, token);
            if (expected is not null && previous is null) return new(ProviderAccessOutcome.StaleExpectation);
            var changed = await aliases.ChangeAsync(record, ProviderAlias.Create(alias), previous, proposed, actor, token);
            return new(changed.Outcome switch
            {
                ProviderAliasChangeOutcome.Changed => ProviderAccessOutcome.Changed,
                ProviderAliasChangeOutcome.Unchanged => ProviderAccessOutcome.Unchanged,
                ProviderAliasChangeOutcome.PreflightFailed => ProviderAccessOutcome.PreflightFailed,
                ProviderAliasChangeOutcome.StaleExpectation => ProviderAccessOutcome.StaleExpectation,
                ProviderAliasChangeOutcome.TargetUnavailable => ProviderAccessOutcome.TargetUnavailable,
                ProviderAliasChangeOutcome.InstanceAlreadyBound => ProviderAccessOutcome.InstanceAlreadyBound,
                _ => ProviderAccessOutcome.StorageUnavailable
            }, changed.Outcome is ProviderAliasChangeOutcome.Changed or ProviderAliasChangeOutcome.Unchanged
                ? new(ProviderAlias.Create(alias), proposed) : null, changed.Preflight);
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (Exception) { return new(ProviderAccessOutcome.StorageUnavailable); }
    }
    private static bool Valid(ProviderSelector value) => value.Revision > 0 &&
        (value.Name is not null ? value.InstanceId is null && ProviderDeclarationInput.ValidName(value.Name) : ValidIdentity(value.InstanceId));
    private static bool ValidIdentity(string? value)
    {
        try { return value is not null && ProviderInstanceId.Parse(value).ToString() == value; }
        catch (ArgumentException) { return false; }
        catch (FormatException) { return false; }
    }
    private static bool ValidAlias(string value)
    {
        try { _ = ProviderAlias.Create(value); return true; }
        catch (ArgumentException) { return false; }
    }
}
