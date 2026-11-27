using System.Net;
using System.Security.Cryptography;
using Haby.Application;
using Haby.Domain;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Haby.Kubernetes;

/// <summary>Access/authentication only. Namespace and capability admission requires an exact Placement later.</summary>
public sealed class KubernetesConnectionProbe : ProviderConnectionProbe<KubernetesProviderConfiguration>
{
    private readonly Func<EnvironmentRecordId, KubernetesProviderConfiguration, CancellationToken, ValueTask<KernelClient>> _connect;

    public KubernetesConnectionProbe(KubernetesProviderConfigurationCodec codec, ISecretStore secrets) :
        this(codec, (environment, configuration, token) => KubernetesClientFactory.CreateAccess(environment, configuration, secrets, token))
    { }

    // Friend tests may supply a disposable transport, never a production setting or DI alternative.
    internal KubernetesConnectionProbe(KubernetesProviderConfigurationCodec codec,
        Func<EnvironmentRecordId, KubernetesProviderConfiguration, CancellationToken, ValueTask<KernelClient>> connect) : base(codec) => _connect = connect;

    public override IReadOnlyList<ProviderAdapterKey> Keys { get; } = [new(KubernetesProviderConfigurationCodec.Kind,
        KubernetesProviderConfigurationCodec.Shape, KubernetesProviderConfigurationCodec.Implementation)];

    public override async ValueTask<ProviderConnectionResult> ProbeTypedAsync(ProviderConnectionContext context,
        KubernetesProviderConfiguration configuration, CancellationToken cancellationToken)
    {
        ProviderConnectionResult Result(ProviderConnectionOutcome outcome, ProviderConnectionDiagnostic? diagnostic = null) => new(outcome)
        {
            Diagnostic = diagnostic,
            ServerCertificateValidationDisabled = configuration.Tls.Mode == KubernetesTlsMode.Insecure,
        };
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var client = await _connect(context.Environment, configuration, deadline.Token);
            // Unlike /version or /readyz, this proves the supplied credentials were authenticated.
            // The review is discarded by the API server; it creates no persistent probe object.
            var review = await client.Client.AuthenticationV1.CreateSelfSubjectReviewAsync(new V1SelfSubjectReview(), cancellationToken: deadline.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) return Result(ProviderConnectionOutcome.DeadlineExceeded);
            var user = review.Status?.UserInfo;
            return Result(user is not null && !string.IsNullOrWhiteSpace(user.Username) && user.Username != "system:anonymous" &&
                user.Groups?.Contains("system:unauthenticated", StringComparer.Ordinal) != true
                ? ProviderConnectionOutcome.Connected : ProviderConnectionOutcome.AuthenticationFailed);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) when (deadline.IsCancellationRequested) { return Result(ProviderConnectionOutcome.DeadlineExceeded); }
        catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.Unauthorized)
        { return Result(ProviderConnectionOutcome.AuthenticationFailed); }
        catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.Forbidden)
        { return Result(ProviderConnectionOutcome.AuthenticationUnavailable); }
        catch (Contracts.WorkloadContractException error) when (error.Code == "credential-unavailable")
        { return Result(ProviderConnectionOutcome.SecretUnavailable); }
        catch (Exception error) when (error is ArgumentException or CryptographicException or FormatException)
        { return Result(ProviderConnectionOutcome.ConfigurationUnavailable); }
        catch (Exception error)
        {
            var diagnostic = KubernetesTlsFailure.Find(error);
            return Result(diagnostic is null ? ProviderConnectionOutcome.ConnectionUnavailable : ProviderConnectionOutcome.TlsFailed, diagnostic);
        }
    }
}
