using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Grpc.Net.ClientFactory;
using Hamstix.Haby.Client.Services;
using Hamstix.Haby.Client.Auth;
using Hamstix.Haby.Shared.Grpc.Configuration;
using Hamstix.Haby.Shared.Grpc.ConfigurationUnits;
using Hamstix.Haby.Shared.Grpc.Generators;
using Hamstix.Haby.Shared.Grpc.OrganizationUnits;
using Hamstix.Haby.Shared.Grpc.Plugins;
using Hamstix.Haby.Shared.Grpc.Services;
using Hamstix.Haby.Shared.Grpc.System;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net;

namespace Hamstix.Haby.Client.Extensions;

public static class ServiceCollectionExtensions
{
    const string GrpcHttpClientName = "Grpc";

    public static IServiceCollection AddHabyClientServices(
        this IServiceCollection services,
        Func<IServiceProvider, Uri> baseUriFactory)
    {
        services.AddTransient<GrpcWebHandler>();
        services.AddHttpClient(GrpcHttpClientName)
            .ConfigureHttpClient((provider, client) => client.BaseAddress = baseUriFactory(provider))
            .AddHttpMessageHandler<GrpcWebHandler>();
        services.AddScoped(provider => provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(GrpcHttpClientName));

        services.AddScoped<Components.Toast.ToastService>();
        services.AddScoped<ILocalStorage, LocalStorage>();
        services.AddAuthorizationCore();
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, CookieAuthenticationStateProvider>();
        services.AddScoped<IAuthenticationService, AuthenticationService>();

        services.AddGrpcPreConfiguredClient<SystemService.SystemServiceClient>(baseUriFactory);
        services.AddGrpcPreConfiguredClient<SystemStatusService.SystemStatusServiceClient>(baseUriFactory);
        services.AddGrpcPreConfiguredClient<PluginsService.PluginsServiceClient>(baseUriFactory);
        services.AddGrpcPreConfiguredClient<ServicesService.ServicesServiceClient>(baseUriFactory);
        services.AddGrpcPreConfiguredClient<GeneratorsService.GeneratorsServiceClient>(baseUriFactory);
        services.AddGrpcPreConfiguredClient<ConfigurationUnitsService.ConfigurationUnitsServiceClient>(baseUriFactory);
        services.AddGrpcPreConfiguredClient<OrganizationUnitsService.OrganizationUnitsServiceClient>(baseUriFactory);
        services.AddGrpcPreConfiguredClient<ConfigurationService.ConfigurationServiceClient>(baseUriFactory);
        return services;
    }

    /// <summary>
    /// Add preconfigred Grpc service with configured Address and CallCredentials.
    /// </summary>
    /// <typeparam name="TClient"></typeparam>
    /// <param name="services"></param>
    /// <param name="baseUri"></param>
    /// <returns></returns>
    public static IHttpClientBuilder AddGrpcPreConfiguredClient<TClient>(
        this IServiceCollection services,
        Func<IServiceProvider, Uri> baseUriFactory) where TClient : class
    {
        return services.AddGrpcClient<TClient>(delegate (IServiceProvider provider, GrpcClientFactoryOptions options)
        {
            options.Address = baseUriFactory(provider);
        }).ConfigureChannel((IServiceProvider p, GrpcChannelOptions o) =>
        {
            o.HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, new HttpClientHandler());
            o.UnsafeUseInsecureChannelCallCredentials = true;
            //var httpClient = p.GetRequiredService<HttpClient>();
            //o.HttpClient = httpClient;
        });
    }
}
