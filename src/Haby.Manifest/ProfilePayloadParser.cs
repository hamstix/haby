using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Corvus.Text.Json.Validator;
using Haby.Domain;

namespace Haby.Manifest;

public sealed record ProfileWireDiagnostic(string Code, string Path);

/// <summary>Wire acceptance only; no persistence, authorization or executable availability claim.</summary>
public sealed class ProfileWireResult<T> where T : class
{
    internal ProfileWireResult(T? declaration, IEnumerable<ProfileWireDiagnostic> errors)
    {
        var ordered = errors.Distinct().OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal).ToArray();
        ErrorCount = ordered.Length;
        Diagnostics = ordered.Take(100).ToImmutableArray();
        Declaration = ErrorCount == 0 ? declaration : null;
    }
    public T? Declaration { get; }
    public bool IsSuccess => Declaration is not null;
    public IReadOnlyList<ProfileWireDiagnostic> Diagnostics { get; }
    public int ErrorCount { get; }
    public bool IsTruncated => ErrorCount > Diagnostics.Count;
}

/// <summary>Strict standalone Profile and assignment intake. All ordinary values remain ordinary.</summary>
public static class ProfilePayloadParser
{
    public const int MaxPayloadBytes = 1024 * 1024;
    private static readonly ManifestJsonContext Context = ManifestJsonContext.CreateStrict();
    private static readonly Lazy<JsonSchema> ProfileSchema = new(() => Load("configuration-profile"));
    private static readonly Lazy<JsonSchema> AssignmentSchema = new(() => Load("configuration-profile-assignment"));

    public static ProfileWireResult<ConfigurationProfileRevision> ParseProfile(ReadOnlyMemory<byte> payload) =>
        Parse(payload, ProfileSchema, Context.ConfigurationProfileDocument, MapProfile);
    public static ProfileWireResult<ConfigurationProfileAssignment> ParseAssignment(ReadOnlyMemory<byte> payload) =>
        Parse(payload, AssignmentSchema, Context.ConfigurationProfileAssignmentDocument, MapAssignment);

