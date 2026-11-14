using Haby.Domain;

namespace Haby.Application;

internal sealed record InternalActionContract(string Subject, string ProviderKind, string Configuration,
    string Implementation, string Action, string InputFormat, string OutputContract);
internal sealed record InternalActionContext(DurableOperationKey Key, DurablePlanningInputs Inputs,
    ProviderInput Subject, ExactProviderResolution Provider, PreparedProviderConfiguration Configuration,
    string ApplicationVersion, Guid Marker, Guid ManagedIdentity, PlacementDeclaration? Placement, Guid InstallationId = default);
internal enum InternalActionOutcome { Confirmed, Uncertain, Refused, Unavailable, NotConfirmed }
internal sealed record InternalActionResult(InternalActionOutcome Outcome, string Code, string? Receipt = null);

/// <summary>Trusted exact registration. Implementations own bounded codecs and semantic evidence.
/// All methods except Execute/Observe are pure and must never resolve material or perform I/O.</summary>
internal interface IInternalActionHandler
{
    InternalActionContract Contract { get; }
    bool ObservationRecovery { get; }
    InternalManagedTarget? ManagedTarget(InternalActionContext context) => null;
    bool RequiresPreparedConfiguration => false;
    InternalIdentityConfirmation? IdentityConfirmation(InternalActionContext context, string payload, string receipt) => null;
    // Pure preparation admission is distinct from permission to create an executable intent.
    IReadOnlyList<ManifestLocalId> PreparationDocuments(InternalActionContext context)
    {
        var payload = Prepare(context);
        if (!Validate(context, payload)) throw new InvalidDataException("Preparation admission refused.");
        return [];
    }
    string Prepare(InternalActionContext context);
    bool Validate(InternalActionContext context, string payload);
    bool ValidateReceipt(InternalActionContext context, string payload, string receipt, bool observation);
    ValueTask<InternalActionResult> ExecuteAsync(InternalActionContext context, string payload, CancellationToken token);
    ValueTask<InternalActionResult> ObserveAsync(InternalActionContext context, string payload, CancellationToken token);
}

/// <summary>Common snapshot wrapper; the Placement payload remains owned by its exact codec.</summary>
internal static class InternalPlacementInput
{
    private static readonly AdapterInputContractId Contract = AdapterInputContractId.Create("haby.exact-placement/v1");
    internal static ExactAdapterInput Encode(Guid id, PlacementDeclaration value) => new(Contract,
        new ConfigurationObject([new("id", new ConfigurationString(id.ToString("N"))),
            new("name", new ConfigurationString(value.Name.Value)), new("revision", new ConfigurationString(value.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            new("contract", new ConfigurationString(value.Contract)), new("format", new ConfigurationString(value.FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            new("payload", new ConfigurationString(value.Payload))]));
    internal static (Guid Id, PlacementDeclaration Declaration) Decode(ExactAdapterInput input)
    {
        if (input.Contract != Contract || input.Value.Members.Count != 6) throw new InvalidDataException("Placement input unavailable.");
        string Read(string key) => input.Value.Members.TryGetValue(key, out var value) && value is ConfigurationString text ? text.Value : throw new InvalidDataException("Placement input unavailable.");
        if (!Guid.TryParseExact(Read("id"), "N", out var id) || id == Guid.Empty ||
            !int.TryParse(Read("revision"), out var revision) || revision <= 0 || !int.TryParse(Read("format"), out var format)) throw new InvalidDataException("Placement input unavailable.");
        return (id, new(ManifestLocalId.Create(Read("name")), revision, Read("contract"), format, Read("payload")));
    }
}

// Pure adapter projections. Persistence alone owns locks, catalog entities and transactions.
internal sealed record InternalManagedTarget(string Contract, int Format, string Payload);
internal sealed record InternalIdentityConfirmation(string Role, string Contract, int Format,
    string InventoryPayload, string Authenticator, string Audience, string ValidationPayload);
