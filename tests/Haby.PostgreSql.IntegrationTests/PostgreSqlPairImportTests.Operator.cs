using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Configuration.Contracts;
using Haby.Domain;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using ImportStatus = Haby.Operator.Contracts.ResourceImportRequestStatus;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlPairImportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_operator_import_rehearsal_preserves_tls_identity_history_and_delivery_across_correction_and_restart(bool postCommit)
    {
        var pair = await ExistingPair();
        await using var passwordGuard = await PasswordGuard(pair);
        var before = await Snapshot(pair);
        var tls = await EnableTls();
        using var pgRoot = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(tls.RootFile));
        using var caKey = RSA.Create(2048);
        using var ca = Root(caKey, "haby-owned-operator-" + Guid.NewGuid().ToString("N"));
        using var publicCa = X509CertificateLoader.LoadCertificate(ca.RawData);
        var (pem, key) = Leaf(ca, true);
        using var certificate = X509Certificate2.CreateFromPem(pem, key);
        var directory = Path.Combine(Path.GetTempPath(), "haby-import-tls-" + Guid.NewGuid().ToString("N"));
        tlsDirectories.Add(Path.GetFullPath(directory)); Directory.CreateDirectory(directory);
        var certificatePath = Path.Combine(directory, "operator.pfx");
        var configPath = Path.Combine(directory, "cli.yaml");
        var inputPath = Path.Combine(directory, "names.json");
        await File.WriteAllBytesAsync(certificatePath, certificate.Export(X509ContentType.Pfx, "test-only"));
        await File.WriteAllTextAsync(inputPath, """{"format":"haby.postgresql.import-names/v1","input":INPUT}""".Replace("INPUT",
            PostgreSqlImportPreparer.Encode(new(pair.Database, pair.Login, pair.Input.Schema)).Json, StringComparison.Ordinal));
        var catalogName = "import_operator_" + Guid.NewGuid().ToString("N"); databases.Add(catalogName);
        await Sql(Control(), $"CREATE DATABASE \"{catalogName}\"");
        var catalog = new NpgsqlConnectionStringBuilder(Control()) { Database = catalogName }.ConnectionString;
        EnvironmentRecordId environment;
        var native = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        var required = Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED") == "1";
        if (required) Assert.False(string.IsNullOrWhiteSpace(native), "The hosted rehearsal requires the published Native AOT CLI.");
        var isolated = OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("HABY_IMPORT_REHEARSAL_ISOLATED_PROFILE") == "1";
        if (required) Assert.True(isolated, "Published CLI TLS rehearsal requires an explicitly isolated Linux qualification profile.");
        var binary = isolated ? native ?? Environment.GetEnvironmentVariable("HABY_IMPORT_REHEARSAL_CLI_PATH") : null;
        if (binary is not null) Assert.True(Path.IsPathFullyQualified(binary) && File.Exists(binary), "Published rehearsal CLI is missing.");
        var transport = isolated ? null : new PostgreSqlProviderTransport(tls.RootFile);
        var firstId = Guid.NewGuid(); var correctedId = Guid.NewGuid();
        var lose = new LosePublicImportResponse(firstId);
        using var roots = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        if (isolated) roots.Open(OpenFlags.ReadWrite);
        var ownedRoots = new List<X509Certificate2>();
        try
        {
            // Hosted Linux uses an explicitly disposable profile and stock client trust.
            // Developer runs use explicit test CA trust without editing the user's root store.
            foreach (var root in isolated ? new[] { pgRoot, publicCa } : [])
            {
                Assert.Empty(roots.Certificates.Find(X509FindType.FindByThumbprint, root.Thumbprint, false));
                roots.Add(root); ownedRoots.Add(root);
            }
            environment = await SeedPublicImportProvider(catalog, transport);
            var applicationName = "public-import-" + Guid.NewGuid().ToString("N")[..12];
            RevisionReference reference;
            ResourceImportRequestReply original;
            var rejected = Guid.NewGuid().ToString("N");
            await VerificationLogging();
            await using (var host = await StartPublicImportHost(catalog, certificatePath, transport, lose))
            {
                using var channel = PublicImportChannel(host, publicCa, isolated);
                var anonymous = new ManifestOperator.ManifestOperatorClient(channel);
                await OperatorHostIntegrationTests.AuthDenied(async () => await anonymous.GetResourceImportRequestAsync(new() { RequestId = firstId.ToString() }));
                await OperatorHostIntegrationTests.AuthDenied(async () => await anonymous.CreateResourceImportChangeSetAsync(new()
                { RequestId = firstId.ToString(), CredentialMaterial = ByteString.CopyFromUtf8(rejected) }));
                var client = OperatorTestCredentials.Client(channel);
                Assert.Equal(ImportStatus.NotFound, (await client.GetResourceImportRequestAsync(new() { RequestId = firstId.ToString() })).Status);
                var envelope = Declaration(applicationName);
                envelope["manifest"]!["spec"]!["components"]!["worker"]!["bindings"]!["use"]!["options"]!["sslMode"] = "verify-full";
                var imported = await client.ImportAsync(new() { OriginalEnvelope = ByteString.CopyFrom(PostgreSqlPairPlanningTests.Bytes(envelope)) });
                Assert.Equal(ImportReply.OutcomeOneofCase.Created, imported.OutcomeCase); reference = imported.Created.Revision.Reference;
                original = await CreatePublic(host, client, firstId, rejected, null, lost: true);
                Assert.True(lose.Thrown); Assert.Equal(ImportStatus.Created, original.Status);
                var replay = await CreatePublic(host, client, firstId, rejected, null, lost: false);
                Assert.Equal(original.Request, replay.Request);
            }
            await using (var host = await StartPublicImportHost(catalog, certificatePath, transport))
            {
                using var channel = PublicImportChannel(host, publicCa, isolated);
                var client = OperatorTestCredentials.Client(channel);
                Assert.Equal(original.Request, (await client.GetResourceImportRequestAsync(new() { RequestId = firstId.ToString() })).Request);
                var oldPlan = await PlanPublic(host, client, original.Request.ChangeSetId);
                ResourceImportCorrection correction;
                if (postCommit)
                {
                    var oldCommit = await CommitPublic(host, client, original.Request.ChangeSetId, oldPlan.Plan.PlanId);
                    Assert.Equal(1, oldCommit.Commit.Generation);
                    Assert.Equal(GenerationExecutionStatus.Failed, (await ExecutePublic(host, client, oldCommit.Commit, false)).Status);
                    correction = new() { PreviousRequestId = firstId.ToString(), Reason = ResourceImportCorrectionReason.CredentialRejected,
                        Committed = new() { DeploymentId = oldCommit.Commit.DeploymentId, Generation = 1 } };
                }
                else correction = new() { PreviousRequestId = firstId.ToString(), Reason = ResourceImportCorrectionReason.CredentialRejected, ExpectedAbsence = true };
                var candidate = await CreatePublic(host, client, correctedId, pair.Password, correction, lost: false);
                Assert.Equal(ImportStatus.Created, candidate.Status);
                var plan = await PlanPublic(host, client, candidate.Request.ChangeSetId);
                var commit = await CommitPublic(host, client, candidate.Request.ChangeSetId, plan.Plan.PlanId);
                Assert.Equal(postCommit ? 2 : 1, commit.Commit.Generation);
                Assert.Equal(GenerationExecutionStatus.Succeeded, (await ExecutePublic(host, client, commit.Commit, true)).Status);
                var old = await client.GetResourceImportRequestAsync(new() { RequestId = firstId.ToString() });
                Assert.Equal(ImportStatus.Superseded, old.Status); Assert.Equal(correctedId.ToString(), old.Request.SupersededByRequestId);
                Assert.Equal(ImportStatus.Conflict, (await SendCreate(client, correctedId, rejected, correction)).Status);
                var confirmedCorrection = new ResourceImportCorrection { PreviousRequestId = correctedId.ToString(), Reason = ResourceImportCorrectionReason.ExplicitRetry,
                    Committed = new() { DeploymentId = commit.Commit.DeploymentId, Generation = commit.Commit.Generation } };
                Assert.Equal(ImportStatus.Conflict, (await SendCreate(client, Guid.NewGuid(), pair.Password, confirmedCorrection)).Status);
                var selection = new ConfigurationDocumentRequest { Environment = "pilot", ApplicationId = applicationName, ComponentId = "worker", DocumentId = "settings", Generation = commit.Commit.Generation };
                var document = await client.MaterializeConfigurationAsync(selection);
                Assert.Equal(Haby.Operator.Contracts.ConfigurationDocumentStatus.Created, document.Status);
                var access = await client.CreateConfigurationAccessAsync(new() { Selector = selection });
                Assert.Equal(ConfigurationAccessStatus.Created, access.Status);
                var delivered = await new ConfigurationDelivery.ConfigurationDeliveryClient(channel).DeliverAsync(new() { RevisionId = document.Artifact.RevisionId },
                    new Metadata { { "authorization", "Bearer " + access.Credential } });
                using var json = JsonDocument.Parse(delivered.Json.Memory);
                var connection = new NpgsqlConnectionStringBuilder(json.RootElement.GetProperty("Connection"u8).GetString()) { Pooling = false };
                try
                {
                    Assert.Equal(SslMode.VerifyFull, connection.SslMode);
                    if (!isolated) connection.RootCertificate = tls.RootFile;
                    Assert.True(connection.Password == pair.Password, "Delivered password differs from the supplied existing credential.");
                    await using var db = new NpgsqlConnection(connection.ConnectionString); await db.OpenAsync();
                    await using var query = new NpgsqlCommand("SELECT value FROM public.fixture_data WHERE id = 1", db);
                    Assert.Equal("retained", await query.ExecuteScalarAsync());
                }
                finally { connection.Password = null; }
                await using var verify = await host.Services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
                Assert.Equal(2, await verify.OperatorImportRequestRecords.CountAsync());
                Assert.Equal(2, await verify.ProtectedApplicationCredentials.CountAsync());
                Assert.Single(await verify.ImportSupersessions.AsNoTracking().ToArrayAsync());
                Assert.Single(await verify.ResourceImportReceipts.AsNoTracking().ToArrayAsync());
                Assert.Equal(commit.Commit.Generation, (await verify.ApplicationDeployments.AsNoTracking().SingleAsync()).AppliedGeneration);
                var receipt = await host.Services.GetRequiredService<IResourceImportAcceptance>().FindAsync(new(ApplicationDeploymentId.Parse(commit.Commit.DeploymentId),
                    DeploymentGeneration.Create(commit.Commit.Generation), OperationId.Create("app-login-access")), default);
                var observed = PostgreSqlImportObservation.Decode(receipt!.Evidence)!;
                Assert.True(observed.AdministratorTls && observed.ApplicationTls);
            }
            Assert.True(before == await Snapshot(pair), "Public import changed external identity, owner, ACL or data.");
            Assert.True(await PasswordUnchanged(passwordGuard, pair), "Public import changed the existing password verifier.");
            var serverLogs = await fixture.ReadServerLogsAsync();
            Assert.False(serverLogs.Contains(pair.Password, StringComparison.Ordinal) || serverLogs.Contains(rejected, StringComparison.Ordinal), "Public import leaked credential material into server logs.");
            var verification = serverLogs.Split('\n').Where(x => x.StartsWith(PostgreSqlImportVerifier.ApplicationName + "|" + pair.Database + "|", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(verification);
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(string.Join('\n', verification),
                @"(?:statement:|execute[^:]*:)\s*(?:CREATE|ALTER|GRANT|REVOKE|DROP|TRUNCATE|INSERT|UPDATE|DELETE)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase), "Public import verification dispatched an external mutation.");

            async Task<ResourceImportRequestReply> SendCreate(ManifestOperator.ManifestOperatorClient client, Guid id, string password, ResourceImportCorrection? correction)
            {
                var bytes = Encoding.UTF8.GetBytes(password);
                var request = new CreateResourceImportChangeSetRequest { RequestId = id.ToString(), EnvironmentRecordId = environment.ToString(), Reference = reference,
                    InputFormat = PostgreSqlImportPreparer.NamesFormat, InputJson = ByteString.CopyFromUtf8(PostgreSqlImportPreparer.Encode(new(pair.Database, pair.Login, pair.Input.Schema)).Json),
                    CredentialMaterial = ByteString.CopyFrom(bytes), Correction = correction };
                try { return await client.CreateResourceImportChangeSetAsync(request); }
                finally { Array.Clear(bytes); request.CredentialMaterial = ByteString.Empty; }
            }
            async Task<ResourceImportRequestReply> CreatePublic(WebApplication host, ManifestOperator.ManifestOperatorClient client, Guid id, string password,
                ResourceImportCorrection? correction, bool lost)
            {
                if (binary is null)
                {
                    if (lost) Assert.Equal(StatusCode.Unavailable, (await Assert.ThrowsAsync<RpcException>(() => SendCreate(client, id, password, correction))).StatusCode);
                    else return await SendCreate(client, id, password, correction);
                }
                else
                {
                    var arguments = new List<string> { "changeset", "import", "create", reference.ApplicationRecordId, reference.RevisionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "--input", inputPath, "--environment", "pilot", "--request-id", id.ToString(), "--password-stdin" };
                    if (correction is not null)
                    {
                        arguments.AddRange(["--corrects-request", correction.PreviousRequestId, "--correction-reason", "credential-rejected"]);
                        if (correction.ExpectedAbsence) arguments.Add("--expected-uncommitted");
                        else arguments.AddRange(["--expected-deployment", correction.Committed.DeploymentId, "--expected-generation", correction.Committed.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                    }
                    using var result = await Cli(host, arguments, password, lost ? 3 : 0);
                    Assert.Equal(id.ToString(), result.RootElement.GetProperty("data"u8).GetProperty("requestId"u8).GetString());
                    if (lost) Assert.Equal("uncertain", result.RootElement.GetProperty("outcome"u8).GetString());
                    using var get = await Cli(host, ["changeset", "import", "get", id.ToString()], null, 0);
                    Assert.Equal(id.ToString(), get.RootElement.GetProperty("data"u8).GetProperty("requestId"u8).GetString());
                }
                return await client.GetResourceImportRequestAsync(new() { RequestId = id.ToString() });
            }
            async Task<PlanningReply> PlanPublic(WebApplication host, ManifestOperator.ManifestOperatorClient client, string change)
            {
                PlanningReply plan;
                if (binary is not null)
                {
                    using var result = await Cli(host, ["plan", "create", change], null, 0);
                    plan = await client.GetPlanAsync(new() { PlanId = result.RootElement.GetProperty("data"u8).GetProperty("plan"u8).GetProperty("planId"u8).GetString()! });
                    using var check = await Cli(host, ["plan", "check", plan.Plan.PlanId], null, 0);
                }
                else plan = await client.CreatePlanAsync(new() { ChangeSetId = change });
                Assert.True(plan.Status is PlanningStatus.Planned or PlanningStatus.Current);
                Assert.Equal(PlanningStatus.Current, (await client.CheckPlanAsync(new() { PlanId = plan.Plan.PlanId })).Status);
                return plan;
            }
            async Task<CommitReply> CommitPublic(WebApplication host, ManifestOperator.ManifestOperatorClient client, string change, string plan)
            {
                if (binary is not null) { using var result = await Cli(host, ["changeset", "commit", change, "--plan", plan], null, 0); }
                else Assert.Equal(CommitStatus.Committed, (await client.CommitDesiredStateAsync(new() { ChangeSetId = change, PlanId = plan })).Status);
                return await client.GetCommitAsync(new() { ChangeSetId = change });
            }
            async Task<GenerationExecutionReply> ExecutePublic(WebApplication host, ManifestOperator.ManifestOperatorClient client, DurableCommitSummary commit, bool success)
            {
                if (binary is not null)
                { using var result = await Cli(host, ["deployment", "generation", "execute", commit.DeploymentId, commit.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)], null, success ? 0 : 1); }
                return await client.ExecuteGenerationAsync(new() { DeploymentId = commit.DeploymentId, Generation = commit.Generation });
            }
            async Task<JsonDocument> Cli(WebApplication host, List<string> arguments, string? password, int expected)
            {
                await OperatorTestCredentials.WriteConfigAsync(configPath, host.Urls.Single());
                var start = new ProcessStartInfo(binary!) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                start.ArgumentList.Add("--config"); start.ArgumentList.Add(configPath); start.ArgumentList.Add("-o"); start.ArgumentList.Add("json");
                start.Environment.Remove("HABY_OPERATOR_SKIP_TLS_VALIDATION"); start.Environment.Remove("HABYCTL_CONFIG");
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                if (password is not null)
                {
                    var bytes = Encoding.UTF8.GetBytes(password);
                    try { await process.StandardInput.BaseStream.WriteAsync(bytes); }
                    finally { Array.Clear(bytes); }
                }
                process.StandardInput.Close();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await process.WaitForExitAsync(deadline.Token);
                var text = await stdout; var error = await stderr;
                Assert.False((text + error).Contains(pair.Password, StringComparison.Ordinal) || (text + error).Contains(rejected, StringComparison.Ordinal), "CLI disclosed password material.");
                Assert.Equal(expected, process.ExitCode);
                return JsonDocument.Parse(text);
            }
        }
        finally
        {
            foreach (var root in ownedRoots)
            {
                roots.Remove(root);
                Assert.Empty(roots.Certificates.Find(X509FindType.FindByThumbprint, root.Thumbprint, false));
            }
        }
    }

    private async Task<EnvironmentRecordId> SeedPublicImportProvider(string catalog, PostgreSqlProviderTransport? transport)
    {
        await using var services = Services(catalog, transport: transport);
        await services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        var secrets = services.GetRequiredService<ISecretStore>();
        var environment = await secrets.CreateEnvironmentAsync(EnvironmentId.Create("pilot"), default);
        var secret = new SecretRef(SecretId.New(), SecretVersionId.New());
        var control = new NpgsqlConnectionStringBuilder(Control());
        var bytes = Encoding.UTF8.GetBytes(control.Password!);
        try { await secrets.CreateAsync(new(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator), secret, bytes, default); }
        finally { Array.Clear(bytes); control.Password = null; }
        var configuration = new PostgreSqlProviderConfiguration(PostgreSqlProviderConfiguration.CurrentShapeVersion, "127.0.0.1", control.Port,
            "postgres", "postgres", secret, PostgreSqlDatabaseCheckpointCapability.ImplementationId, PostgreSqlTransportMode.VerifyFull);
        var providers = services.GetRequiredService<IProviderConfigurationStore>();
        var instance = await providers.CreateInstanceAsync(environment, configuration, default);
        await providers.AddRevisionAsync(environment, instance, ProviderConfigurationRevisionNumber.Create(1), configuration, default);
        Assert.Equal(ProviderAliasChangeOutcome.Changed, (await services.GetRequiredService<IProviderAliasStore>().ChangeAsync(environment, ProviderAlias.Create("postgresql.primary"),
            null, new(instance, ProviderConfigurationRevisionNumber.Create(1)), "fixture", default)).Outcome);
        return environment;
    }

    private static async Task<WebApplication> StartPublicImportHost(string catalog, string path, PostgreSqlProviderTransport? transport, LosePublicImportResponse? loss = null)
    {
        var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + catalog, "--Haby:Tls:Enabled=true", "--Haby:Tls:CertificatePath=" + path,
            "--Haby:Tls:CertificatePassword=test-only", "--Haby:Port=0", "--environment=Production"], services =>
            {
                if (transport is not null) services.AddSingleton(transport);
                if (loss is not null) { services.AddSingleton(loss); services.AddGrpc(options => options.Interceptors.Add<LosePublicImportResponse>()); }
            });
        await host.Services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        return host;
    }

    private static GrpcChannel PublicImportChannel(WebApplication host, X509Certificate2 root, bool systemTrust)
    {
        if (systemTrust) return GrpcChannel.ForAddress(host.Urls.Single());
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(root);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(certificate);
            }
        };
        return GrpcChannel.ForAddress(host.Urls.Single(), new GrpcChannelOptions { HttpHandler = handler });
    }

    private sealed class LosePublicImportResponse(Guid request) : Interceptor
    {
        internal bool Thrown;
        public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest message, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
        {
            var response = await continuation(message, context);
            if (!Thrown && message is CreateResourceImportChangeSetRequest create && create.RequestId == request.ToString())
            { Thrown = true; throw new RpcException(new Status(StatusCode.Unavailable, "Injected lost import response.")); }
            return response;
        }
    }

}
