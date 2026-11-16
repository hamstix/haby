using System.Text.Json.Serialization;

internal sealed record BatchQualificationMember(string DeploymentId, long Generation);
internal sealed record BatchQualificationFile(BatchQualificationMember[] Members);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BatchQualificationFile))]
internal partial class BatchQualificationMetadata : JsonSerializerContext;
