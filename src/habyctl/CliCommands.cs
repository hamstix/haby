using Google.Protobuf;
using Grpc.Core;
using Haby.Operator.Contracts;
using System.CommandLine;

namespace Haby.Cli;

internal sealed partial class CliApplication(
    IEndpointSource endpointSource,
    IRegistryClientFactory clientFactory,
    TextWriter output,
    TextWriter error,
    Action<RootCommand>? registerAdditionalCommands = null)
{
    internal const int Success = 0;
    internal const int Rejected = 1;
    internal const int UsageError = 2;
    internal const int TransportError = 3;

    private readonly TextWriter _output = output;
    private readonly CliTextPresenter _presenter = new(output);
    private readonly TextWriter _error = error;

    internal async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var root = new RootCommand("Inspect and import manifests through a local registry.");
        RegisterManifestCommands(root);
        registerAdditionalCommands?.Invoke(root);
        var parsed = root.Parse(args);
        if (parsed.Errors.Count > 0 || args.Length == 0)
        {
            _error.WriteLine("Invalid command syntax. Use --help to see available commands.");
            return UsageError;
        }

        // Help is handled by System.CommandLine before any command action or endpoint lookup.
        var configuration = new InvocationConfiguration { Output = _output, Error = _error };
        var result = await parsed.InvokeAsync(configuration, cancellationToken);
        return result;
    }

    private void RegisterManifestCommands(RootCommand root)
    {
        var manifest = new Command("manifest", "Validate or import a manifest and inspect revisions.");
        var revision = new Command("revision", "Inspect accepted manifest revisions.");
        root.Subcommands.Add(manifest);
        manifest.Subcommands.Add(revision);
        manifest.SetAction(_ => MissingSubcommand());
        revision.SetAction(_ => MissingSubcommand());

        var validateFile = new Argument<string>("file") { Description = "Manifest envelope file." };
        var validate = new Command("validate", "Validate a manifest envelope.") { validateFile };
        validate.SetAction((parse, cancellationToken) => ExecuteAsync(
            async (client, token) => _presenter.Print(await client.ValidateAsync(
                new EnvelopeRequest
                {
                    OriginalEnvelope = ByteString.CopyFrom(
                    await ReadEnvelopeAsync(parse.GetRequiredValue(validateFile), token))
                }, token)), cancellationToken));
        manifest.Subcommands.Add(validate);

        var importFile = new Argument<string>("file") { Description = "Manifest envelope file." };
        var import = new Command("import", "Import a manifest envelope.") { importFile };
        import.SetAction((parse, cancellationToken) => ExecuteAsync(
            async (client, token) => _presenter.Print(await client.ImportAsync(
                new EnvelopeRequest
                {
                    OriginalEnvelope = ByteString.CopyFrom(
                    await ReadEnvelopeAsync(parse.GetRequiredValue(importFile), token))
                }, token)), cancellationToken));
        manifest.Subcommands.Add(import);

        var getId = new Argument<string>("application-record-id");
        var getNumber = new Argument<long>("revision-number");
        var get = new Command("get", "Get an accepted revision.") { getId, getNumber };
        get.SetAction((parse, cancellationToken) => ExecuteRevisionAsync(parse.GetRequiredValue(getNumber),
            (client, token) => GetAsync(client, parse.GetRequiredValue(getId), parse.GetRequiredValue(getNumber), token),
            cancellationToken));
        revision.Subcommands.Add(get);

        var previewId = new Argument<string>("application-record-id");
        var previewNumber = new Argument<long>("revision-number");
        var contextFile = new Argument<string>("context-file");
        var preview = new Command("preview", "Preview a revision against a local context file.")
            { previewId, previewNumber, contextFile };
        preview.SetAction((parse, cancellationToken) => ExecuteRevisionAsync(parse.GetRequiredValue(previewNumber),
            async (client, token) => _presenter.Print(await client.PreviewDatabaseAsync(
                await ReadPreviewContextAsync(parse.GetRequiredValue(contextFile), new RevisionReference
                {
                    ApplicationRecordId = parse.GetRequiredValue(previewId),
                    RevisionNumber = parse.GetRequiredValue(previewNumber)
                }, token), token)), cancellationToken));
        revision.Subcommands.Add(preview);
    }

    private int MissingSubcommand()
    {
        _error.WriteLine("A command is required. Use --help to see available commands.");
        return UsageError;
    }

    private async Task<int> GetAsync(IRegistryClient client, string id, long number, CancellationToken token) =>
        _presenter.Print(await client.GetRevisionAsync(new GetRevisionRequest
        {
            Reference = new RevisionReference { ApplicationRecordId = id, RevisionNumber = number }
        }, token));

    private Task<int> ExecuteRevisionAsync(long number, Func<IRegistryClient, CancellationToken, Task<int>> action,
        CancellationToken cancellationToken)
    {
        if (number > 0) return ExecuteAsync(action, cancellationToken);
        _error.WriteLine("Revision number must be positive.");
        return Task.FromResult(UsageError);
    }

    private async Task<int> ExecuteAsync(Func<IRegistryClient, CancellationToken, Task<int>> action,
        CancellationToken cancellationToken)
    {
        var address = ParseEndpoint(endpointSource.GetAddress());
        if (address is null)
        {
            _error.WriteLine("Set HABY_OPERATOR_URL to a loopback HTTP or HTTPS registry endpoint.");
            return UsageError;
        }
        try
        {
            using var client = clientFactory.Create(address,
                SkipTlsCertificateValidation(endpointSource.GetSkipTlsValidation()));
            return await action(client, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _error.WriteLine("Operation canceled.");
            return TransportError;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or RpcException or
            HttpRequestException or InvalidDataException)
        {
            _error.WriteLine(SafeError(exception));
            return exception is RpcException or HttpRequestException ? TransportError : UsageError;
        }
    }
}
