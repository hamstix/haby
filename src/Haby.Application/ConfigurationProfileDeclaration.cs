using System.Text.Json;
using Haby.Domain;
using Haby.Manifest;

namespace Haby.Application;

/// <summary>Maps accepted immutable content to the current portable declaration, never its storage encoding.</summary>
public static class ConfigurationProfileDeclaration
{
    public static byte[] Serialize(ConfigurationProfileRevision profile) =>
        JsonSerializer.SerializeToUtf8Bytes(Map(profile), PortableProfileJson.DeclarationType);

    public static ConfigurationProfileDocument Map(ConfigurationProfileRevision profile) => new()
    {
        ApiVersion = "haby.dev/v1alpha1",
        Kind = "ConfigurationProfile",
        Metadata = new() { Id = profile.Reference.ProfileId.Value, Revision = profile.Reference.Revision },
        Spec = new()
        {
            Documents = profile.Documents.OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToDictionary(x => x.Id.Value,
                x => new ProfileJsonDocument
                {
                    FileName = x.FileName,
                    Format = x.Format == StructuredDocumentFormat.Json ? "json" : throw new InvalidDataException("Unsupported Profile format."),
                    Template = Ordinary(x.Template)
                }, StringComparer.Ordinal),
            StaticContributions = profile.Contributions.OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToDictionary(x => x.Id.Value,
                x => new ProfileContributionDocument
                {
                    DocumentRef = x.Document.Value,
                    Path = x.Path,
                    Value = Ordinary(x.Value),
                    SecretProjections = x.SecretProjections.OrderBy(p => p.Path, StringComparer.Ordinal).Select(p => new ProfileSecretProjectionDocument
                    {
                        Path = p.Path,
                        Format = p.Format switch
                        {
                            ProfileSecretFormat.String => "string",
                            ProfileSecretFormat.Json => "json",
                            _ => throw new InvalidDataException("Unsupported Profile secret format.")
                        },
                        SecretRef = new() { Name = p.SecretRef.Name, Version = p.SecretRef.Version }
                    }).ToList()
                }, StringComparer.Ordinal),
            BindingRules = profile.Rules.OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToDictionary(x => x.Id.Value,
                x => new ProfileRuleDocument
                {
                    Slot = x.Slot.Value,
                    RendererRef = x.Renderer.Value,
                    Options = Ordinary(x.Defaults),
                    DocumentProjections = x.Projections.OrderBy(p => p.Document.Value, StringComparer.Ordinal)
                        .ThenBy(p => p.Path, StringComparer.Ordinal).Select(p => new ProfileProjectionDocument
                        { DocumentRef = p.Document.Value, Path = p.Path }).ToList()
                }, StringComparer.Ordinal)
        }
    };

    private static JsonElement Ordinary(ConfigurationValue value)
    {
        using var json = JsonDocument.Parse(ConfigurationJson.Serialize(value).Json
            ?? throw new InvalidDataException("Unsupported Profile value."));
        return json.RootElement.Clone();
    }
}
