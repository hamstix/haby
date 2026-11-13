using System.Diagnostics;
using System.Net;
using System.Text;
using Google.Protobuf;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Domain;
using Haby.Kubernetes;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using k8s;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Kubernetes_declarations_preserve_exact_retries_identity_confirmation_alias_CAS_and_restart(bool certificate)
    {
        var connection = await NewDatabaseAsync();
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        var evidence = new KubernetesReviewEvidence();
        string instance; EnvironmentRecordId environment;
        await using (var host = OperatorHostApplication.Build(args, s => ReplaceKubernetesTransport(s, evidence)))
        {
            await host.Services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync();
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = GrpcChannel.ForAddress(host.Urls.Single());
            var client = OperatorTestCredentials.Client(channel);
            await client.CreateEnvironmentAsync(new() { Name = "pilot" });
            await client.CreateEnvironmentAsync(new() { Name = "other" });
            foreach (var name in new[] { "token", "certificate", "private-key" })
                await client.CreateSecretAsync(new() { Environment = "pilot", Name = name, RequestId = Guid.NewGuid().ToString(), Material = ByteString.CopyFromUtf8("fixture-" + name) });
            async Task<ProviderReply> Apply(string? json = null, bool confirm = false, long baseline = 0)
            {
                var request = new ApplyProviderRequest { Declaration = ByteString.CopyFromUtf8(json ?? KubernetesDeclaration(certificate)), ConfirmLogicalIdentity = confirm };
                if (confirm) request.ExpectedPreviousRevision = baseline;
                return await client.ApplyProviderAsync(request);
            }
            foreach (var malformed in new[] {
                KubernetesDeclaration(certificate).Replace("\"endpoint\"", "\"namespace\"", StringComparison.Ordinal),
                KubernetesDeclaration(certificate).Replace("\"endpoint\":\"https://127.0.0.1:1\"", "\"endpoint\":null", StringComparison.Ordinal),
                KubernetesDeclaration(certificate).Replace("\"version\":1", "\"version\":0", StringComparison.Ordinal) })
                Assert.Equal(ProviderStatus.InvalidRequest, (await Apply(malformed)).Status);
            var first = await Apply();
            Assert.Equal(ProviderStatus.Created, first.Status); instance = first.Provider.InstanceId;
            environment = EnvironmentRecordId.Parse(first.Provider.EnvironmentRecordId);
            Assert.Equal(ProviderStatus.ExistingEquivalent, (await Apply()).Status);
            var race = await Task.WhenAll(Apply(), Apply());
            Assert.All(race, r => Assert.Equal(ProviderStatus.ExistingEquivalent, r.Status));
            Assert.Equal(ProviderStatus.RevisionContentConflict, (await Apply(KubernetesDeclaration(certificate, endpoint: "https://changed.example"))).Status);
            Assert.Equal(ProviderStatus.IdentityConfirmationRequired, (await Apply(KubernetesDeclaration(certificate, 2, "https://changed.example"))).Status);
            Assert.Equal(ProviderStatus.Created, (await Apply(KubernetesDeclaration(certificate, 2, "https://changed.example"), true, 1)).Status);
            Assert.Equal(ProviderStatus.StaleBaseline, (await Apply(KubernetesDeclaration(certificate, 3, "https://third.example"), true, 1)).Status);
            Assert.Equal(ProviderStatus.SecretReferenceUnavailable, (await Apply(KubernetesDeclaration(certificate).Replace("\"version\":1", "\"version\":9", StringComparison.Ordinal))).Status);
            Assert.Equal(ProviderStatus.SecretReferenceUnavailable, (await Apply(KubernetesDeclaration(certificate).Replace("\"environment\":\"pilot\"", "\"environment\":\"other\"", StringComparison.Ordinal))).Status);
            var check = await client.CheckProviderAsync(new() { Environment = "pilot", Name = "cluster", Revision = 1 });
            Assert.Equal(ProviderPreflightStatus.Ready, check.Preflight);
            SetProviderAliasRequest Set(long? expected, long target)
            {
                var request = new SetProviderAliasRequest { Environment = "pilot", Alias = "kubernetes.primary", Target = new() { Name = "cluster", Revision = target } };
                if (expected is { } revision) request.Expected = new() { Name = "cluster", Revision = revision };
                else request.ExpectedAbsent = true;
                return request;
            }
            var calls = evidence.Calls;
            Assert.Equal(ProviderAccessStatus.Changed, (await client.SetProviderAliasAsync(Set(null, 1))).Status);
            Assert.Equal(calls + 1, evidence.Calls);
            evidence.Status = 401;
            Assert.Equal(ProviderPreflightStatus.AuthenticationFailed, (await client.SetProviderAliasAsync(Set(1, 2))).Preflight);
            await using (var verify = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options))
            {
                Assert.Equal(1, (await verify.ProviderAliases.AsNoTracking().SingleAsync()).RevisionNumber);
                Assert.Single(await verify.ProviderAliasAudits.AsNoTracking().ToArrayAsync());
            }
            evidence.Status = 201;
            Assert.Equal(ProviderAccessStatus.Unchanged, (await client.SetProviderAliasAsync(Set(1, 1))).Status);
            Assert.Equal(ProviderAccessStatus.StaleExpectation, (await client.SetProviderAliasAsync(Set(null, 2))).Status);
            var switches = await Task.WhenAll(client.SetProviderAliasAsync(Set(1, 2)).ResponseAsync, client.SetProviderAliasAsync(Set(1, 2)).ResponseAsync);
            Assert.Single(switches, r => r.Status == ProviderAccessStatus.Changed);
            Assert.Single(switches, r => r.Status == ProviderAccessStatus.StaleExpectation);
            await KubernetesNative(host.Urls.Single(), KubernetesDeclaration(certificate));
            await host.StopAsync();
        }
        await using (var restarted = OperatorHostApplication.Build(args))
        {
            // Real production registrations reload exact payloads; no fixture check can authorize this alias.
            var codec = restarted.Services.GetRequiredService<KubernetesProviderConfigurationCodec>();
            var declaration = restarted.Services.GetServices<IProviderDeclarationAdapter>().Single(a => a.Key.ProviderKind == codec.ProviderKindId);
            Assert.Same(codec, declaration.Codec);
            var handler = restarted.Services.GetRequiredService<ProviderPreflightRegistry>().Find(declaration.Key);
            Assert.IsType<KubernetesPreflightHandler>(handler); Assert.Same(codec, Assert.Single(handler.Readers));
            Assert.Empty(restarted.Services.GetServices<IInternalActionHandler>());
            var store = restarted.Services.GetRequiredService<IProviderConfigurationStore>();
            var pinned = await store.FindRevisionAsync<KubernetesProviderConfiguration>(environment, ProviderInstanceId.Parse(instance), ProviderConfigurationRevisionNumber.Create(1), default);
            Assert.Equal("https://127.0.0.1:1/", pinned!.Configuration.Endpoint.AbsoluteUri);
            Assert.Equal(certificate, pinned.Configuration.ClientCertificate is not null);
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(restarted);
            using var channel = GrpcChannel.ForAddress(restarted.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            Assert.Equal(instance, (await client.GetProviderAsync(new() { Environment = "pilot", Name = "cluster", Revision = 1 })).Provider.InstanceId);
            Assert.Equal(2, (await client.GetProviderAliasAsync(new() { Environment = "pilot", Alias = "kubernetes.primary" })).Binding.Revision);
            Assert.NotEqual(ProviderPreflightStatus.Ready, (await client.CheckProviderAsync(new() { Environment = "pilot", Name = "cluster", Revision = 1 })).Preflight);
            await restarted.StopAsync();
        }
        await using (var unsupported = OperatorHostApplication.Build(args, services =>
        {
            foreach (var registration in services.Where(s => s.ImplementationType == typeof(KubernetesPreflightHandler) ||
                s.ImplementationType == typeof(KubernetesProviderDeclarationAdapter)).ToArray()) services.Remove(registration);
        }))
        {
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(unsupported);
            using var channel = GrpcChannel.ForAddress(unsupported.Urls.Single()); var client = OperatorTestCredentials.Client(channel);
            Assert.Equal(ProviderStatus.Found, (await client.GetProviderAsync(new() { Environment = "pilot", Name = "cluster", Revision = 1 })).Status);
            Assert.Equal(ProviderPreflightStatus.HandlerUnavailable, (await client.CheckProviderAsync(new() { Environment = "pilot", Name = "cluster", Revision = 1 })).Preflight);
            await unsupported.StopAsync();
        }
        await using var final = new ManifestCatalogDbContext(new DbContextOptionsBuilder<ManifestCatalogDbContext>().UseNpgsql(connection).Options);
        Assert.Equal(2, await final.ProviderConfigurationRevisions.CountAsync());
        Assert.Equal(2, await final.ProviderAliasAudits.CountAsync());
        Assert.Single(await final.ProviderConfigurationRevisions.AsNoTracking().Where(r => r.ConfirmedPreviousRevision == 1).ToArrayAsync());
    }

    private static string KubernetesDeclaration(bool certificate, int revision = 1, string endpoint = "https://127.0.0.1:1")
    {
        var refs = certificate ? "\"client-certificate\":{\"secretName\":\"certificate\",\"version\":1},\"client-private-key\":{\"secretName\":\"private-key\",\"version\":1}" : "\"bearer-token\":{\"secretName\":\"token\",\"version\":1}";
        return $$$$"""
            {"apiVersion":"haby.dev/v1alpha1","kind":"Provider","metadata":{"name":"cluster","environment":"pilot"},
            "spec":{"revision":{{{{revision}}}},"providerKind":"kubernetes/v1","configurationContract":"haby.kubernetes.connection/v1",
            "implementationId":"haby.kubernetes/v1","properties":{"endpoint":"{{{{endpoint}}}}"},"secretRefs":{ {{{{refs}}}}}}}
            """;
    }

    private sealed class KubernetesReviewEvidence { internal int Calls; internal int Status = 201; }
    private static void ReplaceKubernetesTransport(IServiceCollection services, KubernetesReviewEvidence evidence)
    {
        var registration = services.Single(s => s.ServiceType == typeof(IProviderPreflightHandler) && s.ImplementationType == typeof(KubernetesPreflightHandler));
        services.Remove(registration);
        services.AddSingleton<IProviderPreflightHandler>(p => new KubernetesPreflightHandler(p.GetRequiredService<KubernetesProviderConfigurationCodec>(), async (environment, configuration, token) =>
        {
            // Catalog/Host fixture only. Real authentication and strict production TLS are qualified separately.
            var prepared = p.GetRequiredService<KubernetesProviderConfigurationCodec>().Encode(configuration);
            foreach (var reference in prepared.SecretReferences)
            {
                using var material = await p.GetRequiredService<ISecretStore>().ResolveBoundedAsync(new(new SecretOwner.Environment(environment), SecretPurpose.ProviderAdministrator), reference.Reference, 1_048_576, token);
                Assert.NotNull(material);
            }
            return new KernelClient(new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "https://fixture.invalid" }, new KubernetesReviewTransport(evidence)));
        }));
    }
    private sealed class KubernetesReviewTransport(KubernetesReviewEvidence evidence) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref evidence.Calls);
            Assert.Equal("/apis/authentication.k8s.io/v1/selfsubjectreviews", request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)evidence.Status) { RequestMessage = request, Content = new StringContent("""{"apiVersion":"authentication.k8s.io/v1","kind":"SelfSubjectReview","status":{"userInfo":{"username":"fixture-operator","groups":["system:authenticated"]}}} """, Encoding.UTF8, "application/json") });
        }
    }
    private static async Task KubernetesNative(string endpoint, string declaration)
    {
        var binary = Environment.GetEnvironmentVariable("HABY_AOT_CLI_PATH");
        if (string.IsNullOrEmpty(binary)) { Assert.NotEqual("1", Environment.GetEnvironmentVariable("HABY_AOT_CLI_REQUIRED")); return; }
        var file = Path.GetTempFileName(); var config = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, declaration); await OperatorTestCredentials.WriteConfigAsync(config, endpoint);
            foreach (var command in new[] { new[] { "provider", "apply", "--file", file }, new[] { "provider", "check", "cluster", "--revision", "1", "--environment", "pilot" },
                new[] { "provider", "alias", "get", "kubernetes.primary", "--environment", "pilot" } })
            {
                var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                start.Environment["HABY_OPERATOR_URL"] = endpoint;
                foreach (var argument in command.Concat(["--config", config, "-o", "json"])) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(deadline.Token); } catch { process.Kill(true); throw; }
                Assert.Equal(0, process.ExitCode); Assert.Empty(await stderr); Assert.DoesNotContain("fixture-", await stdout);
            }
        }
        finally { File.Delete(file); File.Delete(config); }
    }
}
