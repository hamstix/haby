using System.Globalization;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed record CliImportedProfile(CliProfileReference Reference, string TechnicalActorId, string ImportedAtUtc);
internal sealed record CliProfileListData(CliImportedProfile[] Items, string? NextAfterProfileId);
internal sealed record CliProfileRevisionListData(string ProfileId, CliImportedProfile[] Items, int? NextAfterRevision);

internal static class ProfileDiscoveryPresentation
{
    private static CliImportedProfile Read(ProfileMetadata item)
    {
        if (item.Reference is not { } r || !ProfilePresentation.ValidProfileId(r.ProfileId) || r.Revision <= 0 ||
            string.IsNullOrWhiteSpace(item.TechnicalActorId) || item.TechnicalActorId.Length > 256 || item.TechnicalActorId.Any(char.IsControl) ||
            !DateTimeOffset.TryParseExact(item.ImportedAtUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant) ||
            instant.Offset != TimeSpan.Zero || instant == DateTimeOffset.MinValue || instant == DateTimeOffset.MaxValue)
            throw new InvalidDataException("Invalid Profile metadata.");
        return new(new(r.ProfileId, r.Revision), item.TechnicalActorId, item.ImportedAtUtc);
    }

    internal static CliProfileListData Read(ListProfilesReply reply, int size, string? after)
    {
        if (reply.Items.Count > size) throw new InvalidDataException("Invalid Profile page.");
        var items = reply.Items.Select(Read).ToArray();
        var previous = after;
        foreach (var item in items)
        {
            if (previous is not null && StringComparer.Ordinal.Compare(item.Reference.ProfileId, previous) <= 0)
                throw new InvalidDataException("Invalid Profile order.");
            previous = item.Reference.ProfileId;
        }
        if (reply.HasNextAfterProfileId && (items.Length != size || reply.NextAfterProfileId != previous ||
            !ProfilePresentation.ValidProfileId(reply.NextAfterProfileId))) throw new InvalidDataException("Invalid Profile continuation.");
        return new(items, reply.HasNextAfterProfileId ? reply.NextAfterProfileId : null);
    }

    internal static CliProfileRevisionListData Read(ListProfileRevisionsReply reply, string id, int size, int? after)
    {
        if (reply.ProfileId != id || !ProfilePresentation.ValidProfileId(id) || reply.Items.Count > size)
            throw new InvalidDataException("Invalid Profile revision page.");
        var items = reply.Items.Select(Read).ToArray();
        var previous = after ?? 0;
        foreach (var item in items)
        {
            if (item.Reference.ProfileId != id || item.Reference.Revision <= previous) throw new InvalidDataException("Invalid Profile revision order.");
            previous = item.Reference.Revision;
        }
        if (reply.HasNextAfterRevision && (items.Length != size || reply.NextAfterRevision != previous || reply.NextAfterRevision <= 0))
            throw new InvalidDataException("Invalid Profile revision continuation.");
        return new(id, items, reply.HasNextAfterRevision ? reply.NextAfterRevision : null);
    }
}

internal sealed partial class CliCommandContext
{
    internal Task<int> ListProfilesAsync(int size, string? after, CancellationToken token)
    {
        if (size is < 1 or > 100 || after is not null && !ProfilePresentation.ValidProfileId(after)) return InvalidProfilePage();
        return ExecuteAsync(async (client, ct) =>
        {
            var request = new ListProfilesRequest { PageSize = size };
            if (after is not null) request.AfterProfileId = after;
            var data = ProfileDiscoveryPresentation.Read(await client.ListProfilesAsync(request, ct), size, after);
            return jsonOutput() ? jsonPresenter.PrintProfiles(data) : presenter.PrintProfiles(data);
        }, token);
    }

    internal Task<int> ListProfileRevisionsAsync(string id, int size, int? after, CancellationToken token)
    {
        if (!ProfilePresentation.ValidProfileId(id) || size is < 1 or > 100 || after is <= 0) return InvalidProfilePage();
        return ExecuteAsync(async (client, ct) =>
        {
            var request = new ListProfileRevisionsRequest { ProfileId = id, PageSize = size };
            if (after is not null) request.AfterRevision = after.Value;
            var data = ProfileDiscoveryPresentation.Read(await client.ListProfileRevisionsAsync(request, ct), id, size, after);
            return jsonOutput() ? jsonPresenter.PrintProfileRevisions(data) : presenter.PrintProfileRevisions(data);
        }, token);
    }

    private Task<int> InvalidProfilePage()
    {
        error.WriteLine("Invalid Profile page selector. Use page size 1..100, a valid Profile ID and a positive revision cursor.");
        return Task.FromResult(CliApplication.UsageError);
    }
}

internal sealed partial class CliJsonPresenter
{
    internal int PrintProfiles(CliProfileListData data)
    {
        Write("profile list", "listed", data, NoDiagnostics, CliOutputJsonContext.Default.CliEnvelopeCliProfileListData);
        return 0;
    }
    internal int PrintProfileRevisions(CliProfileRevisionListData data)
    {
        Write("profile revision list", "listed", data, NoDiagnostics, CliOutputJsonContext.Default.CliEnvelopeCliProfileRevisionListData);
        return 0;
    }
}

internal sealed partial class CliTextPresenter
{
    private void ProfileRows(CliImportedProfile[] items)
    {
        _output.WriteLine("PROFILE  REVISION  IMPORTED AT UTC  ACTOR");
        foreach (var item in items) _output.WriteLine($"{item.Reference.ProfileId}  {item.Reference.Revision}  {item.ImportedAtUtc}  {item.TechnicalActorId}");
    }
    internal int PrintProfiles(CliProfileListData data)
    {
        ProfileRows(data.Items);
        if (data.NextAfterProfileId is { } next) _output.WriteLine($"next --after-profile-id {next}");
        return 0;
    }
    internal int PrintProfileRevisions(CliProfileRevisionListData data)
    {
        ProfileRows(data.Items);
        if (data.NextAfterRevision is { } next) _output.WriteLine($"next --after-revision {next}");
        return 0;
    }
}
