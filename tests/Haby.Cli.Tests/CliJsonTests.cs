using System.Text.Json;
using Haby.Operator.Contracts;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class CliJsonTests
{
    private static JsonElement Check(Action<CliJsonPresenter> action, string command, string outcome)
    {
        using var output = new StringWriter();
        action(new CliJsonPresenter(output));
        var text = output.ToString();
        Assert.EndsWith(Environment.NewLine, text);
        Assert.Equal(1, text.Count(character => character == '\n'));
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.Equal(5, root.EnumerateObject().Count());
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal(command, root.GetProperty("command").GetString());
        Assert.Equal(outcome, root.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("diagnostics").ValueKind);
        return root.Clone();
    }

    private static DiagnosticReport Report() => new()
    {
        Truncated = true,
        Diagnostics =
        {
            new Diagnostic { Code = "bad", Path = "/spec", Message = "Invalid declaration",
                Severity = "Error", RelatedPaths = { "/metadata" },
                Location = new SourceLocation { Line = 3, Column = 2 },
                Parameters = { ["secretValue"] = "sensitive-sentinel" } }
        }
    };

    private static RevisionSummary Revision() => new()
    {
        Reference = new RevisionReference { ApplicationRecordId = "record", RevisionNumber = 7 },
        RevisionId = "revision",
        ApplicationId = "app",
        ApplicationVersion = "v1",
        InputApiVersion = "haby.dev/v1alpha1",
        NormalizedModelVersion = 1,
        TechnicalActorId = "actor",
        ImportedAtUtc = "2026-10-28T00:00:00Z",
        HistoricalWarnings = Report()
    };

    [Fact]
    public void Validate_covers_success_and_both_rejections_without_parameters()
    {
        var valid = Check(p => Assert.Equal(0, p.Print(new ValidateReply
        {
            Valid = new ValidManifest
            { ApplicationId = "app", ApplicationVersion = "v1", Warnings = Report() }
        })),
            "manifest validate", "valid");
        Assert.Equal("app", valid.GetProperty("data").GetProperty("applicationId").GetString());
        Assert.True(valid.GetProperty("data").GetProperty("diagnosticsTruncated").GetBoolean());
        Assert.Equal(3, valid.GetProperty("diagnostics")[0].GetProperty("location").GetProperty("line").GetInt32());
        Assert.DoesNotContain("sensitive-sentinel", valid.GetRawText());
        foreach (var (outcome, reply) in new[]
        {
            ("invalidRequest", new ValidateReply { InvalidRequest = new InvalidRequest { Report = Report() } }),
            ("invalidManifest", new ValidateReply { InvalidManifest = new InvalidManifest { Report = Report() } })
        })
        {
            var root = Check(p => Assert.Equal(1, p.Print(reply)), "manifest validate", outcome);
            Assert.True(root.GetProperty("data").GetProperty("diagnosticsTruncated").GetBoolean());
            Assert.DoesNotContain("sensitive-sentinel", root.GetRawText());
        }
    }

    [Fact]
    public void Import_covers_both_acceptances_and_all_rejections()
    {
        foreach (var (outcome, reply) in new[]
        {
            ("created", new ImportReply { Created = new ImportAccepted { Revision = Revision() } }),
            ("existingEquivalent", new ImportReply { ExistingEquivalent = new ImportAccepted { Revision = Revision() } })
        })
        {
            var root = Check(p => Assert.Equal(0, p.Print(reply)), "manifest import", outcome);
            Assert.Equal(7, root.GetProperty("data").GetProperty("revision")
                .GetProperty("reference").GetProperty("revisionNumber").GetInt64());
            Assert.True(root.GetProperty("data").GetProperty("revision")
                .GetProperty("historicalWarningsTruncated").GetBoolean());
            Assert.DoesNotContain("sensitive-sentinel", root.GetRawText());
        }
        foreach (var (outcome, reply) in new[]
        {
            ("invalidRequest", new ImportReply { InvalidRequest = new InvalidRequest { Report = Report() } }),
            ("invalidManifest", new ImportReply { InvalidManifest = new InvalidManifest { Report = Report() } })
        })
        {
            var root = Check(p => Assert.Equal(1, p.Print(reply)), "manifest import", outcome);
            Assert.True(root.GetProperty("data").GetProperty("diagnosticsTruncated").GetBoolean());
            Assert.DoesNotContain("sensitive-sentinel", root.GetRawText());
        }
        var conflict = Check(p => Assert.Equal(1, p.Print(new ImportReply
        {
            VersionContentConflict = new VersionContentConflict
            {
                ApplicationId = "app",
                ApplicationVersion = "v1",
                ExistingRevision = Revision().Reference,
                ChangedPaths = { "/spec" },
                ChangedPathsTruncated = true
            }
        })), "manifest import", "versionContentConflict");
        Assert.Equal("/spec", conflict.GetProperty("data").GetProperty("changedPaths")[0].GetString());
        Assert.True(conflict.GetProperty("data").GetProperty("changedPathsTruncated").GetBoolean());
    }

    [Fact]
    public void Get_covers_found_and_not_found()
    {
        var found = Check(p => Assert.Equal(0, p.Print(new GetRevisionReply { Found = Revision() })),
            "manifest revision get", "found");
        Assert.Equal("revision", found.GetProperty("data").GetProperty("revision").GetProperty("revisionId").GetString());
        var missing = Check(p => Assert.Equal(1, p.Print(new GetRevisionReply { NotFound = Revision().Reference })),
            "manifest revision get", "notFound");
        Assert.Equal("record", missing.GetProperty("data").GetProperty("requestedReference")
            .GetProperty("applicationRecordId").GetString());
    }

    [Fact]
    public void Preview_covers_hypothetical_success_and_every_rejection()
    {
        var success = Check(p => Assert.Equal(0, p.Print(new PreviewDatabaseReply
        {
            WouldCreate = new PreviewWouldCreate
            {
                EnvironmentId = "dev",
                ResourceId = "db",
                DatabaseName = "example",
                ProviderAlias = "primary",
                InstanceId = "local",
                ConfigurationRevision = 2,
                ImplementationId = "postgres"
            }
        })), "manifest revision preview", "wouldCreate");
        Assert.True(success.GetProperty("data").GetProperty("hypothetical").GetBoolean());
        Assert.False(success.GetProperty("data").GetProperty("canApply").GetBoolean());
        Assert.Equal("example", success.GetProperty("data").GetProperty("databaseName").GetString());
        foreach (var (outcome, reply) in new[]
        {
            ("revisionNotFound", new PreviewDatabaseReply { RevisionNotFound = true }),
            ("invalidContext", new PreviewDatabaseReply { InvalidContext = true }),
            ("aliasNotResolved", new PreviewDatabaseReply { AliasNotResolved = new PreviewAliasFailure { Alias = "primary" } }),
            ("capabilityMismatch", new PreviewDatabaseReply { CapabilityMismatch = new PreviewAliasFailure { Alias = "primary" } }),
            ("unsupportedShape", new PreviewDatabaseReply { UnsupportedShape = true })
        })
        {
            var root = Check(p => Assert.Equal(1, p.Print(reply)), "manifest revision preview", outcome);
            Assert.False(root.GetProperty("data").GetProperty("canApply").GetBoolean());
        }
    }

    [Fact]
    public void Configuration_is_typed_sorted_and_has_no_yaml_or_extra_fields()
    {
        var config = new CliConfiguration("z", new Dictionary<string, CliInstance>
        {
            ["z"] = new("http://localhost:2"),
            ["a"] = new("http://localhost:1")
        }, new Dictionary<string, CliNamedContext>
        {
            ["z"] = new("z", "dev-z"),
            ["a"] = new("a", "dev-a")
        });
        var view = Check(p => p.PrintConfiguration(config, "a"), "config view", "ok");
        Assert.Equal(1, view.GetProperty("data").GetProperty("formatVersion").GetInt32());
        Assert.Equal("a", view.GetProperty("data").GetProperty("instances")[0].GetProperty("name").GetString());
        Assert.Equal("a", view.GetProperty("data").GetProperty("contexts")[0].GetProperty("name").GetString());
        Assert.Equal(4, view.GetProperty("data").EnumerateObject().Count());
        var current = Check(p => p.PrintCurrentContext(null), "config current-context", "ok");
        Assert.Equal(JsonValueKind.Object, current.GetProperty("data").ValueKind);
        Assert.Equal(JsonValueKind.Null, current.GetProperty("data").GetProperty("currentContext").ValueKind);
    }

    [Fact]
    public void Envelope_keeps_data_property_when_payload_is_null()
    {
        var json = JsonSerializer.Serialize(
            new CliEnvelope<CliCurrentContextData>(1, "config current-context", "ok", null, []),
            CliOutputJsonContext.Default.CliEnvelopeCliCurrentContextData);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("data").ValueKind);
        Assert.Equal(5, document.RootElement.EnumerateObject().Count());
    }
}
