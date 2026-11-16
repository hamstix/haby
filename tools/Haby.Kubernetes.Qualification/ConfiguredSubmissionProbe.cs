using System.Net.Http;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Authentication;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Haby.Operator.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Fixture-only, exact first call. Never inspect exception messages, log state, headers or claim values.
internal sealed class ConfiguredSubmissionProbe : ILoggerProvider, ILogger, IStartupFilter
{
    private const string Method = "/haby.operator.v1alpha1.ManifestOperator/SubmitGeneration";
    private const string OriginalActivator = "configured-original-activator";
    private int active, started, reached, intercepted, actor, activation;
    private string serverCategory = "not-observed", clientCategory = "not-observed";
    private bool auditEnabled;
    private readonly AsyncLocal<bool> activationScope = new();
    internal bool Started => Volatile.Read(ref started) != 0;
    internal void Register(IServiceCollection services)
    {
        services.AddSingleton<ILoggerProvider>(this);
        services.AddSingleton<IStartupFilter>(this);
        services.AddSingleton(new CallInterceptor(this));
        services.Configure<GrpcServiceOptions<ManifestOperatorService>>(options => options.Interceptors.Add<CallInterceptor>());
        // Let the built-in container close the unchanged framework activator; delegate its exact handle and release.
        var original = services.Single(x => x.ServiceType == typeof(IGrpcServiceActivator<>));
        if (original.Lifetime != ServiceLifetime.Singleton || original.ImplementationType is null)
            throw new InvalidOperationException("Unexpected qualification activator registration.");
        services.AddKeyedSingleton(typeof(IGrpcServiceActivator<>), OriginalActivator, original.ImplementationType);
        services.AddSingleton<IGrpcServiceActivator<ManifestOperatorService>>(p => new ServiceActivator(
            p.GetRequiredKeyedService<IGrpcServiceActivator<ManifestOperatorService>>(OriginalActivator), this));
    }
    internal void Begin(IOptionsMonitor<LoggerFilterOptions> filters)
    {
        started = reached = intercepted = actor = activation = 0;
        serverCategory = clientCategory = "not-observed";
        // Evaluate the actual rules with only this provider, without changing any Host filters or emitting a log.
        using var filterCheck = new LoggerFactory([this], filters);
        auditEnabled = filterCheck.CreateLogger("Haby.Operator.Host.ManifestOperatorService").IsEnabled(LogLevel.Information);
        Volatile.Write(ref active, 1);
    }
    internal void End() => Volatile.Write(ref active, 0);
    private bool Active => Volatile.Read(ref active) != 0;
    internal void ClientFailure(Exception error) => clientCategory = Classify(error);
    internal void Write(TextWriter output)
    {
        output.WriteLine("Diagnostic configured first-submit-host-reached: " + (reached != 0 ? "true" : "false"));
        output.WriteLine("Diagnostic configured first-submit-interceptor-entered: " + (intercepted != 0 ? "true" : "false"));
        output.WriteLine("Diagnostic configured first-submit-actor-present: " + (actor != 0 ? "true" : "false"));
        output.WriteLine("Diagnostic configured first-submit-audit-provider-enabled: " + (auditEnabled ? "true" : "false"));
        output.WriteLine("Diagnostic configured first-submit-activation: " + (activation switch { 1 => "started", 2 => "completed", 3 => "failed", _ => "not-observed" }));
        output.WriteLine("Diagnostic configured first-submit-server-category: " + serverCategory);
        output.WriteLine("Diagnostic configured first-submit-client-category: " + clientCategory);
    }
    internal static string Classify(Exception error)
    {
        for (var depth = 0; depth < 8; depth++)
        {
            if (error is AuthenticationException) return "tls";
            if (error is SocketException) return "connect";
            if (error is HttpProtocolException) return "http2";
            if (error is HttpRequestException http)
            {
                var category = http.HttpRequestError switch
                {
                    HttpRequestError.SecureConnectionError => "tls",
                    HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError => "connect",
                    HttpRequestError.HttpProtocolError => "http2",
                    HttpRequestError.UserAuthenticationError => "authentication",
                    _ => "http"
                };
                return category;
            }
            if (error is RpcException rpc && rpc.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
                return "authentication";
            var inner = error is RpcException call ? call.Status.DebugException ?? call.InnerException : error.InnerException;
            if (inner is null || ReferenceEquals(inner, error)) break;
            error = inner;
        }
        return "other";
    }
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) =>
        {
            var exact = Active && string.Equals(context.Request.Path.Value, Method, StringComparison.Ordinal);
            if (exact) Interlocked.Exchange(ref reached, 1);
            try { await continuation(context); }
            catch (Exception error) { if (exact) serverCategory = Classify(error); throw; }
            finally
            {
                if (exact && context.Response.StatusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
                    serverCategory = "authentication";
            }
        });
        next(app);
    };
    internal async Task<TResponse> ObserveServerAsync<TResponse>(HttpContext context, Func<Task<TResponse>> continuation)
    {
        Interlocked.Exchange(ref intercepted, 1);
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true && user.HasClaim(x => x.Type == ClaimTypes.NameIdentifier))
            Interlocked.Exchange(ref actor, 1);
        var previous = activationScope.Value;
        activationScope.Value = true;
        try { return await continuation(); }
        catch (Exception error) { if (activation != 3) serverCategory = Classify(error); throw; }
        finally { activationScope.Value = previous; }
    }
    internal sealed class CallInterceptor(ConfiguredSubmissionProbe probe) : Interceptor
    {
        public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context,
            UnaryServerMethod<TRequest, TResponse> continuation) => probe.Active && context.Method == Method
                ? probe.ObserveServerAsync(context.GetHttpContext(), () => continuation(request, context))
                : continuation(request, context);
    }
    internal sealed class ServiceActivator(IGrpcServiceActivator<ManifestOperatorService> original, ConfiguredSubmissionProbe probe)
        : IGrpcServiceActivator<ManifestOperatorService>
    {
        public GrpcActivatorHandle<ManifestOperatorService> Create(IServiceProvider serviceProvider)
        {
            var exact = probe.Active && probe.activationScope.Value;
            if (exact) Interlocked.Exchange(ref probe.activation, 1);
            try
            {
                var handle = original.Create(serviceProvider);
                if (exact) Interlocked.Exchange(ref probe.activation, 2);
                return handle;
            }
            catch (Exception error)
            {
                if (exact)
                {
                    Interlocked.Exchange(ref probe.activation, 3);
                    probe.serverCategory = error is InvalidOperationException or ArgumentException or TypeLoadException or MissingMethodException
                        ? "activation-dependency-resolution" : Classify(error);
                }
                throw;
            }
        }
        public ValueTask ReleaseAsync(GrpcActivatorHandle<ManifestOperatorService> service) => original.ReleaseAsync(service);
    }
    public ILogger CreateLogger(string categoryName) => categoryName == "Haby.Operator.Host.ManifestOperatorService"
        ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (eventId.Id == 2290 && Active) Interlocked.Exchange(ref started, 1);
    }
    public void Dispose() { }
}
