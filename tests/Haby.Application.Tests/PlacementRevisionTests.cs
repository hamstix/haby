using System.Text;
using Haby.Application;
using Haby.Kubernetes.Contracts;
using Haby.Tests;
using Xunit;

namespace Haby.Application.Tests;

public sealed class PlacementRevisionTests
{
    private readonly KubernetesPlacementCodec codec = new();
    [Theory]
    [InlineData(PlacementSamples.Full)]
    [InlineData(PlacementSamples.Minimal)]
    public void Codec_detaches_preserves_absence_and_reconstructs_every_typed_field(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var accepted = codec.Decode(bytes);
        Array.Fill(bytes, (byte)0);
        Assert.Equal(accepted, codec.Read(accepted.Contract, accepted.FormatVersion, accepted.Payload));
        var typed = KubernetesPlacementCodec.ToTyped(accepted);
        Assert.Equal("standard-runtime", typed.Reference.Name.Value);
        Assert.Equal(1, typed.Reference.Revision);
        Assert.Equal("apps", typed.Namespace);
        if (source == PlacementSamples.Minimal)
        {
            Assert.Null(typed.Settings.Replicas); Assert.Null(typed.Settings.Strategy);
            Assert.Empty(typed.NodeSelector); Assert.Empty(typed.ImagePullSecrets);
            Assert.DoesNotContain("replicas", accepted.Payload);
        }
        else
        {
            Assert.Equal("registry.example.test:5000", typed.Registry); Assert.Equal("product/team", typed.RepositoryPrefix);
            Assert.Equal("apps", typed.NodeSelector["example.test/pool"]); Assert.Equal(new[] { "registry-credentials", "second" }, typed.ImagePullSecrets);
            Assert.Equal(3, typed.Settings.Replicas); Assert.Equal(new RolloutStrategy("RollingUpdate", new(25, true), new(0, false)), typed.Settings.Strategy);
            Assert.Equal(10, typed.Settings.MinReadySeconds); Assert.Equal(900, typed.Settings.RolloutSeconds);
            Assert.Equal(new ResourceRequirements(new("250m", "128Mi"), new("1", "1Gi")), typed.Settings.Resources);
            Assert.True(typed.Settings.Service); Assert.True(typed.Settings.HeadlessService);
            Assert.Equal(new IngressPolicy(true, "nginx", "example.test", "existing-tls"), typed.Settings.Ingress);
        }
    }

    [Fact]
    public void Equality_ignores_order_whitespace_and_exact_integer_lexemes_but_preserves_declared_absence()
    {
        var equivalent = """{"spec":{"namespace":"apps"},"metadata":{"revision":1.00e0,"name":"standard-runtime"},"kind":"KubernetesPlacement","apiVersion":"haby.dev/v1alpha1"}""";
        Assert.Equal(codec.Decode(Encoding.UTF8.GetBytes(PlacementSamples.Minimal)), codec.Decode(Encoding.UTF8.GetBytes(equivalent)));
        Assert.NotEqual(codec.Decode(Encoding.UTF8.GetBytes(PlacementSamples.Minimal)), codec.Decode(Encoding.UTF8.GetBytes(PlacementSamples.Minimal.Replace("\"namespace\":", "\"replicas\":1,\"namespace\":"))));
    }

    [Fact]
    public void Maximum_positive_revision_retains_exact_integer_precision()
    {
        var source = PlacementSamples.Minimal.Replace("\"revision\":1", "\"revision\":2147483647.0e0");
        var accepted = codec.Decode(Encoding.UTF8.GetBytes(source));
        Assert.Equal(int.MaxValue, accepted.Revision);
        Assert.Contains("\"revision\":2147483647", accepted.Payload);
        Assert.Equal(int.MaxValue, KubernetesPlacementCodec.ToTyped(accepted).Reference.Revision);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{bad")]
    [InlineData("{\"private-marker\":null}")]
    public void Invalid_content_has_fixed_safe_diagnostics(string source)
    {
        var error = Assert.Throws<PlacementDeclarationException>(() => codec.Decode(Encoding.UTF8.GetBytes(source)));
        Assert.StartsWith("contract.", error.Code); Assert.DoesNotContain("private-marker", error.ToString());
    }

    [Fact]
    public void Closed_schema_limits_duplicate_null_unknown_name_revision_and_semantic_refusal()
    {
        foreach (var source in new[] {
            PlacementSamples.Minimal.Replace("\"namespace\":\"apps\"", "\"namespace\":null"),
            PlacementSamples.Minimal.Replace("\"namespace\":", "\"namespace\":\"duplicate\",\"namespace\":"),
            PlacementSamples.Minimal.Replace("\"namespace\":", "\"private-marker\":true,\"namespace\":"),
            PlacementSamples.Minimal.Replace("standard-runtime", "BAD"), PlacementSamples.Minimal.Replace("\"revision\":1", "\"revision\":0"),
            PlacementSamples.Minimal.Replace("\"revision\":1", "\"revision\":2147483648"),
            PlacementSamples.Full.Replace("\"cpu\":\"1\"", "\"cpu\":\"100m\""),
            new string('[', 65) + "0" + new string(']',65), new string(' ', 1048577) })
            Assert.Throws<PlacementDeclarationException>(() => codec.Decode(Encoding.UTF8.GetBytes(source)));
        var invalidUtf8 = Encoding.UTF8.GetBytes(PlacementSamples.Minimal.Replace("apps", "MARK")).ToArray();
        invalidUtf8[Array.IndexOf(invalidUtf8, (byte)'M')] = 0xff;
        Assert.Throws<PlacementDeclarationException>(() => codec.Decode(invalidUtf8));
    }

    [Theory]
    [InlineData("kubernetes.placement/v2", 1, PlacementSamples.Minimal)]
    [InlineData(KubernetesPlacementCodec.Contract, 2, PlacementSamples.Minimal)]
    [InlineData(KubernetesPlacementCodec.Contract, 1, "private-corrupt-marker")]
    public void Unsupported_or_corrupt_storage_never_falls_back(string contract, int version, string payload)
    {
        var error = Assert.Throws<InvalidDataException>(() => codec.Read(contract, version, payload));
        Assert.Equal("Stored Placement revision is unavailable.", error.Message);
    }
}
