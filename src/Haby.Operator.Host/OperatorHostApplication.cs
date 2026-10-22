using System.Net;
using System.Security.Cryptography.X509Certificates;
using Haby.Application;
using Haby.Manifest;
using Haby.Persistence.PostgreSql;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Haby.Operator.Host;

/// <summary>Composition root for the single-instance alpha operator host.</summary>
public static class OperatorHostApplication
{
    public const int MaxReceiveBytes = 1024 * 1024;
    public const string TechnicalActorId = "haby-alpha-operator-host";

    public static async Task<WebApplication> BuildMigratedAsync(string[] args,
        CancellationToken cancellationToken = default)
    {
        var app = Build(args);
        try
        {
            await app.Services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync(cancellationToken);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var connection = builder.Configuration["Haby:PostgreSql:ConnectionString"];
        var certificatePath = builder.Configuration["Haby:Tls:CertificatePath"];
        var certificatePassword = builder.Configuration["Haby:Tls:CertificatePassword"];
        var portText = builder.Configuration["Haby:Port"];
        var invalid = new List<string>();
        if (string.IsNullOrWhiteSpace(connection)) invalid.Add("Haby:PostgreSql:ConnectionString");
        if (string.IsNullOrWhiteSpace(certificatePath) || !File.Exists(certificatePath))
            invalid.Add("Haby:Tls:CertificatePath");
        if (!int.TryParse(portText, out var port) || port is < 0 or > 65535) invalid.Add("Haby:Port");
        if (invalid.Count > 0)
            throw new InvalidOperationException("Missing or invalid configuration keys: " + string.Join(", ", invalid));
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath!, certificatePassword);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            listen.UseHttps(certificate);
        }));
        builder.Services.AddGrpc(options => options.MaxReceiveMessageSize = MaxReceiveBytes);
        builder.Services.AddGrpcReflection();
        builder.Services.AddSingleton(new ManifestContractCatalog([PostgreSqlDatabaseContract.Definition], [], [], []));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ImportApplicationManifest>();
        builder.Services.AddPostgreSqlManifestCatalog(connection!);
        var app = builder.Build();
        app.MapGrpcService<ManifestOperatorService>();
        app.MapGrpcReflectionService();
        return app;
    }
}
