using Haby.Domain;
using Haby.Manifest;
using ApplicationId = Haby.Domain.ApplicationId;

namespace Haby.Application;

/// <summary>Closed logical target. Version is validation context, never identity.</summary>
public sealed record PlacementSelectionTarget
{
    private PlacementSelectionTarget(string kind, string selector, string application, string component)
    { Kind = kind; Selector = selector; Application = application; Component = component; }
    public string Kind { get; }
    public string Selector { get; }
    public string Application { get; }
    public string Component { get; }
    public static PlacementSelectionTarget Create(string kind, string selector = "", string application = "", string component = "")
    {
        switch (kind)
        {
            case "named" when application == "" && component == "": ManifestLocalId.Create(selector); break;
            case "environment" when selector == "" && application == "" && component == "": break;
            case "application" when selector == "" && component == "": ApplicationId.Create(application); break;
            case "component" when selector == "": ApplicationId.Create(application); ManifestLocalId.Create(component); break;
            default: throw new ArgumentException("Invalid Placement selection target.");
        }
        return new(kind, selector, application, component);
    }
}
public sealed record PlacementSelectionToken
{
    private PlacementSelectionToken(Guid value) => Value = value;
    public Guid Value { get; }
    public static PlacementSelectionToken New() => new(Guid.CreateVersion7());
    public static PlacementSelectionToken Parse(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
        ? new(id) : throw new ArgumentException("Invalid Placement selection token.");
    public override string ToString() => Value.ToString("D");
}
public abstract record PlacementSelectionPrecondition
{
    private PlacementSelectionPrecondition() { }
    public sealed record ExpectedAbsence : PlacementSelectionPrecondition;
    public sealed record ExpectedToken(PlacementSelectionToken Token) : PlacementSelectionPrecondition;
}
public sealed record PlacementSelectionAudit(string ActorId, DateTimeOffset ChangedAtUtc, ManifestRevisionRef? CheckedRevision);
public sealed record PersistedPlacementSelection(PlacementSelectionTarget Target, PlacementRevisionKey Reference,
    PlacementSelectionToken Token, PlacementSelectionAudit Audit);
public enum PlacementSelectionOutcome
{
    Created, Updated, Unchanged, Found, NotFound, Conflict, InvalidContext, EnvironmentNotFound,
    ApplicationNotFound, ApplicationVersionNotFound, ComponentNotFound, RevisionNotFound
}
public sealed record PlacementSelectionResult(PlacementSelectionOutcome Outcome, PersistedPlacementSelection? Selection = null);
public interface IPlacementSelectionScopeQuery
{
    ValueTask<bool> ApplicationExistsAsync(ApplicationId application, CancellationToken cancellationToken);
    ValueTask<ManifestRevision?> FindVersionAsync(ApplicationId application, ApplicationVersion version, CancellationToken cancellationToken);
}
public interface IPlacementSelectionStore
{
    ValueTask<PersistedPlacementSelection?> FindAsync(EnvironmentRecordId environment, ProviderAlias alias,
        PlacementSelectionTarget target, CancellationToken token);
    ValueTask<PlacementSelectionResult> ApplyAsync(PlacementSelectionTarget target, PlacementRevisionKey reference,
        PlacementSelectionPrecondition precondition, PlacementSelectionAudit audit, CancellationToken token);
}

/// <summary>Preparatory administration. No Provider configuration, network or deployment dependency.</summary>
public sealed class PlacementSelectionAdministration(IEnvironmentQuery environments, IPlacementSelectionScopeQuery scopes,
    IPlacementRevisionStore revisions, IPlacementSelectionStore selections, TimeProvider clock)
{
    public async ValueTask<PlacementSelectionResult> GetAsync(string environment, string alias, PlacementSelectionTarget target, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(target);
        var scope = await Scope(environment, alias, token);
        if (scope.Error is { } error) return new(error);
        var found = await selections.FindAsync(scope.Environment!, scope.Alias!, target, token);
        return found is null ? new(PlacementSelectionOutcome.NotFound) : new(PlacementSelectionOutcome.Found, found);
    }
    public async ValueTask<PlacementSelectionResult> ApplyAsync(string environment, string alias, PlacementSelectionTarget target,
        string name, int revision, string? applicationVersion, PlacementSelectionPrecondition? precondition, string actor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (target is null || precondition is null or PlacementSelectionPrecondition.ExpectedToken { Token: null }) return new(PlacementSelectionOutcome.InvalidContext);
        ManifestLocalId placement; ApplicationVersion? version = null;
        try
        {
            placement = ManifestLocalId.Create(name);
            if (revision <= 0) return new(PlacementSelectionOutcome.InvalidContext);
            if (target.Kind == "component") version = ApplicationVersion.Create(applicationVersion!);
            else if (applicationVersion is not null) return new(PlacementSelectionOutcome.InvalidContext);
        }
        catch (ArgumentException) { return new(PlacementSelectionOutcome.InvalidContext); }
        if (string.IsNullOrEmpty(actor) || actor.Length > 256 || actor.Any(char.IsControl)) throw new ArgumentException("Invalid actor.");
        var scope = await Scope(environment, alias, token);
        if (scope.Error is { } error) return new(error);
        ManifestRevisionRef? checkedRevision = null;
        if (target.Application != "" && !await scopes.ApplicationExistsAsync(ApplicationId.Create(target.Application), token))
            return new(PlacementSelectionOutcome.ApplicationNotFound);
        if (target.Kind == "component")
        {
            var accepted = await scopes.FindVersionAsync(ApplicationId.Create(target.Application), version!, token);
            if (accepted is null) return new(PlacementSelectionOutcome.ApplicationVersionNotFound);
            if (!accepted.Declaration.Spec.Components.ContainsKey(ManifestLocalId.Create(target.Component))) return new(PlacementSelectionOutcome.ComponentNotFound);
            checkedRevision = accepted.Reference;
        }
        var reference = new PlacementRevisionKey(scope.Environment!, scope.Alias!, placement, revision);
        if (await revisions.FindAsync(reference, token) is null) return new(PlacementSelectionOutcome.RevisionNotFound);
        return await selections.ApplyAsync(target, reference, precondition, new(actor, clock.GetUtcNow(), checkedRevision), token);
    }
    private async ValueTask<(EnvironmentRecordId? Environment, ProviderAlias? Alias, PlacementSelectionOutcome? Error)> Scope(string environment, string alias, CancellationToken token)
    {
        EnvironmentId id; ProviderAlias provider;
        try { id = EnvironmentId.Create(environment); provider = ProviderAlias.Create(alias); }
        catch (ArgumentException) { return (null, null, PlacementSelectionOutcome.InvalidContext); }
        var record = await environments.FindAsync(id, token);
        return record is null ? (null, null, PlacementSelectionOutcome.EnvironmentNotFound) : (record, provider, null);
    }
}
