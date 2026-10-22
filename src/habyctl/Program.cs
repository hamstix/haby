using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Haby.Cli;
using Haby.Operator.Contracts;

try
{
    var address = CliApplication.ParseEndpoint(Environment.GetEnvironmentVariable("HABY_OPERATOR_URL"));
    if (args.Length < 2 || args[0] != "manifest" || address is null)
        return CliApplication.Usage();
    var skipTlsValidation = CliApplication.SkipTlsCertificateValidation(
        Environment.GetEnvironmentVariable("HABY_OPERATOR_SKIP_TLS_VALIDATION"));
    using var channel = skipTlsValidation && address.Scheme == Uri.UriSchemeHttps
        ? GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = CliApplication.CreateHttpHandler(true) })
        : GrpcChannel.ForAddress(address);
    var client = new ManifestOperator.ManifestOperatorClient(channel);
    if (args.Length == 3 && args[1] is "validate" or "import")
    {
        var bytes = await CliApplication.ReadEnvelopeAsync(args[2]);
        var request = new EnvelopeRequest { OriginalEnvelope = ByteString.CopyFrom(bytes) };
        if (args[1] == "validate") return CliApplication.Print(await client.ValidateAsync(request));
        return CliApplication.Print(await client.ImportAsync(request));
    }
    if (args.Length == 5 && args[1] == "revision" && args[2] == "get" &&
        long.TryParse(args[4], out var number) && number > 0)
        return CliApplication.Print(await client.GetRevisionAsync(new GetRevisionRequest
        {
            Reference = new RevisionReference { ApplicationRecordId = args[3], RevisionNumber = number }
        }));
    return CliApplication.Usage();
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or RpcException or
    HttpRequestException or InvalidDataException)
{
    // Exception messages can contain paths, endpoint details, or TLS failure internals.
    Console.Error.WriteLine(CliApplication.SafeError(error));
    return 2;
}
