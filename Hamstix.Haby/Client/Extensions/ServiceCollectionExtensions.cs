using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Grpc.Net.ClientFactory;
using Hamstix.Haby.Client.Services;
using System.Net;

namespace Hamstix.Haby.Client.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add preconfigred Grpc service with configured Address and CallCredentials.
    /// </summary>
    /// <typeparam name="TClient"></typeparam>
    /// <param name="services"></param>
    /// <param name="baseUri"></param>
    /// <returns></returns>
    public static IHttpClientBuilder AddGrpcPreConfiguredClient<TClient>(this IServiceCollection services, Uri baseUri) where TClient : class
    {
        return services.AddGrpcClient<TClient>(delegate (GrpcClientFactoryOptions o)
        {
            o.Address = baseUri;
        }).ConfigureChannel((IServiceProvider p, GrpcChannelOptions o) =>
        {
            o.HttpHandler = new GrpcWebHandler(new HttpClientHandler());
            o.UnsafeUseInsecureChannelCallCredentials = true;
            //var httpClient = p.GetRequiredService<HttpClient>();
            //o.HttpClient = httpClient;
        })
        .AddCallCredentials(async delegate (AuthInterceptorContext context, Metadata metadata, IServiceProvider provider)
        {
            var localStorage = provider.GetRequiredService<ILocalStorage>();

            var token = await localStorage.GetStringAsync("token");

            if (!string.IsNullOrWhiteSpace(token))
            {
                metadata.Add(HttpRequestHeader.Authorization.ToString(), $"Bearer {token}");
            }
        });
    }
}
