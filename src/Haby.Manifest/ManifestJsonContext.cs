using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haby.Manifest;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ImportApplicationEnvelope))]
[JsonSerializable(typeof(ApplicationManifest))]
[JsonSerializable(typeof(BindingTarget))]
[JsonSerializable(typeof(ConfigurationDocument))]
[JsonSerializable(typeof(ParameterDefinition))]
public partial class ManifestJsonContext : JsonSerializerContext
{
    public static ManifestJsonContext CreateStrict()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowOutOfOrderMetadataProperties = true,
            RespectNullableAnnotations = true
        };
        return new ManifestJsonContext(options);
    }
}
