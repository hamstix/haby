using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Haby.Operator.Contracts;
using Haby.Operator.Host;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class OperatorHostIntegrationTests
{
    [Fact]
    public async Task Shared_credential_protected_intake_preserves_exact_retry_and_purpose_boundary()
    {
        var connection = await NewDatabaseAsync();
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        Haby.Operator.Contracts.SecretMetadata original;
        var request = new CreateSecretRequest
        {
            Environment = "shared-pilot",
            Name = "shared-login",
            Purpose = "SharedApplicationCredential",
            RequestId = Guid.NewGuid().ToString(),
            Material = ByteString.CopyFromUtf8("protected-test-material")
        };
        await using (var host = await OperatorHostApplication.BuildMigratedAsync(args))
        {
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(host.Urls.Single());
            var client = OperatorTestCredentials.Client(channel);
            await client.CreateEnvironmentAsync(new() { Name = request.Environment });
            var created = await client.CreateSecretAsync(request);
            Assert.Equal(SecretStatus.Created, created.Status);
            original = created.Secret;
            Assert.Equal("SharedApplicationCredential", original.Purpose);
            Assert.Single(original.Versions);
            await host.StopAsync();
        }
        await using (var host = await OperatorHostApplication.BuildMigratedAsync(args))
        {
            await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
            using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(host.Urls.Single());
            var client = OperatorTestCredentials.Client(channel);
            var retry = await client.CreateSecretAsync(request);
            Assert.Equal(SecretStatus.ExistingEquivalent, retry.Status);
            Assert.Equal(original, retry.Secret);
            var changed = request.Clone();
            changed.Purpose = "ProviderAdministrator";
            Assert.Equal(SecretStatus.RequestContentConflict, (await client.CreateSecretAsync(changed)).Status);
            changed = request.Clone();
            changed.Material = ByteString.CopyFromUtf8("changed-test-material");
            Assert.Equal(SecretStatus.RequestContentConflict, (await client.CreateSecretAsync(changed)).Status);
            Assert.Equal(original, (await client.GetSecretAsync(new() { Environment = request.Environment, Name = request.Name })).Secret);
            Assert.Equal(original, Assert.Single((await client.ListSecretsAsync(new() { Environment = request.Environment })).Items));
            await host.StopAsync();
        }
    }
    [Fact]
    public async Task Shared_binding_operator_RPC_requires_admin_and_exposes_only_safe_exact_scope_and_audit()
    {
        var connection = await NewDatabaseAsync();
        var args = new[] { "--Haby:PostgreSql:ConnectionString=" + connection, "--Haby:Tls:Enabled=false", "--Haby:Port=0" };
        await using var host = await OperatorHostApplication.BuildMigratedAsync(args);
        await OperatorTestCredentials.StartWithIssuedOperatorAsync(host);
        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(host.Urls.Single());
        var client = OperatorTestCredentials.Client(channel);
        await client.CreateEnvironmentAsync(new() { Name = "pilot" });
        foreach (var item in new[] { ("admin", "ProviderAdministrator"), ("shared", "SharedApplicationCredential") })
            Assert.Equal(SecretStatus.Created, (await client.CreateSecretAsync(new()
            {
                Environment = "pilot",
                Name = item.Item1,
                Purpose = item.Item2,
                RequestId = Guid.NewGuid().ToString(),
                Material = ByteString.CopyFromUtf8("protected-rpc-test-value")
            })).Status);
        var declaration = ProviderDeclarationTests.Declaration.Replace("\"secretRefs\":{",
            "\"secretRefs\":{\"system-access-credential\":{\"secretName\":\"shared\",\"version\":1},\"application-credential\":{\"secretName\":\"shared\",\"version\":1},", StringComparison.Ordinal);
        var provider = await client.ApplyProviderAsync(new() { Declaration = ByteString.CopyFrom(Encoding.UTF8.GetBytes(declaration)) });
        Assert.Equal(ProviderStatus.Created, provider.Status);
        var request = new SharedCredentialRequest { Environment = "pilot", ProviderInstanceId = provider.Provider.InstanceId, Revision = 1 };
        var anonymous = new ManifestOperator.ManifestOperatorClient(channel);
        Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(async () => await anonymous.GetSharedCredentialBindingsAsync(request))).StatusCode);
        Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(async () => await anonymous.RevokeSharedCredentialUseAsync(request))).StatusCode);
        var inspected = await client.GetSharedCredentialBindingsAsync(request);
        Assert.Equal("Listed", inspected.Status);
        Assert.Equal(2, inspected.Bindings.Count);
        var binding = Assert.Single(inspected.Bindings, x => x.Use == "SystemAccess");
        Assert.Equal(provider.Provider.InstanceId, binding.ProviderInstanceId);
        Assert.False(string.IsNullOrWhiteSpace(binding.IssuedBy));
        Assert.True(DateTimeOffset.TryParse(binding.IssuedAtUtc, out _));
        Assert.DoesNotContain("protected-rpc-test-value", inspected.ToString());
        request.Slot = binding.Slot; request.Use = binding.Use; request.SecretId = binding.SecretId; request.SecretVersionId = binding.SecretVersionId;
        Assert.Equal("Revoked", (await client.RevokeSharedCredentialUseAsync(request)).Status);
        Assert.Equal("AlreadyRevoked", (await client.RevokeSharedCredentialUseAsync(request)).Status);
        var after = await client.GetSharedCredentialBindingsAsync(request);
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(after.Bindings, x => x.Use == "SystemAccess").RevokedBy));
        Assert.False(Assert.Single(after.Bindings, x => x.Use == "ApplicationDelivery").HasRevokedBy);
        Assert.False(Assert.Single(after.Bindings, x => x.Use == "ApplicationDelivery").HasRevokedAtUtc);
        request.Use = "invalid";
        Assert.Equal("InvalidRequest", (await client.RevokeSharedCredentialUseAsync(request)).Status);
        await host.StopAsync();
    }

}
