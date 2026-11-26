using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Haby.TestDiagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class HostFailureDiagnosticsTests
{
    private sealed class DiagnosticSecretException : Exception
    {
        public override string Message => throw new InvalidOperationException("SECRET-MESSAGE-MUST-NOT-BE-READ");
        public override string ToString() => throw new InvalidOperationException("SECRET-EXCEPTION-MUST-NOT-BE-FORMATTED");
    }
    [Fact]
    public async Task Composition_exception_sentinel_captures_only_bounded_stage_type_frames_and_preserves_primary_failure()
    {
        const string secret = "SECRET-MATERIAL /private/credential C:\\private\\subject";
        var retainedDirectory = Environment.GetEnvironmentVariable("HABY_TEST_TIMING_DIRECTORY");
        var retainedName = "composition-" + Guid.NewGuid().ToString("N");
        var retainedIdentity = retainedDirectory is null ? null : Path.Combine(retainedDirectory, retainedName + ".identity.json");
        var retainedExceptions = retainedDirectory is null ? null : Path.Combine(retainedDirectory, retainedName + ".exceptions.jsonl");
        var identity = new CompositionDiagnosticIdentity(new('a', 40), new('b', 64), new('c', 64), new('d', 64), new('e', 40), new('f', 64));
        var lines = new List<string>();
        var stage = CompositionDiagnosticStage.DiagnosticSentinel;
        using var diagnostic = new CompositionExceptionDiagnostics(identity, () => stage, lines.Add);
        Exception primary;
        try { throw new DiagnosticSecretException(); }
        catch (DiagnosticSecretException error) { primary = error; }
        var logger = diagnostic.CreateLogger(secret);
        logger.Log(LogLevel.Error, new(1), secret, primary, (_, _) => throw new InvalidOperationException(secret));
        var captured = JsonSerializer.Deserialize(lines.Single(), CompositionDiagnosticJson.Default.CompositionExceptionRecord)!;
        Assert.Equal(identity, captured.Identity);
        Assert.Equal(CompositionDiagnosticStage.DiagnosticSentinel, captured.Stage);
        Assert.EndsWith(nameof(DiagnosticSecretException), captured.Type);
        Assert.NotEmpty(captured.Frames);
        Assert.All(captured.Frames, frame => { Assert.True(frame.Length <= CompositionExceptionDiagnostics.MaxName); Assert.DoesNotContain('/', frame); Assert.DoesNotContain('\\', frame); Assert.DoesNotContain(':', frame); });
        Assert.DoesNotContain(secret, lines.Single());
        Assert.DoesNotContain("SECRET-", lines.Single());
        var root = Path.Combine(Path.GetTempPath(), "haby-composition-sentinel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var identityPath = Path.Combine(root, "identity.json"); var stagePath = Path.Combine(root, "stage"); var output = Path.Combine(root, "records.jsonl");
            File.WriteAllText(identityPath, JsonSerializer.Serialize(identity, CompositionDiagnosticJson.Default.CompositionDiagnosticIdentity));
            File.WriteAllText(stagePath, nameof(CompositionDiagnosticStage.DiagnosticSentinel));
            using var recorder = CompositionExceptionDiagnostics.FromFiles(stagePath, output, identityPath);
            Assert.NotNull(recorder);
            recorder.CreateLogger(secret).Log(LogLevel.Error, new(1), secret, primary, (_, _) => throw new InvalidOperationException(secret));
            var recorded = File.ReadAllText(output);
            Assert.Equal(CompositionDiagnosticStage.DiagnosticSentinel, JsonSerializer.Deserialize(recorded, CompositionDiagnosticJson.Default.CompositionExceptionRecord)!.Stage);
            Assert.DoesNotContain("SECRET-", recorded); Assert.DoesNotContain(secret, recorded);
            if (retainedDirectory is not null)
            {
                var binary = Environment.GetEnvironmentVariable("HABY_COMPOSITION_SAMPLE_PATH");
                Assert.True(Path.IsPathFullyQualified(binary!) && File.Exists(binary), "Published composition bytes are required for retained sentinel identity.");
                var directory = Path.GetDirectoryName(binary)!;
                var testAssembly = typeof(HostFailureDiagnosticsTests).Assembly.Location;
                string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
                string Source(string file)
                {
                    var value = FileVersionInfo.GetVersionInfo(file).ProductVersion?.Split('+').Last();
                    return value?.Length == 40 && value.All(char.IsAsciiHexDigit) ? value.ToLowerInvariant() : "unknown";
                }
                var actualIdentity = new CompositionDiagnosticIdentity(Source(binary!), Hash(binary!), Hash(Path.Combine(directory, "Haby.Persistence.PostgreSql.dll")),
                    Hash(Path.Combine(directory, "Haby.Application.dll")), Source(testAssembly), Hash(testAssembly));
                Assert.Matches("^[a-f0-9]{40}$", actualIdentity.Source);
                Assert.Equal(actualIdentity.Source, actualIdentity.TestSource);
                Directory.CreateDirectory(retainedDirectory);
                File.WriteAllText(retainedIdentity!, JsonSerializer.Serialize(actualIdentity, CompositionDiagnosticJson.Default.CompositionDiagnosticIdentity));
                using var retainedRecorder = new CompositionExceptionDiagnostics(actualIdentity, () => CompositionDiagnosticStage.DiagnosticSentinel,
                    line => File.AppendAllText(retainedExceptions!, line + Environment.NewLine));
                retainedRecorder.CreateLogger(secret).Log(LogLevel.Error, new(1), secret, primary, (_, _) => throw new InvalidOperationException(secret));
                Assert.True(File.Exists(retainedIdentity), "Explicit sentinel identity was not retained in the timing directory.");
                Assert.True(File.Exists(retainedExceptions), "Explicit sanitized sentinel exception was not retained in the timing directory.");
                var savedIdentity = JsonSerializer.Deserialize(File.ReadAllText(retainedIdentity!), CompositionDiagnosticJson.Default.CompositionDiagnosticIdentity)!;
                var savedText = Assert.Single(File.ReadAllLines(retainedExceptions!));
                var savedRecord = JsonSerializer.Deserialize(savedText, CompositionDiagnosticJson.Default.CompositionExceptionRecord)!;
                Assert.Equal(actualIdentity, savedIdentity); Assert.Equal(savedIdentity, savedRecord.Identity);
                Assert.Equal(CompositionDiagnosticStage.DiagnosticSentinel, savedRecord.Stage);
                Assert.DoesNotContain("SECRET-", savedText); Assert.DoesNotContain(secret, savedText);
            }
        }
        finally { Directory.Delete(root, true); }
        stage = (CompositionDiagnosticStage)999;
        diagnostic.Record(primary); Assert.Single(lines);
        stage = CompositionDiagnosticStage.InitialMixedMaterialization;
        diagnostic.Record(primary);
        Assert.Equal(CompositionDiagnosticStage.InitialMixedMaterialization,
            JsonSerializer.Deserialize(lines.Last(), CompositionDiagnosticJson.Default.CompositionExceptionRecord)!.Stage);
        stage = CompositionDiagnosticStage.InitialProtectedImportBaselineCheckPlan;
        for (var i = 0; i < 100; i++) diagnostic.Record(primary);
        Assert.Equal(CompositionExceptionDiagnostics.MaxRecords, lines.Count);
        Assert.All(lines, line => Assert.True(line.Length < 10000));
        var invalid = new CompositionDiagnosticIdentity(secret, secret, secret, secret, secret, secret);
        var badIdentity = new List<string>();
        using (var recorder = new CompositionExceptionDiagnostics(invalid, () => stage, badIdentity.Add)) recorder.Record(primary);
        Assert.DoesNotContain(secret, badIdentity.Single()); Assert.Contains("unknown", badIdentity.Single());
        using var unavailable = new CompositionExceptionDiagnostics(identity, () => stage, _ => throw new IOException(secret));
        var cleanup = false;
        var actual = await Record.ExceptionAsync(() => HostFailureDiagnostics.RunAsync(defer =>
        {
            defer(() => { cleanup = true; return ValueTask.CompletedTask; });
            throw primary;
        }, () => HostFailurePhase.Catalog, () => null, (_, error, _) => unavailable.Record(error)));
        Assert.Same(primary, actual); Assert.True(cleanup);
    }
}
