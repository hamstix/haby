using System.Net;
using System.Security.Cryptography.X509Certificates;
using Haby.Application;
using Haby.Domain;
using Haby.Manifest;
using Haby.Kubernetes;
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
        CancellationToken cancellationToken = default) => await BuildMigratedCoreAsync(args, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Builds an unstarted Host with trusted additive services registered after stock services.</summary>
    public static WebApplication BuildComposed(string[] args,
        Action<IServiceCollection, IConfiguration> configureServices)
    {
        ArgumentNullException.ThrowIfNull(configureServices);
        return BuildCore(args, configureServices);
    }

    /// <summary>Builds, validates and migrates an unstarted Host. The caller owns its successful lifecycle.</summary>
    public static async Task<WebApplication> BuildComposedMigratedAsync(string[] args,
        Action<IServiceCollection, IConfiguration> configureServices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configureServices);
        return await BuildMigratedCoreAsync(args, configureServices, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WebApplication> BuildMigratedCoreAsync(string[] args,
        Action<IServiceCollection, IConfiguration>? configureServices, CancellationToken cancellationToken)
    {
        var app = BuildCore(args, configureServices, out var resources);
        try
        {
            await app.Services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync(cancellationToken).ConfigureAwait(false);
            return app;
        }
        catch
        {
            await DisposeFailedAsync(app, resources).ConfigureAwait(false);
            throw;
        }
    }

    public static WebApplication Build(string[] args) => BuildCore(args, null);

    // Tests use the real composition with deterministic internal fault hooks, never a production setting or RPC.
    internal static WebApplication Build(string[] args, Action<IServiceCollection>? configureServices) =>
        BuildCore(args, configureServices is null ? null : (services, _) => configureServices(services));

    private static WebApplication BuildCore(string[] args, Action<IServiceCollection, IConfiguration>? configureServices) =>
        BuildCore(args, configureServices, out _);

    private static WebApplication BuildCore(string[] args, Action<IServiceCollection, IConfiguration>? configureServices,
        out HostResources resources)
    {
        var builder = WebApplication.CreateBuilder(args);
        var ownership = resources = new HostResources(builder.Configuration);
        WebApplication? app = null;
        try { return BuildCore(builder, configureServices, ownership, out app); }
        catch
        {
            // This synchronous API must also finish asynchronous disposal. Start cleanup without
            // the caller's SynchronizationContext, and join it; never leave detached cleanup work.
            if (app is not null) Task.Run(() => DisposeFailedAsync(app, ownership)).GetAwaiter().GetResult();
            else ReleaseFailedResources(ownership);
            throw;
        }
    }

    private static async Task DisposeFailedAsync(WebApplication app, HostResources resources)
    {
        try { await app.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { /* Preserve the original validation/migration failure, without logging secrets. */ }
        finally { ReleaseFailedResources(resources); }
    }

    private static void ReleaseFailedResources(HostResources resources)
    {
        try { resources.Dispose(); }
        catch (Exception) { /* Preserve the factory failure. */ }
        try { resources.ReleaseConfiguration(); }
        catch (Exception) { /* Preserve the factory failure. */ }
    }

    // Retain factory-created ownership independently of the provider: failed DI disposal may
    // never reach ConfigurationManager. Never resolve new services while disposing the app.
    private sealed class HostResources(ConfigurationManager configuration) : IDisposable
    {
        private X509Certificate2? _value;
        private ConfigurationManager? _configuration = configuration;
        internal X509Certificate2? Value { get => _value; set => _value = value; }
        public void Dispose() => Interlocked.Exchange(ref _value, null)?.Dispose();
        // ConfigurationManager owns provider disposal and safely handles its already-disposed
        // state. Do not enumerate/dispose arbitrary downstream providers or external instances.
        internal void ReleaseConfiguration() => Interlocked.Exchange(ref _configuration, null)?.Dispose();
    }

    private static WebApplication BuildCore(WebApplicationBuilder builder,
        Action<IServiceCollection, IConfiguration>? configureServices, HostResources certificateOwner,
        out WebApplication? app)
    {
        app = null;
        var runtimeAuthentication = RuntimeAuthenticationComposition.Read(builder.Configuration);
        RuntimeAuthenticationComposition.Add(builder.Services, runtimeAuthentication);
        var runtimeDelivery = RuntimeDeliveryOptions.Read(builder.Configuration);
        builder.Services.AddSingleton(_ => new RuntimeDeliveryLifetime(runtimeDelivery));
        builder.Services.AddSingleton<IRuntimeConfigurationStore, PostgreSqlRuntimeConfigurationStore>();
        builder.Services.AddSingleton<ConfigurationDeliveryMaterializer>();
        builder.Services.AddSingleton<DeliverRuntimeConfiguration>();
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
        var certificate = certificateOwner.Value = tlsEnabled
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
        builder.Services.AddSingleton<IPlacementSelectionScopeQuery, PostgreSqlPlacementSelectionScopeQuery>();
        builder.Services.AddSingleton<IPlacementSelectionStore, PostgreSqlPlacementSelectionStore>();
        builder.Services.AddSingleton<PlacementSelectionAdministration>();
        builder.Services.AddSingleton<ICheckpointProviderCapability, PostgreSqlDatabaseCheckpointCapability>();
        builder.Services.AddSingleton<PreviewImportedDatabase>();
        builder.Services.AddPostgreSqlManifestCatalog(connection!);
        // Execution resolves only the committed exact Provider revision and SecretVersion.
        builder.Services.AddPostgreSqlProviderConfigurationCodec();
        builder.Services.AddSingleton<KubernetesProviderConfigurationCodec>();
        builder.Services.AddSingleton<IProviderConfigurationCodec>(p => p.GetRequiredService<KubernetesProviderConfigurationCodec>());
        builder.Services.AddSingleton<IProviderDeclarationAdapter, KubernetesProviderDeclarationAdapter>();
        builder.Services.AddSingleton<IProviderPreflightHandler, KubernetesPreflightHandler>();
        builder.Services.AddSingleton<IPlanActionDescriber, PostgreSqlPlanActionDescriber>();
        builder.Services.AddSingleton<DescribeDurablePlan>();
        builder.Services.AddSingleton<IWorkloadPlanDescriber, KubernetesWorkloadPlanDescriber>();
        builder.Services.AddSingleton<DescribeWorkloadPlan>();
        builder.Services.AddSingleton<IOperatorDeploymentAdmission>(p => new PostgreSqlOperatorDeploymentAdmission(
            p.GetRequiredService<PostgreSqlDurableDeploymentPlanner>(),
            (PostgreSqlDurableDesiredStateCommitter)p.GetRequiredService<IDurableDesiredStateCommitter>(),
            p.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ManifestCatalogDbContext>>(),
            [new InternalDeploymentAction(p.GetRequiredService<ISecretStore>())]));
        var submitText = builder.Configuration["Haby:WorkloadSubmission:RequestMilliseconds"];
        if (submitText is not null && !int.TryParse(submitText, out _))
            throw new InvalidOperationException("Invalid configuration key: Haby:WorkloadSubmission:RequestMilliseconds");
        var submitOptions = new WorkloadSubmissionOptions(submitText is null ? 30000 : int.Parse(submitText, System.Globalization.CultureInfo.InvariantCulture));
        submitOptions.Validate();
        builder.Services.AddSingleton(submitOptions);
        builder.Services.AddSingleton<InternalDeploymentAction>();
        builder.Services.AddSingleton<InternalRuntimeIdentityAction>();
        builder.Services.AddSingleton<InternalWorkloadConfiguration>(p => new(
            p.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ManifestCatalogDbContext>>(),
            p.GetRequiredService<PostgreSqlDurableDeploymentPlanner>(), p.GetRequiredService<PostgreSqlConfigurationDocumentStore>(),
            [p.GetRequiredService<InternalDeploymentAction>()], p.GetServices<IExternalOperationOutputContract>(),
            p.GetServices<IProviderConfigurationCodec>(), p.GetRequiredService<DurableActionAdmission>(), p.GetServices<IDurableResourceReuseVerifier>()));
        builder.Services.AddSingleton<InternalDurableActions>(p => new(
            p.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ManifestCatalogDbContext>>(),
            [p.GetRequiredService<InternalDeploymentAction>(), p.GetRequiredService<InternalRuntimeIdentityAction>()],
            p.GetServices<IProviderConfigurationCodec>(), p.GetServices<IExternalOperationOutputContract>(), p.GetRequiredService<DurableActionAdmission>(),
            p.GetRequiredService<DurableOperationSerialization>(), p.GetRequiredService<TimeProvider>(),
            configurationPreparation: p.GetRequiredService<InternalWorkloadConfiguration>(), reuseVerifiers: p.GetServices<IDurableResourceReuseVerifier>()));
        builder.Services.AddSingleton<InternalRuntimeIdentityPreparation>(p => new(
            p.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ManifestCatalogDbContext>>(),
            p.GetRequiredService<InternalWorkloadConfiguration>(), p.GetRequiredService<InternalDurableActions>(), InternalRuntimeIdentityAction.Create));
        builder.Services.AddSingleton<InternalRuntimePublication>();
        builder.Services.AddSingleton<InternalRuntimePublicationPreparation>();
        builder.Services.AddSingleton<IOperatorWorkloadSubmission>(p => new PostgreSqlWorkloadSubmission(
            p.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ManifestCatalogDbContext>>(),
            p.GetRequiredService<InternalWorkloadConfiguration>(), p.GetRequiredService<InternalDurableActions>(),
            p.GetRequiredService<InternalRuntimeIdentityPreparation>(), p.GetRequiredService<InternalRuntimePublicationPreparation>(),
            p.GetRequiredService<DurableOperationSerialization>(), submitOptions, InternalDeploymentAction.Create));
        int ObservationSetting(string name, int fallback)
        {
            var key = "Haby:WorkloadObservation:" + name;
            var value = builder.Configuration[key];
            if (value is null) return fallback;
            if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                throw new InvalidOperationException("Invalid configuration key: " + key);
            return parsed;
        }
        var observationLimits = new RolloutOptions(ObservationSetting("MaximumCalls", 64), ObservationSetting("MaximumRounds", 32),
            ObservationSetting("ReadTimeoutMilliseconds", 10000), ObservationSetting("PollMilliseconds", 1000), ObservationSetting("RequestMilliseconds", 30000));
        observationLimits.Validate();
        int BatchSetting(string name, int fallback)
        {
            var key = "Haby:WorkloadBatchObservation:" + name;
            var value = builder.Configuration[key];
            if (value is null) return fallback;
            if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                throw new InvalidOperationException("Invalid configuration key: " + key);
            return parsed;
        }
        var batchLimits = new BatchObservationOptions(BatchSetting("MaximumMembers", 200), BatchSetting("Concurrency", 4),
            BatchSetting("MaximumReads", 256), BatchSetting("RequestMilliseconds", 30000), BatchSetting("StartsPerSecond", 10));
        batchLimits.Validate();
        builder.Services.AddSingleton<IOperatorRolloutObservation>(p => new PostgreSqlOperatorRolloutObservation(
            p.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ManifestCatalogDbContext>>(),
            p.GetRequiredService<InternalWorkloadConfiguration>(), p.GetRequiredService<InternalDurableActions>(), observationLimits,
            p.GetRequiredService<TimeProvider>(), InternalDeploymentAction.Create, InternalRuntimeIdentityAction.Create, batchLimits));
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
        builder.Services.AddPostgreSqlResourceObservationAcceptance();
        builder.Services.AddPostgreSqlExistingPairImport();
        builder.Services.AddPostgreSqlOperatorImportRequests();
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
        builder.Services.AddSingleton(_ => certificateOwner);
        configureServices?.Invoke(builder.Services, builder.Configuration);
        app = builder.Build();
        _ = app.Services.GetRequiredService<HostResources>();
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
