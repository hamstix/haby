using System.Net;
using System.Net.Http.Headers;
using Grpc.Net.Client;
using Haby.Operator.Contracts;
using Haby.PostgreSql.IntegrationTests;

internal static class ConfiguredOperatorClientControls
{
    private const string Credential = "configured-fixture-credential-sentinel";
    private sealed class Capture : HttpMessageHandler
    {
        internal bool Matched;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Matched = request.RequestUri?.AbsoluteUri == "https://127.0.0.1:43219/haby.operator.v1alpha1.ManifestOperator/SubmitGeneration" &&
                request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == Credential;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Version = HttpVersion.Version20, Content = new ByteArrayContent(new byte[5]) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
            response.TrailingHeaders.Add("grpc-status", "0");
            return Task.FromResult(response);
        }
    }
    internal static async Task Run()
    {
        var endpoint = ConfiguredDeploymentCases.LocalOperatorEndpoint("https://0.0.0.0:43219/");
        if (endpoint.AbsoluteUri != "https://127.0.0.1:43219/")
            throw new InvalidOperationException("Local operator destination control failed.");
        using var capture = new Capture();
        using var channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpHandler = capture });
        var client = OperatorTestCredentials.Client(channel, Credential);
        await client.SubmitGenerationAsync(new GenerationRequest());
        if (!capture.Matched) throw new InvalidOperationException("Explicit RPC destination/credential association failed.");

        // The actual CLI helper's writer must use this explicit credential, with no cache entry for loopback.
        var config = Path.Combine(Path.GetTempPath(), "haby-configured-operator-control-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            await OperatorHostIntegrationTests.WriteNativeCommitConfiguration(config, endpoint.AbsoluteUri, "pilot", Credential);
            var lines = await File.ReadAllLinesAsync(config);
            if (lines.Count(x => x.Trim() == "endpoint: " + endpoint.AbsoluteUri) != 1 ||
                lines.Count(x => x.Trim() == "token: " + Credential) != 1)
                throw new InvalidOperationException("Explicit CLI destination/credential association failed.");
        }
        finally { File.Delete(config); }
        Console.WriteLine("PASS configured-operator-client-controls exact-loopback-same-port; explicit-RPC-credential; explicit-CLI-config-credential; no-network");
    }
}
