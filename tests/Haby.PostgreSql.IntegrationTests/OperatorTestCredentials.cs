using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Haby.PostgreSql.IntegrationTests;

// Explicit fixture setup through the production issuance port, never an anonymous Host switch.
internal static class OperatorTestCredentials
{
    private static readonly ConcurrentDictionary<string, string> Credentials = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<Guid, string> Retained = new();
    internal static async Task StartWithIssuedOperatorAsync(WebApplication host)
    {
        var store = host.Services.GetRequiredService<IOperatorAccessStore>();
        // Retain the same issued credential across restart/restore, including actor-bound retries.
        string? credential = null;
        foreach (var grant in await store.ListAsync(null, 100, default))
            if (Retained.TryGetValue(grant.Id, out var retained) && await store.AuthorizeAsync(retained, default) is not null)
            { credential = retained; break; }
        if (credential is null)
        {
            var created = await store.CreateAsync("integration fixture", LocalOperatorIssuance.LocalActor, null, default);
            Retained[created.Grant.Id] = created.Credential; credential = created.Credential;
        }
        await host.StartAsync();
        Credentials[Key(host.Urls.Single())] = credential;
    }
    internal static Task WriteConfigAsync(string path, string endpoint) => File.WriteAllTextAsync(path,
        $"formatVersion: 1\ncurrentContext: selected\ninstances:\n  local:\n    endpoint: {endpoint}\n    token: {Credential(endpoint)}\ncontexts:\n  selected:\n    instance: local\n    environmentId: pilot\n");
    private static string Key(string endpoint) => new Uri(endpoint.Contains("://", StringComparison.Ordinal) ? endpoint : "http://" + endpoint).Authority;
    internal static string Credential(string endpoint) => Credentials[Key(endpoint)];
    internal static Metadata Headers(string endpoint) => new() { { "authorization", "Bearer " + Credential(endpoint) } };
    internal static ManifestOperator.ManifestOperatorClient Client(GrpcChannel channel) => new(channel.Intercept(new Attach(Credential(channel.Target))));
    private sealed class Attach(string credential) : Interceptor
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context,
            AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        {
            var headers = context.Options.Headers ?? new Metadata(); headers.Add("authorization", "Bearer " + credential);
            return continuation(request, new(context.Method, context.Host, context.Options.WithHeaders(headers)));
        }
    }
}
