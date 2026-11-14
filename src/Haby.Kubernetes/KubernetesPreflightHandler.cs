using System.Net;
using System.Security.Cryptography;
using Haby.Application;
using Haby.Domain;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Haby.Kubernetes;

/// <summary>Access/authentication only. Namespace and capability admission requires an exact Placement later.</summary>
public sealed class KubernetesPreflightHandler : ProviderPreflightHandler<KubernetesProviderConfiguration>
{
    private readonly Func<EnvironmentRecordId, KubernetesProviderConfiguration, CancellationToken, ValueTask<KernelClient>> _connect;

    public KubernetesPreflightHandler(KubernetesProviderConfigurationCodec codec, ISecretStore secrets) :
        this(codec, (environment, configuration, token) => KubernetesClientFactory.CreateAccess(environment, configuration, secrets, token))
    { }

    // Friend tests may supply a disposable transport, never a production setting or DI alternative.
    internal KubernetesPreflightHandler(KubernetesProviderConfigurationCodec codec,
        Func<EnvironmentRecordId, KubernetesProviderConfiguration, CancellationToken, ValueTask<KernelClient>> connect) : base(codec) => _connect = connect;

    public override IReadOnlyList<ProviderAdapterKey> Keys { get; } = [new(KubernetesProviderConfigurationCodec.Kind,
        KubernetesProviderConfigurationCodec.Shape, KubernetesProviderConfigurationCodec.Implementation)];

    public override async ValueTask<ProviderPreflightResult> CheckTypedAsync(EnvironmentRecordId environment,
        KubernetesProviderConfiguration configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var client = await _connect(environment, configuration, deadline.Token);
            // Unlike /version or /readyz, this proves the supplied credentials were authenticated.
            // The review is discarded by the API server; it creates no persistent probe object.
            var review = await client.Client.AuthenticationV1.CreateSelfSubjectReviewAsync(new V1SelfSubjectReview(), cancellationToken: deadline.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) return new(ProviderPreflightOutcome.DeadlineExceeded);
            var user = review.Status?.UserInfo;
            return new(user is not null && !string.IsNullOrWhiteSpace(user.Username) && user.Username != "system:anonymous" &&
                user.Groups?.Contains("system:unauthenticated", StringComparer.Ordinal) != true
                ? ProviderPreflightOutcome.Ready : ProviderPreflightOutcome.AuthenticationFailed);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) when (deadline.IsCancellationRequested) { return new(ProviderPreflightOutcome.DeadlineExceeded); }
        catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.Unauthorized)
        { return new(ProviderPreflightOutcome.AuthenticationFailed); }
        catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.Forbidden)
        { return new(ProviderPreflightOutcome.InsufficientPrivilege); }
        catch (Contracts.WorkloadContractException error) when (error.Code == "credential-unavailable")
        { return new(ProviderPreflightOutcome.SecretUnavailable); }
        catch (Exception error) when (error is ArgumentException or CryptographicException or FormatException)
        { return new(ProviderPreflightOutcome.ConfigurationUnavailable); }
        catch (Exception) { return new(ProviderPreflightOutcome.ServerUnavailable); }
    }
}
