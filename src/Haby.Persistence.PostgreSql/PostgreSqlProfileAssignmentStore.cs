using Haby.Application;
using Haby.Domain;
using ApplicationId = Haby.Domain.ApplicationId;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Haby.Persistence.PostgreSql;

public sealed class PostgreSqlProfileAssignmentStore(IDbContextFactory<ManifestCatalogDbContext> contexts)
    : IProfileAssignmentStore
{
    public async ValueTask<PersistedProfileAssignment?> FindAsync(EnvironmentMetadata environment, ProfileAssignmentTarget target, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await Query(context, environment, target).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : Rehydrate(row, environment, target);
    }
    public async ValueTask<AssignmentResult> ApplyAsync(EnvironmentMetadata environment, ConfigurationProfileAssignment declaration,
        AssignmentPrecondition precondition, AssignmentAudit audit, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var row = await Query(context, environment, declaration.Target).SingleOrDefaultAsync(cancellationToken);
            var current = row is null ? null : Rehydrate(row, environment, declaration.Target);
            // A stale expectation must fail even when the proposed content is identical.
            if (precondition switch
            {
                AssignmentPrecondition.ExpectedAbsence => row is not null,
                AssignmentPrecondition.ExpectedConcurrencyToken match => current is null || current.Token != match.Token,
                _ => true
            }) return new(AssignmentOutcome.Conflict);
            if (current is not null && current.Declaration.Equals(declaration))
            {
                // Verify the token at the database boundary too: an equal-content contender must
                // not report Unchanged if another transaction replaced this state after our read.
                context.Entry(row!).Property(x => x.Token).IsModified = true;
            }
            else
            {
                if (row is null)
                {
                    var (kind, app, component) = Key(declaration.Target);
                    row = new()
                    {
                        EnvironmentRecordId = Guid.Parse(environment.RecordId.ToString()),
                        TargetKind = kind,
                        ApplicationId = app,
                        ComponentRef = component,
                        Snapshot = "",
                        ActorId = ""
                    };
                    context.ProfileAssignments.Add(row);
                }
                row.Token = AssignmentToken.New().Value;
                row.Snapshot = AssignmentSnapshotCodec.Encode(declaration);
                row.ActorId = audit.TechnicalActorId;
                var utc = audit.ChangedAtUtc.ToUniversalTime();
                row.ChangedAtUtc = utc.AddTicks(-(utc.Ticks % 10));
                row.CheckedApplicationRecordId = audit.CheckedRevision is { } r ? Guid.Parse(r.ApplicationRecordId.ToString()) : null;
                row.CheckedRevisionNumber = audit.CheckedRevision?.RevisionNumber.Value;
            }
            var accepted = Rehydrate(row!, environment, declaration.Target);
            await context.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            return new(current is null ? AssignmentOutcome.Created : current.Declaration.Equals(declaration)
                ? AssignmentOutcome.Unchanged : AssignmentOutcome.Updated, accepted);
        }
        catch (DbUpdateConcurrencyException) { return new(AssignmentOutcome.Conflict); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_configuration_profile_assignments" })
        { return new(AssignmentOutcome.Conflict); }
    }
    private static IQueryable<ProfileAssignmentRow> Query(ManifestCatalogDbContext context, EnvironmentMetadata environment, ProfileAssignmentTarget target)
    {
        var (kind, app, component) = Key(target);
        var id = Guid.Parse(environment.RecordId.ToString());
        return context.ProfileAssignments.Where(x => x.EnvironmentRecordId == id && x.TargetKind == kind && x.ApplicationId == app && x.ComponentRef == component);
    }
    private static (string Kind, string Application, string Component) Key(ProfileAssignmentTarget target) => target switch
    {
        ProfileAssignmentTarget.Environment => ("environment", "", ""),
        ProfileAssignmentTarget.Application a => ("application", a.ApplicationId.Value, ""),
        ProfileAssignmentTarget.Component c => ("component", c.ApplicationId.Value, c.ComponentRef.Value),
        _ => throw new ArgumentException("Invalid assignment target.")
    };
    private static PersistedProfileAssignment Rehydrate(ProfileAssignmentRow row, EnvironmentMetadata environment, ProfileAssignmentTarget target)
    {
        try
        {
            if (row.ActorId.Length is 0 or > 256 || row.ActorId.Any(char.IsControl) || row.ChangedAtUtc.Offset != TimeSpan.Zero ||
                (target is ProfileAssignmentTarget.Component ? row.CheckedApplicationRecordId is null || row.CheckedRevisionNumber is null or <= 0 :
                    row.CheckedApplicationRecordId is not null || row.CheckedRevisionNumber is not null)) throw new InvalidDataException();
            ManifestRevisionRef? revision = row.CheckedApplicationRecordId is { } app
                ? ManifestRevisionRef.Create(ApplicationRecordId.Parse(app.ToString()), ManifestRevisionNumber.Create(row.CheckedRevisionNumber!.Value)) : null;
            return new(environment, AssignmentSnapshotCodec.Decode(row.Snapshot, target), AssignmentToken.Parse(row.Token.ToString()),
                new(row.ActorId, row.ChangedAtUtc, revision));
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or NullReferenceException)
        { throw new InvalidDataException("Stored assignment is invalid or unsupported."); }
    }
}

public sealed class PostgreSqlProfileAssignmentScopeQuery(IDbContextFactory<ManifestCatalogDbContext> contexts, IManifestRevisionQuery revisions)
    : IProfileAssignmentScopeQuery
{
    public async ValueTask<bool> ApplicationExistsAsync(ApplicationId application, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        return await context.Applications.AnyAsync(x => x.ApplicationId == application.Value, cancellationToken);
    }
    public async ValueTask<ManifestRevision?> FindVersionAsync(ApplicationId application, ApplicationVersion version, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await context.Revisions.AsNoTracking()
            .Where(x => x.ApplicationId == application.Value && x.ApplicationVersion == version.Value)
            .Select(x => new { x.ApplicationRecordId, x.LocalRevisionNumber }).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : await revisions.FindAsync(ManifestRevisionRef.Create(ApplicationRecordId.Parse(row.ApplicationRecordId.ToString()),
            ManifestRevisionNumber.Create(row.LocalRevisionNumber)), cancellationToken);
    }
}
