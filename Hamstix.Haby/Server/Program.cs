using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Monq.Core.HttpClientExtensions.Exceptions;
using Hamstix.Haby.Server.DependencyInjection;
using Hamstix.Haby.Server.Extensions;
using Hamstix.Haby.Server.Configurator;
using Hamstix.Haby.Server.Services;
using Hamstix.Haby.Server.Services.Impl;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

var connectionString = builder.Configuration.ReadPgConnectionString();
builder.Services
    .AddDbContext<Hamstix.Haby.Server.Configuration.HabbyContext>(options => options.UseNpgsql(connectionString));

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

builder.Services.AddAutoMapper(typeof(Program));

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

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();
