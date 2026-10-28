using Grpc.Net.Client;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal interface IEndpointSource
{
    string? GetAddress();
    string? GetSkipTlsValidation();
}

internal sealed class EnvironmentEndpointSource : IEndpointSource
{
    public string? GetAddress() => Environment.GetEnvironmentVariable("HABY_OPERATOR_URL");
    public string? GetSkipTlsValidation() => Environment.GetEnvironmentVariable("HABY_OPERATOR_SKIP_TLS_VALIDATION");
}

internal interface IRegistryClient : IDisposable
{
    Task<ValidateReply> ValidateAsync(EnvelopeRequest request, CancellationToken cancellationToken);
    Task<ImportReply> ImportAsync(EnvelopeRequest request, CancellationToken cancellationToken);
    Task<GetRevisionReply> GetRevisionAsync(GetRevisionRequest request, CancellationToken cancellationToken);
    Task<PreviewDatabaseReply> PreviewDatabaseAsync(PreviewDatabaseRequest request, CancellationToken cancellationToken);
}

internal interface IRegistryClientFactory
{
    IRegistryClient Create(Uri address, bool skipTlsValidation);
}

internal sealed class GrpcRegistryClientFactory : IRegistryClientFactory
{
    public IRegistryClient Create(Uri address, bool skipTlsValidation)
    {
        var channel = skipTlsValidation && address.Scheme == Uri.UriSchemeHttps
            ? GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = CliApplication.CreateHttpHandler(true) })
            : GrpcChannel.ForAddress(address);
        return new GrpcRegistryClient(channel);
    }
}

internal sealed class GrpcRegistryClient(GrpcChannel channel) : IRegistryClient
{
    private readonly ManifestOperator.ManifestOperatorClient _client = new(channel);

    public async Task<ValidateReply> ValidateAsync(EnvelopeRequest request, CancellationToken cancellationToken) =>
        await _client.ValidateAsync(request, cancellationToken: cancellationToken);

    public async Task<ImportReply> ImportAsync(EnvelopeRequest request, CancellationToken cancellationToken) =>
        await _client.ImportAsync(request, cancellationToken: cancellationToken);

    public async Task<GetRevisionReply> GetRevisionAsync(GetRevisionRequest request, CancellationToken cancellationToken) =>
        await _client.GetRevisionAsync(request, cancellationToken: cancellationToken);

    public async Task<PreviewDatabaseReply> PreviewDatabaseAsync(PreviewDatabaseRequest request, CancellationToken cancellationToken) =>
        await _client.PreviewDatabaseAsync(request, cancellationToken: cancellationToken);

    public void Dispose() => channel.Dispose();
}
