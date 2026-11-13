using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Haby.Application;
using Haby.Kubernetes.Contracts;
using k8s;
using k8s.Autorest;
using k8s.Models;
using static Haby.Kubernetes.DeploymentIntents;

namespace Haby.Kubernetes;

/// <summary>Bounded adapter kernel. It neither persists dispatch authority nor advances Haby lifecycle state.</summary>
public sealed class DeploymentKernel
{
    private readonly KubernetesBinding _binding;
    private readonly Func<CancellationToken, ValueTask<KernelClient>> _connect;
    public DeploymentKernel(KubernetesConnection connection, ISecretStore secrets)
    {
        connection.Validate(); ArgumentNullException.ThrowIfNull(secrets);
        _binding = connection.Binding; _connect = token => KubernetesClientFactory.Create(connection, secrets, token);
    }
    internal DeploymentKernel(KubernetesBinding binding, Func<CancellationToken, ValueTask<KernelClient>> connect) { _binding = binding; _connect = connect; }

    public async Task<KernelResult> ExecuteAsync(DeploymentIntent intent, CancellationToken cancellationToken = default)
    {
        var writes = 0; var dispatchInFlight = false; var validatingLiveObject = false; var validIntent = false;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); budget.CancelAfter(TimeSpan.FromSeconds(10));
        var clock = Stopwatch.StartNew();
        try
        {
            Validate(intent); Need(intent.Binding == _binding, "provider-binding"); budget.Token.ThrowIfCancellationRequested();
            validIntent = true;
            using var lease = await _connect(budget.Token); var client = lease.Client;
            while (true)
            {
                var current = await Read(client, intent, budget.Token);
                if (intent.ExpectedUid is null)
                {
                    if (current is not null) return new(KernelOutcome.Conflict, "occupied-name", writes);
                    var body = DeploymentObjects.Create(intent);
                    budget.Token.ThrowIfCancellationRequested(); dispatchInFlight = true; writes++;
                    using var response = await client.AppsV1.CreateNamespacedDeploymentWithHttpMessagesAsync(body, intent.Namespace, cancellationToken: budget.Token);
                    var observed = DeploymentObjects.Read(response.Body);
                    // Successful response with unexpected content is still an uncertain effect, never a refusal.
                    var receipt = DeploymentObjects.Receipt(observed, intent, ConfirmationSource.Response);
                    return new(KernelOutcome.Confirmed, "response-confirmed", writes, receipt);
                }
                if (current is null) return new(KernelOutcome.Conflict, "managed-object-missing", writes);
                validatingLiveObject = true;
                DeploymentObjects.Ownership(current, intent);
                Need(DeploymentObjects.String(current, "metadata", "uid") == intent.ExpectedUid, "uid-conflict");
                DeploymentObjects.Matches(current, intent.LastConfirmed!);
                var patch = DeploymentObjects.Patch(current, intent);
                budget.Token.ThrowIfCancellationRequested();
                if (clock.Elapsed >= TimeSpan.FromSeconds(10)) return new(KernelOutcome.Conflict, "action-budget", writes);
                dispatchInFlight = true; writes++;
                try
                {
                    using var response = await client.AppsV1.PatchNamespacedDeploymentWithHttpMessagesAsync(
                        new V1Patch(patch.ToJsonString(), V1Patch.PatchType.JsonPatch), intent.Name, intent.Namespace, cancellationToken: budget.Token);
                    var observed = DeploymentObjects.Read(response.Body);
                    Need(DeploymentObjects.String(observed, "metadata", "uid") == intent.ExpectedUid, "uid-conflict");
                    return new(KernelOutcome.Confirmed, "response-confirmed", writes, DeploymentObjects.Receipt(observed, intent, ConfirmationSource.Response));
                }
                catch (HttpOperationException error) when (IsResourceVersionTestRejection(error))
                {
                    dispatchInFlight = false;
                    if (writes >= 3 || clock.Elapsed >= TimeSpan.FromSeconds(10)) return new(KernelOutcome.Conflict, "precondition-limit", writes);
                    // A positively rejected test changed nothing. Next iteration reads and validates everything again.
                }
            }
        }
        catch (OperationCanceledException) { return new(dispatchInFlight ? KernelOutcome.Uncertain : writes == 0 ? KernelOutcome.CanceledBeforeDispatch : KernelOutcome.Conflict, dispatchInFlight ? "dispatch-uncertain" : "action-canceled", writes); }
        catch (WorkloadContractException)
        {
            return new(dispatchInFlight ? KernelOutcome.Uncertain : validatingLiveObject || writes > 0 ? KernelOutcome.Conflict : KernelOutcome.Refused,
            dispatchInFlight ? "response-unconfirmed" : validatingLiveObject ? "owned-state-conflict" : validIntent ? "connection-unavailable" : "invalid-input", writes);
        }
        catch (HttpOperationException error)
        {
            var status = (int?)error.Response?.StatusCode;
            if (dispatchInFlight && status is not (400 or 401 or 403 or 404 or 409 or 422)) return new(KernelOutcome.Uncertain, "dispatch-uncertain", writes);
            return new(KernelOutcome.Conflict, status is 401 or 403 ? "api-access-denied" : "api-conflict", writes);
        }
        catch (Exception) { return new(dispatchInFlight ? KernelOutcome.Uncertain : KernelOutcome.Refused, dispatchInFlight ? "dispatch-uncertain" : "connection-unavailable", writes); }
    }

    /// <summary>Read-only evidence recovery using the same immutable intent; zero write calls.</summary>
    public async Task<KernelResult> ObserveAsync(DeploymentIntent intent, CancellationToken cancellationToken = default)
    {
        var validIntent = false; var receivedObject = false;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); budget.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            Validate(intent); Need(intent.Binding == _binding, "provider-binding"); budget.Token.ThrowIfCancellationRequested();
            validIntent = true;
            using var lease = await _connect(budget.Token);
            var current = await Read(lease.Client, intent, budget.Token);
            if (current is null) return new(KernelOutcome.NotConfirmed, "object-absent", 0);
            receivedObject = true;
            DeploymentObjects.Ownership(current, intent);
            Need(intent.ExpectedUid is null || DeploymentObjects.String(current, "metadata", "uid") == intent.ExpectedUid, "uid-conflict");
            return new(KernelOutcome.Confirmed, "observation-confirmed", 0, DeploymentObjects.Receipt(current, intent, ConfirmationSource.Observation));
        }
        catch (WorkloadContractException)
        {
            return new(validIntent && !receivedObject ? KernelOutcome.ObservationUnavailable : KernelOutcome.NotConfirmed,
            validIntent && !receivedObject ? "observation-unavailable" : "evidence-not-confirmed", 0);
        }
        catch (Exception)
        { return new(KernelOutcome.ObservationUnavailable, "observation-unavailable", 0); }
    }

    private static async Task<System.Text.Json.Nodes.JsonObject?> Read(k8s.Kubernetes client, DeploymentIntent i, CancellationToken token)
    {
        try { using var response = await client.AppsV1.ReadNamespacedDeploymentWithHttpMessagesAsync(i.Name, i.Namespace, cancellationToken: token); return DeploymentObjects.Read(response.Body); }
        catch (HttpOperationException error) when (error.Response?.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
    internal static bool IsResourceVersionTestRejection(HttpOperationException error)
    {
        if ((int?)error.Response?.StatusCode != 422 || error.Response.Content is not { Length: <= 131072 } body) return false;
        try
        {
            using var json = JsonDocument.Parse(body); var root = json.RootElement;
            if (root.GetProperty("reason"u8).GetString() != "Invalid" || root.GetProperty("code"u8).GetInt32() != 422) return false;
            var causes = root.GetProperty("details"u8).GetProperty("causes"u8); if (causes.GetArrayLength() != 1) return false;
            var cause = causes[0];
            return cause.GetProperty("field"u8).GetString() == "patch" && cause.GetProperty("reason"u8).GetString() == "FieldValueInvalid" &&
                cause.GetProperty("message"u8).GetString()!.EndsWith(": testing value /metadata/resourceVersion failed: test failed", StringComparison.Ordinal);
        }
        catch (Exception malformed) when (malformed is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }
}
