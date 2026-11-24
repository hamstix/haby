using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Application;
using Haby.Configuration.Contracts;
using Haby.Domain;
using Haby.Manifest;
using Haby.Operator.Host;
using Haby.Persistence.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class SharedReferenceDeliveryTests
{
    private static async Task<WebApplication> DeliveryHost(Scenario scenario, InternalDurableActions actions)
    {
        var rendering = new Rendering(scenario.Boundary);
        var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=" + scenario.Connection,
            "--Haby:Port=0", "--Haby:Tls:Enabled=false"], services =>
        {
            services.AddSingleton(scenario.Boundary).AddSingleton<Handler>()
                .AddSingleton<IDurableActionAdmissionHandler>(p => p.GetRequiredService<Handler>())
                .AddSingleton<IDurableActionHandler>(p => p.GetRequiredService<Handler>())
                .AddSingleton<IExternalOperationOutputContract, Evidence>();
            services.AddSingleton(new ResourceTypeDefinition(Type, "{\"type\":\"object\",\"additionalProperties\":false}", OptionsSchema));
            services.AddSingleton(new OutputContractDefinition(OutputContractId.Create(Output.Contract), OptionsSchema));
            services.AddSingleton<ConfigurationRendererContract>(new Definition());
            services.AddSingleton<ConfigurationFormatterContract>(new FormatDefinition());
            services.AddSingleton(new ConfigurationFormatterImplementation(Implementation, FormatterId));
            services.AddSingleton<IConfigurationRenderer>(rendering).AddSingleton<IConfigurationFormatter>(rendering)
                .AddSingleton<IConfigurationResolutionBinding>(rendering);
            services.RemoveAll<IRuntimeAuthenticator>();
            services.AddSingleton<IRuntimeAuthenticator, ExactRuntimeIdentity>();
            // Real catalog origin verification, with the same closed test-only identity I/O backend.
            services.AddSingleton<IRuntimeIdentityStore>(p => new PostgreSqlRuntimeIdentityStore(p.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>(), actions));
            services.AddDbContextFactory<ManifestCatalogDbContext>(o => o.AddInterceptors(scenario.Boundary));
        });
        try { await OperatorTestCredentials.StartWithIssuedOperatorAsync(host); return host; }
        catch { await host.DisposeAsync(); throw; }
    }
    private static async Task RpcDelivery(WebApplication host, Scenario scenario, ReadyApplication baseline,
        RuntimePublicationRequest[] requests, bool allowed)
    {
        using var channel = GrpcChannel.ForAddress(host.Urls.Single());
        var client = new ConfigurationDelivery.ConfigurationDeliveryClient(channel);
        var operatorClient = OperatorTestCredentials.Client(channel);
        scenario.Boundary.ResetMaterial();
        var metadata = await operatorClient.GetSharedCredentialBindingsAsync(new()
        { Environment = "pilot", ProviderInstanceId = scenario.Provider.ToString(), Revision = 1 });
        Assert.Equal(4, metadata.Bindings.Single(x => x.Use == "ApplicationDelivery").Dependencies.Count);
        Assert.Equal(0, scenario.Boundary.PayloadQueries);
        foreach (var request in requests)
        {
            var component = request.Origin!.Component;
            var grant = baseline.Grants[component];
            var document = new DeliverConfigurationRequest { RevisionId = grant.Grant.Document.Id.ToString() };
            var runtime = new DeliverRuntimeConfigurationRequest { BindingId = request.Binding.ToString(), RevisionId = request.Revision!.Value.ToString() };
            var staticHeaders = new Metadata { { "authorization", "Bearer " + grant.Credential } };
            var runtimeHeaders = new Metadata { { "authorization", "Bearer " + request.Binding.ToString("N") } };
            scenario.Boundary.ResetMaterial();
            Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(async () =>
                await client.DeliverAsync(document, OperatorTestCredentials.Headers(host.Urls.Single())))).StatusCode);
            Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(async () =>
                await client.DeliverRuntimeAsync(runtime, OperatorTestCredentials.Headers(host.Urls.Single())))).StatusCode);
            Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(async () =>
                await client.DeliverRuntimeAsync(runtime, staticHeaders))).StatusCode);
            Assert.Equal(0, scenario.Boundary.PayloadQueries);
            if (allowed)
            {
                var delivered = await client.DeliverAsync(document, staticHeaders);
                Assert.Equal(1, scenario.Boundary.MaterialReads); Assert.Equal(2, scenario.Boundary.PayloadQueries);
                using (var json = JsonDocument.Parse(delivered.Json.Memory))
                    Assert.Equal("shared-application-test-material", json.RootElement.GetProperty("Access"u8).GetProperty("Password"u8).GetString());
                scenario.Boundary.ResetMaterial();
                var live = await client.DeliverRuntimeAsync(runtime, runtimeHeaders);
                Assert.Equal(1, scenario.Boundary.MaterialReads); Assert.Equal(2, scenario.Boundary.PayloadQueries);
                using var runtimeJson = JsonDocument.Parse(live.Json.Memory);
                Assert.Equal("shared-application-test-material", runtimeJson.RootElement.GetProperty("Access"u8).GetProperty("Password"u8).GetString());
                scenario.Boundary.ResetMaterial();
                var wrong = requests.Single(x => x.Binding != request.Binding);
                Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(async () =>
                    await client.DeliverRuntimeAsync(new() { BindingId = runtime.BindingId, RevisionId = wrong.Revision!.Value.ToString() }, runtimeHeaders))).StatusCode);
                Assert.Equal(0, scenario.Boundary.PayloadQueries);
            }
            else
            {
                Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(async () => await client.DeliverAsync(document, staticHeaders))).StatusCode);
                Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(async () => await client.DeliverRuntimeAsync(runtime, runtimeHeaders))).StatusCode);
                Assert.Equal(0, scenario.Boundary.PayloadQueries);
            }
        }
    }
}
