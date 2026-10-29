using Google.Protobuf;
using Grpc.Core;
using Haby.Operator.Contracts;

namespace Haby.Cli;

internal sealed partial class CliCommandContext
{
    internal int PrintSecret(string command, SecretReply reply) =>
        jsonOutput() ? jsonPresenter.PrintSecret(command, reply) : presenter.PrintSecret(command, reply);
    internal int PrintSecrets(ListSecretsReply reply) =>
        jsonOutput() ? jsonPresenter.PrintSecrets(reply) : presenter.PrintSecrets(reply);

    internal Task<int> ExecuteSecretAsync(string? environment,
        Func<IRegistryClient, string, CancellationToken, Task<int>> action, CancellationToken token) =>
        ExecuteResolvedAsync((client, selected, ct) =>
        {
            var env = environment ?? selected;
            if (string.IsNullOrEmpty(env))
            {
                error.WriteLine("Select an Environment using --environment or a context.");
                return Task.FromResult(CliApplication.UsageError);
            }
            return action(client, env, ct);
        }, requireContext: false, token);

    internal Task<int> CreateSecretAsync(string name, string? environment, bool stdin, string? suppliedRequestId, CancellationToken token)
    {
        var requestId = Guid.NewGuid();
        if (suppliedRequestId is not null && (!Guid.TryParse(suppliedRequestId, out requestId) || requestId == Guid.Empty))
        {
            error.WriteLine("Request ID must be a nonempty UUID.");
            return Task.FromResult(CliApplication.UsageError);
        }
        return ExecuteSecretAsync(environment, async (client, env, ct) =>
        {
            byte[] bytes;
            try { bytes = await SecretInput.ReadAsync(secretTerminal, stdin, error, ct); }
            catch (InvalidDataException)
            {
                error.WriteLine("Secret input requires 1..65,536 bytes; redirected input requires --stdin.");
                return CliApplication.UsageError;
            }
            try
            {
                var request = new CreateSecretRequest
                { Environment = env, Name = name, RequestId = requestId.ToString(), Material = ByteString.CopyFrom(bytes) };
                try
                {
                    var reply = await client.CreateSecretAsync(request, ct);
                    if (reply.Status == SecretStatus.SecretStorageUnavailable) Retry();
                    if (reply.Status is SecretStatus.Created or SecretStatus.ExistingEquivalent &&
                        (!reply.HasRequestId || !Guid.TryParse(reply.RequestId, out var acceptedRequest) ||
                         acceptedRequest != requestId || reply.Secret?.Name != name))
                        throw new InvalidDataException("Incomplete creation result.");
                    return PrintSecret("secret create", reply);
                }
                catch (Exception failure) when (failure is RpcException or HttpRequestException or OperationCanceledException or IOException or InvalidDataException)
                {
                    Retry();
                    return CliApplication.TransportError;
                }
                finally { request.Material = ByteString.Empty; }
            }
            finally { Array.Clear(bytes); }

            void Retry() => error.WriteLine($"Creation outcome is uncertain or storage is unavailable. RequestId: {requestId}. Retry secret create with the same Environment, name, exact input and --request-id {requestId}; do not change the value.");
        }, token);
    }
}
