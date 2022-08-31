using Hamstix.Haby.Server.Authentication;
using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Server.Configurator;
using Hamstix.Haby.Server.DependencyInjection;
using Hamstix.Haby.Server.Extensions;
using Hamstix.Haby.Server.Grpc;
using Hamstix.Haby.Server.Services;
using Hamstix.Haby.Server.Services.Impl;
using Mapster;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Monq.Core.BasicDotNetMicroservice.GrpcInterceptors;
using Monq.Core.HttpClientExtensions.Exceptions;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

var connectionString = builder.Configuration.ReadPgConnectionString();
builder.Services
    .AddDbContext<HabbyContext>(options => options.UseNpgsql(connectionString));

builder.Services
    .AddGlobalExceptionFilter()
    .AddExceptionHandler<ResponseException>(ex =>
        new ObjectResult(System.Text.Json.JsonSerializer.Deserialize<object>(ex.ResponseData))
        {
            StatusCode = (int)ex.StatusCode
        })
    .AddDefaultExceptionHandlers();

builder.Services.AddHttpClient();
builder.Services.AddHttpClient(Hamstix.Haby.Shared.PluginsCore.Constants.DisableSslVerification)
    .ConfigurePrimaryHttpMessageHandler(() =>
        {
            return new HttpClientHandler
            {
                ClientCertificateOptions = ClientCertificateOption.Manual,
                ServerCertificateCustomValidationCallback =
                    (httpRequestMessage, cert, certChain, policyErrors) => true
            };
        });

builder.Services.AddGrpc(options =>
{
    //options.Interceptors.Add<DownstreamHttpRequestInterceptor>();
    options.EnableDetailedErrors = true;
    options.MaxReceiveMessageSize = 2 * 1024 * 1024; // 2 MB
    options.MaxSendMessageSize = 5 * 1024 * 1024; // 5 MB
});

builder.Services.AddCors(setupAction =>
{
    setupAction.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader().AllowAnyOrigin().AllowAnyMethod().WithExposedHeaders("Grpc-Status", "Grpc-Message", "Grpc-Encoding", "Grpc-Accept-Encoding");
    });
});

TypeAdapterConfig.GlobalSettings.Scan(AppDomain.CurrentDomain.GetAssemblies());

builder.Services.AddAuthentication(AppConstants.AuthenticationSchemeName)
                .AddScheme<HabyAuthenticationOptions, HabyAuthenticationHandler>(AppConstants.AuthenticationSchemeName, null);
builder.Services.AddSingleton<HabyAuthenticationManager>();

builder.Services.RegisterPlugins(builder.Configuration);

builder.Services.AddTransient<ICuConfigurator, CuConfigurator>();
builder.Services.AddTransient<IForeignKeyConfigurator, ForeignKeyConfigurator>();
builder.Services.AddTransient<IServiceConfigurator, ServiceConfigurator>();
builder.Services.AddTransient<ISchemaInitializer, DefaultSchemaInitializer>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UpdateDatabase();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error");
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseCors();
app.UseRouting();
app.UseGrpcWeb(new GrpcWebOptions
{
    DefaultEnabled = true
});
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.UseEndpoints(e =>
{
    e.MapControllers();
    e.MapGrpcService<SystemGrpcService>().EnableGrpcWeb();
    e.MapGrpcService<SystemStatusGrpcService>().EnableGrpcWeb();
    e.MapGrpcService<PluginsGrpcService>().EnableGrpcWeb();
    e.MapGrpcService<ServicesGrpcService>().EnableGrpcWeb();
    e.MapGrpcService<GeneratorsGrpcService>().EnableGrpcWeb();
    e.MapGrpcService<ConfigurationUnitsGrpcService>().EnableGrpcWeb();
    e.MapGrpcService<ConfigurationGrpcService>().EnableGrpcWeb();
});
app.MapFallbackToFile("index.html");

app.Run();
