using System.Text.Json;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Xunit;

namespace Haby.Persistence.PostgreSql.Tests;

public sealed class ManifestCatalogJsonContextTests
{
    [Fact]
    public void Historical_warnings_round_trip_through_generated_metadata()
    {
        var report = new ManifestValidationReport(
            [new ManifestDiagnostic("manifest.resource.unused", ManifestDiagnosticSeverity.Warning,
                "/spec/resources/db", "Unused resource", new Dictionary<string, string> { ["resource"] = "db" },
                ["/spec/components/worker"], new ManifestSourceLocation(3, 5))], false);
        var metadata = ManifestCatalogJsonContext.Default.ManifestValidationReport;

        var json = JsonSerializer.Serialize(report, metadata);
        var restored = JsonSerializer.Deserialize(json, metadata);

        Assert.NotNull(restored);
        Assert.False(restored.Truncated);
        var warning = Assert.Single(restored.Diagnostics);
        Assert.Equal("manifest.resource.unused", warning.Code);
        Assert.Equal(ManifestDiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("/spec/resources/db", warning.Path);
        Assert.Equal("Unused resource", warning.Message);
        Assert.Equal("db", warning.Parameters["resource"]);
        Assert.Equal("/spec/components/worker", Assert.Single(warning.RelatedPaths));
        Assert.Equal(new ManifestSourceLocation(3, 5), warning.Location);
    }
}
