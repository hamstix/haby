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
        }
        finally { Directory.Delete(root, true); }
        stage = (CompositionDiagnosticStage)999;
        diagnostic.Record(primary); Assert.Single(lines);
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
