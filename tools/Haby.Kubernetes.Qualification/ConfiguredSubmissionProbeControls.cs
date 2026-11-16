using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Haby.Operator.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

internal static class ConfiguredSubmissionProbeControls
{
    private const string Sentinel = "protected-message-state-header-credential";
    private sealed class HostileException : Exception
    {
        public override string Message => throw new InvalidOperationException(Sentinel);
        public override string ToString() => throw new InvalidOperationException(Sentinel);
    }
    private sealed class HostileState
    {
        public override string ToString() => throw new InvalidOperationException(Sentinel);
    }
    private sealed class ControlledActivator(GrpcActivatorHandle<ManifestOperatorService> handle, Exception? failure = null)
        : IGrpcServiceActivator<ManifestOperatorService>
    {
        internal int Creates, Releases;
        public GrpcActivatorHandle<ManifestOperatorService> Create(IServiceProvider serviceProvider)
        { Creates++; if (failure is not null) throw failure; return handle; }
        public ValueTask ReleaseAsync(GrpcActivatorHandle<ManifestOperatorService> service) { Releases++; return ValueTask.CompletedTask; }
    }
    internal static async Task Run()
    {
        foreach (var (error, expected) in new (Exception, string)[]
        {
            (new HostileException(), "other"),
            (new AuthenticationException(Sentinel), "tls"),
            (new SocketException(), "connect"),
            (new HttpProtocolException(1, Sentinel, null), "http2"),
            (new HttpRequestException(HttpRequestError.ConnectionError, Sentinel), "connect"),
            (new HttpRequestException(HttpRequestError.SecureConnectionError, Sentinel), "tls"),
            (new HttpRequestException(HttpRequestError.HttpProtocolError, Sentinel), "http2"),
            (new HttpRequestException(HttpRequestError.UserAuthenticationError, Sentinel), "authentication"),
            (new HttpRequestException(Sentinel), "http"),
            (new RpcException(new(StatusCode.Unauthenticated, Sentinel)), "authentication"),
            (new RpcException(new(StatusCode.Internal, Sentinel, new AuthenticationException(Sentinel))), "tls")
        })
            if (ConfiguredSubmissionProbe.Classify(error) != expected) throw new InvalidOperationException("Probe category control failed.");
        using var probe = new ConfiguredSubmissionProbe();
        var logger = probe.CreateLogger("Haby.Operator.Host.ManifestOperatorService");
        void Audit() => logger.Log(LogLevel.Information, new EventId(2290), new HostileState(), new HostileException(),
            (_, _) => throw new InvalidOperationException(Sentinel));
        Audit();
        if (probe.Started) throw new InvalidOperationException("Inactive audit was observed.");
        using var filteredServices = new ServiceCollection().AddLogging(builder => builder.SetMinimumLevel(LogLevel.None)).BuildServiceProvider();
        probe.Begin(filteredServices.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>());
        Audit();
        probe.End();
        if (!probe.Started) throw new InvalidOperationException("Exact audit was lost.");
        var output = new StringWriter(); probe.Write(output);
        if (output.ToString().Contains(Sentinel, StringComparison.Ordinal) || !output.ToString().Contains("audit-provider-enabled: false", StringComparison.Ordinal))
            throw new InvalidOperationException("Probe output control failed.");

        using var providerFilteredServices = new ServiceCollection().AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information)
            .AddFilter<ConfiguredSubmissionProbe>("Haby.Operator.Host.ManifestOperatorService", LogLevel.None)).BuildServiceProvider();
        probe.Begin(providerFilteredServices.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>());
        probe.End(); output = new StringWriter(); probe.Write(output);
        if (!output.ToString().Contains("audit-provider-enabled: false", StringComparison.Ordinal))
            throw new InvalidOperationException("Provider-specific audit filter was ignored.");

        // Exercise the actual Host graph and unchanged framework activator without starting a listener or opening a database.
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=Host=192.0.2.1;Database=qualification;Username=fixture",
            "--Haby:Tls:Enabled=false", "--Haby:Port=0"], probe.Register);
        using var scope = host.Services.CreateScope();
        var activator = scope.ServiceProvider.GetRequiredService<IGrpcServiceActivator<ManifestOperatorService>>();
        var handle = activator.Create(scope.ServiceProvider);
        if (handle.Instance is null) throw new InvalidOperationException("Host activation control failed.");
        await activator.ReleaseAsync(handle);
        foreach (var error in new Exception[] { new InvalidOperationException(Sentinel), new HostileException() })
        {
            probe.Begin(host.Services.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>());
            var original = new ControlledActivator(handle, error);
            var observed = new ConfiguredSubmissionProbe.ServiceActivator(original, probe);
            Exception? caught = null;
            try { await probe.ObserveServerAsync(new DefaultHttpContext(), () => Task.FromResult(observed.Create(scope.ServiceProvider))); }
            catch (Exception failure) { caught = failure; }
            probe.End(); output = new StringWriter(); probe.Write(output);
            var expectedCategory = error is InvalidOperationException ? "activation-dependency-resolution" : "other";
            if (!ReferenceEquals(error, caught) || !output.ToString().Contains("activation: failed", StringComparison.Ordinal) ||
                !output.ToString().Contains("server-category: " + expectedCategory, StringComparison.Ordinal) || output.ToString().Contains(Sentinel, StringComparison.Ordinal))
                throw new InvalidOperationException("Activation failure was hidden or leaked.");
        }
        probe.Begin(host.Services.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>());
        var successful = new ControlledActivator(handle);
        var wrapped = new ConfiguredSubmissionProbe.ServiceActivator(successful, probe);
        var forwarded = await probe.ObserveServerAsync(new DefaultHttpContext(), () => Task.FromResult(wrapped.Create(scope.ServiceProvider)));
        await wrapped.ReleaseAsync(forwarded);
        probe.End();
        if (!ReferenceEquals(forwarded.Instance, handle.Instance) || !ReferenceEquals(forwarded.State, handle.State) ||
            forwarded.Created != handle.Created || successful.Creates != 1 || successful.Releases != 1)
            throw new InvalidOperationException("Activator semantics changed.");

        probe.Begin(host.Services.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>());
        var authenticated = new DefaultHttpContext();
        authenticated.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Sentinel)], "fixture"));
        var bodyFailure = new InvalidOperationException(Sentinel);
        Exception? observedBody = null;
        try
        {
            await probe.ObserveServerAsync<int>(authenticated, () =>
            {
                _ = wrapped.Create(scope.ServiceProvider);
                throw bodyFailure;
            });
        }
        catch (Exception failure) { observedBody = failure; }
        probe.End(); output = new StringWriter(); probe.Write(output);
        if (!ReferenceEquals(bodyFailure, observedBody) || !output.ToString().Contains("activation: completed", StringComparison.Ordinal) ||
            !output.ToString().Contains("server-category: other", StringComparison.Ordinal) ||
            !output.ToString().Contains("actor-present: true", StringComparison.Ordinal) || output.ToString().Contains(Sentinel, StringComparison.Ordinal))
            throw new InvalidOperationException("Handler failure was misclassified as activation or leaked.");

        // No server starts: run the actual observation middleware with a synthetic HTTP context.
        var app = new ApplicationBuilder(host.Services);
        probe.Configure(builder => builder.Run(_ => Task.CompletedTask))(app);
        var pipeline = app.Build();
        var context = new DefaultHttpContext();
        context.Request.Path = "/haby.operator.v1alpha1.ManifestOperator/SubmitGeneration";
        probe.Begin(host.Services.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>());
        await pipeline(context);
        probe.End(); output = new StringWriter(); probe.Write(output);
        if (!output.ToString().Contains("host-reached: true", StringComparison.Ordinal) || probe.Started)
            throw new InvalidOperationException("Request and audit were conflated.");
        probe.Begin(host.Services.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>());
        context.Request.Path = "/different-method";
        await pipeline(context);
        probe.End(); output = new StringWriter(); probe.Write(output);
        if (!output.ToString().Contains("host-reached: false", StringComparison.Ordinal))
            throw new InvalidOperationException("Different request was observed.");
        Console.WriteLine("PASS configured-submission-probe-controls fixed-categories; hostile-payload; exact-scope; Host-activation; request-versus-audit");
    }
}
