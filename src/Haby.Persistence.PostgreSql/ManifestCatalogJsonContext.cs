using System.Text.Json.Serialization;
using Haby.Manifest;

namespace Haby.Persistence.PostgreSql;

[JsonSerializable(typeof(ManifestValidationReport))]
internal partial class ManifestCatalogJsonContext : JsonSerializerContext;
