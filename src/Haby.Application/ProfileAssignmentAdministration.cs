using Haby.Domain;
using ApplicationId = Haby.Domain.ApplicationId;
using Haby.Manifest;

namespace Haby.Application;

public sealed record AssignmentToken
{
    private AssignmentToken(Guid value) => Value = value;
    public Guid Value { get; }
    public static AssignmentToken New() => new(Guid.CreateVersion7());
    public static AssignmentToken Parse(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
        ? new(id) : throw new ArgumentException("Invalid assignment token.");
    public override string ToString() => Value.ToString("D");
}
public abstract record AssignmentPrecondition
{
    private AssignmentPrecondition() { }
    public sealed record ExpectedAbsence : AssignmentPrecondition;
    public sealed record ExpectedConcurrencyToken(AssignmentToken Token) : AssignmentPrecondition;
}
public sealed record AssignmentAudit(string TechnicalActorId, DateTimeOffset ChangedAtUtc, ManifestRevisionRef? CheckedRevision);
public sealed record PersistedProfileAssignment(EnvironmentMetadata Environment, ConfigurationProfileAssignment Declaration,
    AssignmentToken Token, AssignmentAudit Audit);
public enum AssignmentOutcome
{
    Valid, InvalidDeclaration, Created, Updated, Unchanged, Found, NotFound, Conflict,
    InvalidContext, EnvironmentNotFound, ApplicationNotFound, ApplicationVersionNotFound, ComponentNotFound, ProfileRevisionNotFound
}
public sealed record AssignmentResult(AssignmentOutcome Outcome, PersistedProfileAssignment? Assignment = null,
    ProfileWireResult<ConfigurationProfileAssignment>? Report = null);

/// <summary>Exact accepted scope validation, with no latest-version selection.</summary>
public interface IProfileAssignmentScopeQuery
{
    ValueTask<bool> ApplicationExistsAsync(ApplicationId application, CancellationToken cancellationToken);
    ValueTask<ManifestRevision?> FindVersionAsync(ApplicationId application, ApplicationVersion version, CancellationToken cancellationToken);
}
/// <summary>Atomic unique-scope CAS. Reads must refuse corrupt/unsupported storage.</summary>
public interface IProfileAssignmentStore
{
    ValueTask<PersistedProfileAssignment?> FindAsync(EnvironmentMetadata environment, ProfileAssignmentTarget target, CancellationToken cancellationToken);
    ValueTask<AssignmentResult> ApplyAsync(EnvironmentMetadata environment, ConfigurationProfileAssignment declaration,
        AssignmentPrecondition precondition, AssignmentAudit audit, CancellationToken cancellationToken);
}

/// <summary>Trusted assignment administration; deliberately has no SecretStore or Provider dependency.</summary>
public sealed class ProfileAssignmentAdministration(IEnvironmentCatalog environments, IProfileAssignmentScopeQuery scopes,
    IConfigurationProfileRevisionQuery profiles, IProfileAssignmentStore assignments, TimeProvider clock)
{
    public static ProfileWireResult<ConfigurationProfileAssignment> Validate(ReadOnlyMemory<byte> payload, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ProfilePayloadParser.ParseAssignment(payload);
    }

    public async ValueTask<AssignmentResult> GetAsync(string environment, ProfileAssignmentTarget target, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        EnvironmentId name;
        try { name = EnvironmentId.Create(environment); }
        catch (ArgumentException) { return new(AssignmentOutcome.InvalidContext); }
        var scope = await environments.GetAsync(name, token);
        if (scope is null) return new(AssignmentOutcome.EnvironmentNotFound);
        var found = await assignments.FindAsync(scope, target, token);
        return found is null ? new(AssignmentOutcome.NotFound) : new(AssignmentOutcome.Found, found);
    }

    public async ValueTask<AssignmentResult> ApplyAsync(ReadOnlyMemory<byte> payload, string environment, string? applicationVersion,
        AssignmentPrecondition precondition, string actor, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(precondition);
        if (precondition is AssignmentPrecondition.ExpectedConcurrencyToken { Token: null }) throw new ArgumentException("Missing token.");
        if (string.IsNullOrEmpty(actor) || actor.Length > 256 || actor.Any(char.IsControl)) throw new ArgumentException("Invalid actor.");
        var parsed = Validate(payload, token);
        if (!parsed.IsSuccess) return new(AssignmentOutcome.InvalidDeclaration, Report: parsed);
        var declaration = parsed.Declaration!;
        EnvironmentId name;
        ApplicationVersion? version = null;
        try
        {
            name = EnvironmentId.Create(environment);
            if (declaration.Target is ProfileAssignmentTarget.Component)
            {
                if (applicationVersion is null) return new(AssignmentOutcome.InvalidContext);
                version = ApplicationVersion.Create(applicationVersion);
            }
            else if (applicationVersion is not null) return new(AssignmentOutcome.InvalidContext);
        }
        catch (ArgumentException) { return new(AssignmentOutcome.InvalidContext); }
        var scope = await environments.GetAsync(name, token);
        if (scope is null) return new(AssignmentOutcome.EnvironmentNotFound);
        ManifestRevisionRef? checkedRevision = null;
        var application = declaration.Target switch
        {
            ProfileAssignmentTarget.Application a => a.ApplicationId,
            ProfileAssignmentTarget.Component c => c.ApplicationId,
            _ => null
        };
        if (application is not null && !await scopes.ApplicationExistsAsync(application, token)) return new(AssignmentOutcome.ApplicationNotFound);
        if (declaration.Target is ProfileAssignmentTarget.Component component)
        {
            var revision = await scopes.FindVersionAsync(component.ApplicationId, version!, token);
            if (revision is null) return new(AssignmentOutcome.ApplicationVersionNotFound);
            if (!revision.Declaration.Spec.Components.ContainsKey(component.ComponentRef)) return new(AssignmentOutcome.ComponentNotFound);
            checkedRevision = revision.Reference;
        }
        foreach (var use in declaration.Decisions.OfType<ProfileDecision.Use>())
            if (await profiles.FindAsync(use.Reference, token) is null) return new(AssignmentOutcome.ProfileRevisionNotFound);
        return await assignments.ApplyAsync(scope, declaration, precondition, new(actor, clock.GetUtcNow(), checkedRevision), token);
    }
}
