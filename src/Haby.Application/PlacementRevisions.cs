using Haby.Domain;

namespace Haby.Application;

public sealed record PlacementRevisionKey
{
    public PlacementRevisionKey(EnvironmentRecordId environment, ProviderAlias alias, ManifestLocalId name, int revision)
    {
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        Alias = alias ?? throw new ArgumentNullException(nameof(alias));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        if (revision <= 0) throw new ArgumentOutOfRangeException(nameof(revision));
        Revision = revision;
    }
    public EnvironmentRecordId Environment { get; }
    public ProviderAlias Alias { get; }
    public ManifestLocalId Name { get; }
    public int Revision { get; }
}

/// <summary>Detached portable content. Only the registered adapter interprets its versioned payload.</summary>
public sealed record PlacementDeclaration(ManifestLocalId Name, int Revision, string Contract, int FormatVersion, string Payload);

public interface IPlacementCodec
{
    PlacementDeclaration Decode(ReadOnlyMemory<byte> payload);
    PlacementDeclaration Read(string contract, int formatVersion, string payload);
}

/// <summary>Value-free, bounded contract refusal; implementations must never include submitted values.</summary>
public sealed class PlacementDeclarationException(string code) : ArgumentException("Invalid Placement declaration.")
{
    public string Code { get; } = code;
}

public sealed record AcceptedPlacementRevision(Guid Id, PlacementRevisionKey Key, PlacementDeclaration Declaration, ImportAuditMetadata Audit);
public sealed record PlacementCreateResult(AcceptedPlacementRevision Revision, bool Created);

public interface IPlacementRevisionStore
{
    ValueTask<PlacementCreateResult> CompareAndCreateAsync(PlacementRevisionKey key, PlacementDeclaration declaration,
        ImportAuditMetadata audit, CancellationToken token);
    ValueTask<AcceptedPlacementRevision?> FindAsync(PlacementRevisionKey key, CancellationToken token);
}

public enum PlacementOutcome { Valid, Created, ExistingEquivalent, Found, NotFound, InvalidContext, EnvironmentNotFound, InvalidDeclaration, RevisionContentConflict }
public sealed record PlacementResult(PlacementOutcome Outcome, AcceptedPlacementRevision? Revision = null,
    PlacementDeclaration? Declaration = null, string? DiagnosticCode = null);

/// <summary>Preparatory intake only. No Provider lookup, selections or deployment effects.</summary>
public sealed class PlacementAdministration(IEnvironmentQuery environments, IPlacementCodec codec, IPlacementRevisionStore store, TimeProvider clock)
{
    public PlacementResult Validate(ReadOnlyMemory<byte> payload)
    {
        try { return new(PlacementOutcome.Valid, Declaration: codec.Decode(payload)); }
        catch (PlacementDeclarationException error) { return new(PlacementOutcome.InvalidDeclaration, DiagnosticCode: error.Code); }
    }

    public async ValueTask<PlacementResult> ImportAsync(string environment, string alias, ReadOnlyMemory<byte> payload, string actor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var scope = await Scope(environment, alias, token);
        if (scope.Error is { } error) return new(error);
        var validation = Validate(payload);
        if (validation.Outcome != PlacementOutcome.Valid) return validation;
        if (actor.Length is 0 or > 256 || actor.Any(char.IsControl)) throw new ArgumentException("Invalid import actor.", nameof(actor));
        var declaration = validation.Declaration!;
        var key = new PlacementRevisionKey(scope.Environment!, scope.Alias!, declaration.Name, declaration.Revision);
        var result = await store.CompareAndCreateAsync(key, declaration, new(actor, clock.GetUtcNow()), token);
        return new(result.Created ? PlacementOutcome.Created : result.Revision.Declaration == declaration
            ? PlacementOutcome.ExistingEquivalent : PlacementOutcome.RevisionContentConflict, result.Revision);
    }

    public async ValueTask<PlacementResult> GetAsync(string environment, string alias, string name, int revision, CancellationToken token)
    {
        ManifestLocalId id;
        try { id = ManifestLocalId.Create(name); if (revision <= 0) return new(PlacementOutcome.InvalidContext); }
        catch (ArgumentException) { return new(PlacementOutcome.InvalidContext); }
        var scope = await Scope(environment, alias, token);
        if (scope.Error is { } error) return new(error);
        var found = await store.FindAsync(new(scope.Environment!, scope.Alias!, id, revision), token);
        return found is null ? new(PlacementOutcome.NotFound) : new(PlacementOutcome.Found, found);
    }

    private async ValueTask<(EnvironmentRecordId? Environment, ProviderAlias? Alias, PlacementOutcome? Error)> Scope(string environment, string alias, CancellationToken token)
    {
        EnvironmentId id; ProviderAlias provider;
        try { id = EnvironmentId.Create(environment); provider = ProviderAlias.Create(alias); }
        catch (ArgumentException) { return (null, null, PlacementOutcome.InvalidContext); }
        var record = await environments.FindAsync(id, token);
        return record is null ? (null, null, PlacementOutcome.EnvironmentNotFound) : (record, provider, null);
    }
}
