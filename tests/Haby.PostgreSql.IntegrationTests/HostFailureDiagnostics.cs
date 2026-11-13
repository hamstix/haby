using System.Runtime.ExceptionServices;
using Grpc.Core;

namespace Haby.PostgreSql.IntegrationTests;

internal enum HostFailurePhase
{
    Build, Start, AddressScheme, AddressLoopback, Channel, ReflectionWrite, ReflectionRead, StatusAssertion, Cleanup
}

// Structured test instrumentation, not parsing or copying exception messages/stacks.
internal static class HostFailureDiagnostics
{
    internal static async Task RunAsync(Func<Action<Func<ValueTask>>, Task> body,
        Func<HostFailurePhase> phase, Func<StatusCode?> observed,
        Action<HostFailurePhase, Exception, StatusCode?>? record = null)
    {
        record ??= Record;
        var cleanup = new List<Func<ValueTask>>();
        ExceptionDispatchInfo? primary = null;
        try { await body(cleanup.Add); }
        catch (Exception error)
        {
            primary = ExceptionDispatchInfo.Capture(error);
            // Capture body evidence before any resource can replace its exception.
            record(phase(), error, observed());
        }
        ExceptionDispatchInfo? firstCleanup = null;
        for (var i = cleanup.Count - 1; i >= 0; i--)
        {
            try { await cleanup[i](); }
            catch (Exception error)
            {
                firstCleanup ??= ExceptionDispatchInfo.Capture(error);
                record(HostFailurePhase.Cleanup, error, null);
            }
        }
        primary?.Throw();
        firstCleanup?.Throw();
    }

    private static readonly object Gate = new();
    internal const string Method = "Haby.PostgreSql.IntegrationTests.OperatorHostConfigurationTests.Disabled_tls_serves_grpc_on_loopback_without_certificate_settings";

    internal static string Render(HostFailurePhase phase, Exception error, StatusCode? observed)
    {
        var stage = phase switch
        {
            HostFailurePhase.Build => "host-build",
            HostFailurePhase.Start => "host-start",
            HostFailurePhase.AddressScheme => "address-scheme",
            HostFailurePhase.AddressLoopback => "address-loopback",
            HostFailurePhase.Channel => "channel",
            HostFailurePhase.ReflectionWrite => "reflection-write",
            HostFailurePhase.ReflectionRead => "reflection-read",
            HostFailurePhase.StatusAssertion => "status-assertion",
            HostFailurePhase.Cleanup => "cleanup",
            _ => "unknown"
        };
        var status = ((error as RpcException)?.StatusCode ?? observed) switch
        {
            StatusCode.Unauthenticated => "unauthenticated",
            StatusCode.Internal => "internal",
            StatusCode.Unavailable => "unavailable",
            StatusCode.DeadlineExceeded => "deadline-exceeded",
            null => "unavailable-evidence",
            _ => "other"
        };
        return stage + "," + status;
    }

    internal static void Record(HostFailurePhase phase, Exception error, StatusCode? observed)
    {
        var directory = Environment.GetEnvironmentVariable("HABY_TEST_TIMING_DIRECTORY");
        if (directory is null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "host-diagnostics.csv"), Method + "," + Render(phase, error, observed) + Environment.NewLine);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Allowlisted Host diagnostic unavailable; primary failure preserved.");
        }
    }
}
