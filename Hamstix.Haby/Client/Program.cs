using Grpc.Net.Client.Web;
using Hamstix.Haby.Client;
using Hamstix.Haby.Client.Auth;
using Hamstix.Haby.Client.Extensions;
using Hamstix.Haby.Client.Services;
using Hamstix.Haby.Shared.Grpc.Configuration;
using Hamstix.Haby.Shared.Grpc.ConfigurationUnits;
using Hamstix.Haby.Shared.Grpc.Generators;
using Hamstix.Haby.Shared.Grpc.Plugins;
using Hamstix.Haby.Shared.Grpc.Services;
using Hamstix.Haby.Shared.Grpc.System;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

const string grpcHttpCliientName = "Grpc";

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddTransient<GrpcWebHandler>();
builder.Services.AddHttpClient(grpcHttpCliientName, client => client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler<GrpcWebHandler>();

builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>()
    .CreateClient(grpcHttpCliientName));

builder.Services.AddScoped<Hamstix.Haby.Client.Components.Toast.ToastService>();
builder.Services.AddSingleton<ILocalStorage, LocalStorage>();
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<AuthenticationStateProvider, AppAuthenticationStateProvider>();
builder.Services.AddScoped<IAuthenticationService, AuthenticationService>();

// GRPC
builder.Services.AddGrpcPreConfiguredClient<SystemService.SystemServiceClient>(new Uri(builder.HostEnvironment.BaseAddress));
builder.Services.AddGrpcPreConfiguredClient<SystemStatusService.SystemStatusServiceClient>(new Uri(builder.HostEnvironment.BaseAddress));
builder.Services.AddGrpcPreConfiguredClient<PluginsService.PluginsServiceClient>(new Uri(builder.HostEnvironment.BaseAddress));
builder.Services.AddGrpcPreConfiguredClient<ServicesService.ServicesServiceClient>(new Uri(builder.HostEnvironment.BaseAddress));
builder.Services.AddGrpcPreConfiguredClient<GeneratorsService.GeneratorsServiceClient>(new Uri(builder.HostEnvironment.BaseAddress));
builder.Services.AddGrpcPreConfiguredClient<ConfigurationUnitsService.ConfigurationUnitsServiceClient>(new Uri(builder.HostEnvironment.BaseAddress));
builder.Services.AddGrpcPreConfiguredClient<ConfigurationService.ConfigurationServiceClient>(new Uri(builder.HostEnvironment.BaseAddress));

await builder.Build().RunAsync();
