using Hamstix.Haby.Client.Extensions;
using Hamstix.Haby.Server.Authentication;
using Hamstix.Haby.Server.Components;
using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Server.Configurator;
using Hamstix.Haby.Server.DependencyInjection;
using Hamstix.Haby.Server.Extensions;
using Hamstix.Haby.Server.Grpc;
using Hamstix.Haby.Server.Services;
using Hamstix.Haby.Server.Services.Impl;
using Mapster;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddCascadingAuthenticationState();

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = AppConstants.SmartAuthenticationSchemeName;
    options.DefaultAuthenticateScheme = AppConstants.SmartAuthenticationSchemeName;
    options.DefaultChallengeScheme = AppConstants.SmartAuthenticationSchemeName;
    options.DefaultSignInScheme = AppConstants.CookieAuthenticationSchemeName;
})
.AddPolicyScheme(AppConstants.SmartAuthenticationSchemeName, null, options =>
{
    options.ForwardDefaultSelector = context =>
        context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? AppConstants.AuthenticationSchemeName
            : AppConstants.CookieAuthenticationSchemeName;
})
.AddCookie(AppConstants.CookieAuthenticationSchemeName, options =>
{
    options.Cookie.Name = "Haby.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.LoginPath = "/login";
    options.Events.OnRedirectToLogin = context =>
    {
        if (!context.Request.Path.StartsWithSegments("/auth")
            && HttpMethods.IsGet(context.Request.Method)
            && context.Request.GetTypedHeaders().Accept?.Any(header =>
                header.MediaType.Value?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true) == true)
        {
            context.Response.Redirect(context.RedirectUri);
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }

        return Task.CompletedTask;
    };
})
.AddScheme<HabyAuthenticationOptions, HabyAuthenticationHandler>(
    AppConstants.AuthenticationSchemeName, null);
builder.Services.AddAuthorization();

var connectionString = builder.Configuration.ReadPgConnectionString();
builder.Services
    .AddDbContext<HabbyContext>(options => options.UseNpgsql(connectionString));

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation());

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

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.MapStaticAssets();

app.UseCors();
app.UseRouting();

app.UseGrpcWeb(new GrpcWebOptions
{
    DefaultEnabled = true
});
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapHealthChecks("/health");
app.MapPost("/auth/login", async (
    Hamstix.Haby.Shared.Grpc.System.AclModel request,
    HabyAuthenticationManager authenticationManager,
    HttpContext context) =>
{
    if (!authenticationManager.ValidateToken(request.Token))
        return Results.Unauthorized();

    var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "Haby user")],
        AppConstants.CookieAuthenticationSchemeName);
    await context.SignInAsync(
        AppConstants.CookieAuthenticationSchemeName,
        new ClaimsPrincipal(identity));

    return Results.NoContent();
}).AllowAnonymous();
app.MapPost("/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(AppConstants.CookieAuthenticationSchemeName);
    return Results.NoContent();
}).AllowAnonymous();
app.MapGet("/auth/user", () => Results.NoContent())
    .RequireAuthorization();
app.MapGrpcService<SystemGrpcService>().EnableGrpcWeb();
app.MapGrpcService<SystemStatusGrpcService>().EnableGrpcWeb();
app.MapGrpcService<PluginsGrpcService>().EnableGrpcWeb();
app.MapGrpcService<ServicesGrpcService>().EnableGrpcWeb();
app.MapGrpcService<GeneratorsGrpcService>().EnableGrpcWeb();
app.MapGrpcService<ConfigurationUnitsGrpcService>().EnableGrpcWeb();
app.MapGrpcService<ConfigurationGrpcService>().EnableGrpcWeb();
app.MapGrpcService<OrganizationUnitsGrpcService>().EnableGrpcWeb();

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Hamstix.Haby.Client.Routes).Assembly);

app.Run();