    private static ProfileWireResult<T> Parse<TDto, T>(ReadOnlyMemory<byte> payload, Lazy<JsonSchema> schema,
        JsonTypeInfo<TDto> type, Func<TDto, List<ProfileWireDiagnostic>, T?> map) where T : class
    {
        var errors = new List<ProfileWireDiagnostic>();
        if (payload.Length > MaxPayloadBytes) return new(null, [new("profile.input.too-large", "")]);
        if (payload.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) || !ManifestPayloadParser.IsValidUtf8(payload.Span))
            return new(null, [new("profile.syntax.invalid-encoding", "")]);
        // Reuse the Application raw scanner before any deserializer can lose duplicate members.
        foreach (var finding in ManifestPayloadParser.ScanSyntax(payload.Span))
            errors.Add(new("profile.syntax." + finding.Code, finding.Path));
        if (errors.Count != 0) return new(null, errors);
        using var collector = Corvus.Text.Json.JsonSchemaResultsCollector.Create(Corvus.Text.Json.JsonSchemaResultsLevel.Detailed, 20000);
        if (!schema.Value.Validate(payload.ToArray(), collector))
        {
            foreach (var result in collector.EnumerateResults())
                if (!result.IsMatch)
                    errors.Add(new("profile.schema.constraint-violation", result.GetDocumentEvaluationLocationText() ?? ""));
            if (errors.Count == 0) errors.Add(new("profile.schema.constraint-violation", ""));
            return new(null, errors);
        }
        try
        {
            var dto = JsonSerializer.Deserialize(payload.Span, type);
            if (dto is null) return new(null, [new("profile.schema.constraint-violation", "")]);
            return new(map(dto, errors), errors);
        }
        catch (JsonException)
        {
            return new(null, [new("profile.schema.constraint-violation", "")]);
        }
        catch (ArgumentException)
        {
            return new(null, [new("profile.declaration.invalid", "")]);
        }
    }

    private static ConfigurationProfileRevision? MapProfile(ConfigurationProfileDocument dto, List<ProfileWireDiagnostic> errors)
    {
        var spec = dto.Spec;
        var total = spec.StaticContributions.Count + spec.StaticContributions.Values.Sum(x => x.SecretProjections.Count) +
            spec.BindingRules.Values.Sum(x => x.DocumentProjections.Count);
        if (total > 256) errors.Add(new("profile.declaration.limit-exceeded", "/spec"));
        var contributions = new List<ProfileStaticContribution>();
        foreach (var (id, source) in spec.StaticContributions)
        {
            try
            {
                contributions.Add(new(ManifestLocalId.Create(id), ManifestLocalId.Create(source.DocumentRef), source.Path,
                    ConfigurationValueMapper.FromValidatedJson(source.Value), source.SecretProjections.Select(x =>
                        new ProfileSecretProjection(x.Path, x.Format == "string" ? ProfileSecretFormat.String : ProfileSecretFormat.Json,
                            new(x.SecretRef.Name, x.SecretRef.Version)))));
            }
            catch (ArgumentException)
            {
                errors.Add(new("profile.secret-projections.invalid", "/spec/staticContributions/" + id + "/secretProjections"));
            }
        }
        foreach (var (id, rule) in spec.BindingRules)
            if (rule.DocumentProjections.Select(x => (x.DocumentRef, x.Path)).Distinct().Count() != rule.DocumentProjections.Count)
                errors.Add(new("profile.projection.duplicate", "/spec/bindingRules/" + id + "/documentProjections"));
        if (errors.Count != 0) return null;
        return new(new(ProfileId.Create(dto.Metadata.Id), dto.Metadata.Revision),
            spec.Documents.Select(x => new ProfileDocument(ManifestLocalId.Create(x.Key), x.Value.FileName,
                StructuredDocumentFormat.Json, ConfigurationValueMapper.FromValidatedJson(x.Value.Template))), contributions,
            spec.BindingRules.Select(x => new ProfileBindingRule(ManifestLocalId.Create(x.Key), ManifestLocalId.Create(x.Value.Slot),
                ConfigurationRendererId.Create(x.Value.RendererRef), (ConfigurationObject)ConfigurationValueMapper.FromValidatedJson(x.Value.Options),
                x.Value.DocumentProjections.Select(p => new ProfileProjection(ManifestLocalId.Create(p.DocumentRef), p.Path)))));
    }

    private static ConfigurationProfileAssignment? MapAssignment(ConfigurationProfileAssignmentDocument dto, List<ProfileWireDiagnostic> errors)
    {
        var decisions = dto.Spec.Decisions.Select<ProfileDecisionDocument, ProfileDecision>(x => x switch
        {
            ProfileUseDocument use => new ProfileDecision.Use(new(ProfileId.Create(use.ProfileRef.ProfileId), use.ProfileRef.Revision)),
            ProfileDisableDocument disable => new ProfileDecision.Disable(ProfileId.Create(disable.ProfileId)),
            _ => throw new InvalidOperationException("Unsupported validated decision.")
        }).ToArray();
        if (decisions.Select(x => x.ProfileId).Distinct().Count() != decisions.Length)
        {
            errors.Add(new("profile.selection.duplicate", "/spec/decisions"));
            return null;
        }
        ProfileAssignmentTarget target = dto.Spec.Target switch
        {
            ProfileEnvironmentTargetDocument => new ProfileAssignmentTarget.Environment(),
            ProfileApplicationTargetDocument app => new ProfileAssignmentTarget.Application(Haby.Domain.ApplicationId.Create(app.ApplicationId)),
            ProfileComponentTargetDocument component => new ProfileAssignmentTarget.Component(Haby.Domain.ApplicationId.Create(component.ApplicationId), ManifestLocalId.Create(component.ComponentRef)),
            _ => throw new InvalidOperationException("Unsupported validated target.")
        };
        return new(target, decisions);
    }
    private static JsonSchema Load(string name)
    {
        using var stream = typeof(ProfilePayloadParser).Assembly.GetManifestResourceStream("Haby.Manifest." + name + ".v1alpha1.json")
            ?? throw new InvalidOperationException("The Profile schema resource is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return JsonSchema.FromText(reader.ReadToEnd(), "https://haby.dev/schemas/" + name + ".v1alpha1.json",
            new JsonSchema.Options(allowFileSystemAndHttpResolution: false));
    }
}
