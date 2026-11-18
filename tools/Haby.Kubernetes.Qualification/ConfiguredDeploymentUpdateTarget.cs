using System.Text.Json.Nodes;

internal static class ConfiguredDeploymentUpdateTarget
{
    internal static string PrepareEnvelope(JsonNode envelope, string image)
    {
        const string package = "ghcr.io/hamstix/haby-runtime-consumer";
        const string digest = "sha256:192203c5c93a3d21989b0b7b54a1e687fa03c35ea75b14fb11018aef99fd378a";
        if (image != package + "@" + digest) throw new InvalidOperationException("Update qualification requires the existing accepted consumer digest.");
        // Explicit default port changes the reference while preserving the pinned
        // registry/repository/digest. The production grammar accepts digest-only.
        var targetImage = "ghcr.io:443/hamstix/haby-runtime-consumer@" + digest;
        envelope["applicationVersion"] = "configured-update-v2";
        envelope["manifest"]!["spec"]!["components"]!["api"]!["workloads"]!["main"]!["properties"]!["image"]!["reference"] = targetImage;
        return targetImage;
    }
}
