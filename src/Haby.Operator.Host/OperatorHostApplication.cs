using System.Net;
using System.Security.Cryptography.X509Certificates;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.PostgreSql;
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

    public static WebApplication Build(string[] args) => Build(args, null);

    // Tests use the real composition with deterministic internal fault hooks, never a production setting or RPC.
    internal static WebApplication Build(string[] args, Action<IServiceCollection>? configureServices)
    {
        var builder = WebApplication.CreateBuilder(args);
        // Driver/EF diagnostics can contain connection settings, payloads and raw exceptions even
        // when the application catches a bounded failure. Keep them outside the operator log surface.
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
        builder.Logging.AddFilter("Npgsql", LogLevel.None);
        builder.Logging.AddFilter("Grpc", LogLevel.None);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.None);

        var connection = builder.Configuration["Haby:PostgreSql:ConnectionString"];
        var tlsEnabledText = builder.Configuration["Haby:Tls:Enabled"];
        var certificatePath = builder.Configuration["Haby:Tls:CertificatePath"];
        var certificatePassword = builder.Configuration["Haby:Tls:CertificatePassword"];
        var portText = builder.Configuration["Haby:Port"];
        var addressText = builder.Configuration["Haby:ListenAddress"] ?? "127.0.0.1";

        var invalid = new List<string>();
        if (!IPAddress.TryParse(addressText, out var listenAddress)) invalid.Add("Haby:ListenAddress");
        if (string.IsNullOrWhiteSpace(connection)) invalid.Add("Haby:PostgreSql:ConnectionString");
        var tlsEnabled = true;
        if (tlsEnabledText is not null && !bool.TryParse(tlsEnabledText, out tlsEnabled))
            invalid.Add("Haby:Tls:Enabled");
        if (tlsEnabled && (string.IsNullOrWhiteSpace(certificatePath) || !File.Exists(certificatePath)))
            invalid.Add("Haby:Tls:CertificatePath");
        if (!int.TryParse(portText, out var port) || port is < 0 or > 65535) invalid.Add("Haby:Port");
        if (invalid.Count > 0)
            throw new InvalidOperationException("Missing or invalid configuration keys: " + string.Join(", ", invalid));
        var certificate = tlsEnabled
            ? X509CertificateLoader.LoadPkcs12FromFile(certificatePath!, certificatePassword)
            : null;
        builder.WebHost.ConfigureKestrel(options => options.Listen(listenAddress!, port, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            if (tlsEnabled) listen.UseHttps(certificate!);
        }));
        builder.Services.AddGrpc(options => { options.MaxReceiveMessageSize = MaxReceiveBytes; options.MaxSendMessageSize = 8 * 1024 * 1024; });
        builder.Services.AddGrpcReflection();
        var reflectionEnabled = true;
        if (builder.Configuration["Haby:Reflection:Enabled"] is { } reflectionText && !bool.TryParse(reflectionText, out reflectionEnabled))
            throw new InvalidOperationException("Invalid configuration key: Haby:Reflection:Enabled");
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IOperatorAccessStore, PostgreSqlOperatorAccessStore>();
        builder.Services.AddAuthentication(OperatorAuthentication.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, OperatorAuthentication>(OperatorAuthentication.SchemeName, _ => { });
        builder.Services.AddAuthorization(options => options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .AddAuthenticationSchemes(OperatorAuthentication.SchemeName).RequireAuthenticatedUser().Build());
        builder.Services.AddSingleton(PostgreSqlConnectionRendering.CreateCatalog());
        builder.Services.AddSingleton(PostgreSqlConnectionRendering.FormatterCatalog());
        builder.Services.AddSingleton(new ManifestContractCatalog([PostgreSqlDatabaseContract.Definition, PostgreSqlApplicationLoginContract.Definition], [Haby.Kubernetes.Contracts.KubernetesContract.Definition],
            [new(OutputContractId.Create(PostgreSqlApplicationConnectionContract.Contract), PostgreSqlConsumerOptions.Schema)],
            [new(PostgreSqlConnectionRendering.Renderer)]));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ComposeConfigurationDocument>();
        builder.Services.AddSingleton<PostgreSqlConfigurationDocumentStore>();
        builder.Services.AddSingleton<IConfigurationDocumentStore>(p => p.GetRequiredService<PostgreSqlConfigurationDocumentStore>());
        builder.Services.AddSingleton<IConfigurationApplicationInventory>(p => p.GetRequiredService<PostgreSqlConfigurationDocumentStore>());
        builder.Services.AddSingleton<MaterializeApplicationConfiguration>();
        builder.Services.AddSingleton<PostgreSqlConfigurationAccessStore>();
        builder.Services.AddSingleton<IConfigurationAccessStore>(p => p.GetRequiredService<PostgreSqlConfigurationAccessStore>());
        builder.Services.AddSingleton<IConfigurationDeliveryDocumentStore>(p => p.GetRequiredService<PostgreSqlConfigurationAccessStore>());
        builder.Services.AddSingleton<IConfigurationResolutionBinding, PostgreSqlConnectionResolutionBinding>();
        builder.Services.AddSingleton<DeliverConfiguration>();
        builder.Services.AddSingleton<ImportApplicationManifest>();
        builder.Services.AddSingleton<ImportConfigurationProfile>();
        builder.Services.AddSingleton<IPlacementCodec, Haby.Kubernetes.Contracts.KubernetesPlacementCodec>();
        builder.Services.AddSingleton<IPlacementRevisionStore, PostgreSqlPlacementRevisionStore>();
        builder.Services.AddSingleton<PlacementAdministration>();
        builder.Services.AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>();
        builder.Services.AddSingleton<PreviewImportedDatabase>();
        builder.Services.AddPostgreSqlManifestCatalog(connection!);
        // Execution resolves only the committed exact Provider revision and SecretVersion.
        builder.Services.AddPostgreSqlProviderConfigurationCodec();
        builder.Services.AddSingleton<IPlanActionDescriber, PostgreSqlPlanActionDescriber>();
        builder.Services.AddSingleton<DescribeDurablePlan>();
        builder.Services.AddSingleton<IProviderAliasResolver, PostgreSqlProviderAliasResolver>();
        builder.Services.AddPostgreSqlDurableDesiredStateCommitter();
        // Explicit pilot storage choice under ADR-006: database rows and backups contain recoverable secrets.
        builder.Services.AddPostgreSqlPlaintextSecretStore();
        builder.Services.AddPostgreSqlProviderConfigurationStore();
        builder.Services.AddPostgreSqlDurableOperationJournal();
        builder.Services.AddPostgreSqlDurableOperationExecution();
        // One database admission explicitly composes standalone and pair shapes; no duplicate key.
        builder.Services.AddSingleton<IDurableActionAdmissionHandler>(
            new PostgreSqlPairResourceAdmission(PostgreSqlApplicationLoginContract.Id));
        builder.Services.AddSingleton<IGeneratedCredentialOperations, PostgreSqlGeneratedCredentialOperations>();
        builder.Services.AddSingleton<IDurableActionHandler>(p => new GeneratedCredentialAction(
            PostgreSqlApplicationLoginAction.RegistrationKey.Admission,
            p.GetRequiredService<IDurableOperationJournal>(), p.GetRequiredService<IGeneratedCredentialSlots>(),
            p.GetRequiredService<IGeneratedCredentialOperations>(), p.GetRequiredService<TimeProvider>()));
        builder.Services.AddPostgreSqlDatabaseRestrict();
        builder.Services.AddPostgreSqlProviderResourceReservations();
        builder.Services.AddPostgreSqlApplicationLogin();
        builder.Services.AddPostgreSqlApplicationAccess();
        builder.Services.AddPostgreSqlDatabaseRecoveryJournal();
        builder.Services.AddPostgreSqlDatabaseRecovery();
        builder.Services.AddSingleton<PostgreSqlOperationRecoveryAdapter>();
        builder.Services.AddSingleton<OperationRecoveryAdapter>();

        builder.Services.AddSingleton<PostgreSqlProviderRevisionPreflight>();
        builder.Services.AddSingleton<IProviderPreflightHandler, PostgreSqlPreflightHandler>();
        builder.Services.AddSingleton<ProviderPreflightRegistry>();
        builder.Services.AddSingleton<IProviderRevisionPreflight, ExactProviderRevisionPreflight>();
        builder.Services.AddPostgreSqlProviderAliasStore();
        builder.Services.AddSingleton<ProviderAccessAdministration>();
        builder.Services.AddSingleton<IProviderIdentityQuery, PostgreSqlProviderIdentityQuery>();
        configureServices?.Invoke(builder.Services);
        var app = builder.Build();
        // Validate only current-build registrations, never require handlers for every stored revision.
        _ = app.Services.GetRequiredService<ProviderDeclarationRegistry>();
        _ = app.Services.GetRequiredService<IProviderConfigurationStore>();
        _ = app.Services.GetRequiredService<ProviderPreflightRegistry>();
        _ = app.Services.GetRequiredService<DurableActionRegistry>();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGrpcService<ManifestOperatorService>();
        app.MapGrpcService<ConfigurationDeliveryService>().AllowAnonymous();
        if (reflectionEnabled) app.MapGrpcReflectionService();

        return app;
    }
}
