namespace Haby.Cli;

internal sealed class DefaultCliContextResolver(CliConfigurationService configuration, IEndpointSource endpoints) : ICliContextResolver
{
    public async ValueTask<CliResolvedContext> ResolveAsync(CliContextSelection selection, CancellationToken cancellationToken)
    {
        var config = await configuration.LoadAsync(selection.ConfigurationPath, cancellationToken);
        var name = selection.ContextName ?? config.CurrentContext;
        if (name is null)
        {
            var endpoint = CliApplication.ParseEndpoint(endpoints.GetAddress()) ?? throw new InvalidRegistryEndpointException();
            return new(endpoint, "", skipTlsCertificateValidation: CliApplication.SkipTlsCertificateValidation(endpoints.GetSkipTlsValidation()));
        }
        if (!config.Contexts.TryGetValue(name, out var context) || !config.Instances.TryGetValue(context.Instance, out var instance)) throw new InvalidDataException();
        var address = CliApplication.ParseEndpoint(instance.Endpoint, instance.AllowInsecureHttp) ?? throw new InvalidRegistryEndpointException();
        return new(address, context.EnvironmentId, new InstanceCredentialSource(instance), instance.AllowInsecureHttp,
            CliApplication.SkipTlsCertificateValidation(endpoints.GetSkipTlsValidation()));
    }
    private sealed class InstanceCredentialSource(CliInstance instance) : ICliCredentialSource
    {
        public async ValueTask<string?> GetCredentialAsync(CancellationToken cancellationToken) => await CliOperatorCredential.ResolveAsync(instance, cancellationToken);
        public override string ToString() => "[Credential source]";
    }
}
internal sealed class InvalidRegistryEndpointException : Exception;
