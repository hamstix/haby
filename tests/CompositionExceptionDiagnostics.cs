using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Haby.TestDiagnostics;

// Linked only into the verification executable and its tests. Never serializes an Exception.
internal enum CompositionDiagnosticStage { Inactive, InitialProtectedImportBaselineCheckPlan, AdditiveCheckPlan, DiagnosticSentinel }
internal sealed record CompositionDiagnosticIdentity(string Source, string SampleSha256, string PersistenceSha256,
    string ApplicationSha256, string TestSource, string TestSha256);
internal sealed record CompositionExceptionRecord(CompositionDiagnosticIdentity Identity, CompositionDiagnosticStage Stage, string Type, string[] Frames);

internal sealed class CompositionExceptionDiagnostics(CompositionDiagnosticIdentity identity,
    Func<CompositionDiagnosticStage> stage, Action<string> write) : ILoggerProvider
{
    internal const int MaxRecords = 16;
    internal const int MaxFrames = 24;
    internal const int MaxName = 256;
    private readonly object _gate = new();
    private int _records;
    private readonly CompositionDiagnosticIdentity _identity = new(Hex(identity.Source, 40), Hex(identity.SampleSha256, 64),
        Hex(identity.PersistenceSha256, 64), Hex(identity.ApplicationSha256, 64), Hex(identity.TestSource, 40), Hex(identity.TestSha256, 64));

    internal static CompositionExceptionDiagnostics? FromEnvironment()
    {
        var stagePath = Environment.GetEnvironmentVariable("HABY_TEST_COMPOSITION_STAGE");
        var outputPath = Environment.GetEnvironmentVariable("HABY_TEST_COMPOSITION_RECORDS");
        var identityPath = Environment.GetEnvironmentVariable("HABY_TEST_COMPOSITION_IDENTITY");
        return stagePath is null || outputPath is null || identityPath is null ? null : FromFiles(stagePath, outputPath, identityPath);
    }
    internal static CompositionExceptionDiagnostics? FromFiles(string stagePath, string outputPath, string identityPath)
    {
        try
        {
            if (new FileInfo(identityPath).Length > 4096) return null;
            var value = JsonSerializer.Deserialize(File.ReadAllText(identityPath), CompositionDiagnosticJson.Default.CompositionDiagnosticIdentity);
            return value is null ? null : new(value, () => new FileInfo(stagePath).Length <= 64 && Enum.TryParse<CompositionDiagnosticStage>(File.ReadAllText(stagePath), out var current) ? current : CompositionDiagnosticStage.Inactive,
                line => File.AppendAllText(outputPath, line + Environment.NewLine));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal void Record(Exception exception)
    {
        lock (_gate)
        {
            try
            {
                var current = stage();
                if (current is not (CompositionDiagnosticStage.InitialProtectedImportBaselineCheckPlan or CompositionDiagnosticStage.AdditiveCheckPlan or CompositionDiagnosticStage.DiagnosticSentinel)) return;
                for (var error = exception; error is not null && _records < MaxRecords; error = error.InnerException)
                {
                    // Stack metadata only: no file names, arguments, messages, state or Exception.ToString().
                    var frames = (new StackTrace(error, false).GetFrames() ?? []).Take(MaxFrames).Select(frame =>
                    {
                        var method = frame.GetMethod();
                        return Name(method?.DeclaringType?.FullName + "." + method?.Name);
                    }).ToArray();
                    var record = new CompositionExceptionRecord(_identity, current, Name(error.GetType().FullName), frames);
                    write(JsonSerializer.Serialize(record, CompositionDiagnosticJson.Default.CompositionExceptionRecord));
                    _records++;
                }
            }
            // Diagnostic I/O cannot replace the handler's primary failure or its cleanup.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
    private static string Hex(string? value, int length) => value?.Length == length && value.All(char.IsAsciiHexDigit) ? value.ToLowerInvariant() : "unknown";
    private static string Name(string? value) => new((value ?? "unknown").Take(MaxName)
        .Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '`' or '<' or '>' ? c : '_').ToArray());
    public ILogger CreateLogger(string categoryName) => new RecorderLogger(this);
    public void Dispose() { }
    private sealed class RecorderLogger(CompositionExceptionDiagnostics owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (exception is not null) owner.Record(exception); }
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(CompositionDiagnosticIdentity))]
[JsonSerializable(typeof(CompositionExceptionRecord))]
internal partial class CompositionDiagnosticJson : JsonSerializerContext;
