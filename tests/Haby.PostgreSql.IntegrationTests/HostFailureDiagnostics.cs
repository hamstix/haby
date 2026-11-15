using System.Runtime.ExceptionServices;
using Grpc.Core;

namespace Haby.PostgreSql.IntegrationTests;

internal enum HostFailurePhase
{
    Build, Start, AddressScheme, AddressLoopback, Channel, ReflectionWrite, ReflectionRead, StatusAssertion, Cleanup,
    Catalog, LocalIssuance, AdministrativeRead, StaticDeliveryRead, RouteDiscovery, ReflectionCall, ReflectionComplete, DescriptorAssertion
}

// Structured test instrumentation, not parsing or copying exception messages/stacks.
internal static class HostFailureDiagnostics
{
    internal static async Task RunAsync(Func<Action<Func<ValueTask>>, Task> body,
        Func<HostFailurePhase> phase, Func<StatusCode?> observed,
        Action<HostFailurePhase, Exception, StatusCode?>? record = null)
    {
        record ??= Record;
        var cleanup = new List<Func<ValueTask>>();
        ExceptionDispatchInfo? primary = null;
        try { await body(cleanup.Add); }
        catch (Exception error)
        {
            primary = ExceptionDispatchInfo.Capture(error);
            // Capture body evidence before any resource can replace its exception.
            record(phase(), error, observed());
        }
        ExceptionDispatchInfo? firstCleanup = null;
        for (var i = cleanup.Count - 1; i >= 0; i--)
        {
            try { await cleanup[i](); }
            catch (Exception error)
            {
                firstCleanup ??= ExceptionDispatchInfo.Capture(error);
                record(HostFailurePhase.Cleanup, error, null);
            }
        }
        primary?.Throw();
        firstCleanup?.Throw();
    }

    private static readonly object Gate = new();
    internal const string Method = "Haby.PostgreSql.IntegrationTests.OperatorHostConfigurationTests.Disabled_tls_serves_grpc_on_loopback_without_certificate_settings";

    internal static string Render(HostFailurePhase phase, Exception error, StatusCode? observed)
    {
        var stage = phase switch
        {
            HostFailurePhase.Build => "host-build",
            HostFailurePhase.Start => "host-start",
            HostFailurePhase.AddressScheme => "address-scheme",
            HostFailurePhase.AddressLoopback => "address-loopback",
            HostFailurePhase.Channel => "channel",
            HostFailurePhase.ReflectionWrite => "reflection-write",
            HostFailurePhase.ReflectionRead => "reflection-read",
            HostFailurePhase.StatusAssertion => "status-assertion",
            HostFailurePhase.Cleanup => "cleanup",
            _ => "unknown"
        };
        var status = ((error as RpcException)?.StatusCode ?? observed) switch
        {
            StatusCode.Unauthenticated => "unauthenticated",
            StatusCode.Internal => "internal",
            StatusCode.Unavailable => "unavailable",
            StatusCode.DeadlineExceeded => "deadline-exceeded",
            null => "unavailable-evidence",
            _ => "other"
        };
        return stage + "," + status;
    }

    internal static void Record(HostFailurePhase phase, Exception error, StatusCode? observed)
    {
        var directory = Environment.GetEnvironmentVariable("HABY_TEST_TIMING_DIRECTORY");
        if (directory is null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "host-diagnostics.csv"), Method + "," + Render(phase, error, observed) + Environment.NewLine);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Allowlisted Host diagnostic unavailable; primary failure preserved.");
        }
    }

    internal static void RecordWorkload(bool initial, WorkloadAdmissionCheckpoint checkpoint, Exception error, StatusCode? observed)
    {
        var directory = Environment.GetEnvironmentVariable("HABY_TEST_TIMING_DIRECTORY");
        if (directory is null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "host-diagnostics.csv"),
                    WorkloadAdmissionDiagnostics.Render(initial, checkpoint, error, observed) + Environment.NewLine);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Allowlisted Workload diagnostic unavailable; primary failure preserved.");
        }
    }

    internal static void RecordProtected(bool enabled, HostFailurePhase phase, Exception error, StatusCode? observed,
        ReflectionRouteKind route, ReflectionCredentialPurpose purpose)
    {
        var directory = Environment.GetEnvironmentVariable("HABY_TEST_TIMING_DIRECTORY");
        if (directory is null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "protected-reflection-diagnostics.csv"),
                    ProtectedReflectionDiagnostics.Render(enabled, phase, error, observed, route, purpose) + Environment.NewLine);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Allowlisted protected reflection diagnostic unavailable; primary failure preserved.");
        }
    }
}

internal enum ReflectionRouteKind { None, V1Alpha, V1, Unknown }
internal enum ReflectionCredentialPurpose { None, Anonymous, StaticDelivery, OperatorAdmin, Unknown }

// One test invocation owns this state. Only closed values enter the separate CSV.
internal sealed class ProtectedReflectionDiagnostics(bool enabled, Action<string>? capture = null)
{
    internal const string Method = "Haby.PostgreSql.IntegrationTests.OperatorHostIntegrationTests.Operator_protected_reflection_describes_both_contracts_and_can_be_disabled";
    private readonly HashSet<Exception> recorded = new(ReferenceEqualityComparer.Instance);
    internal HostFailurePhase Phase { get; private set; } = HostFailurePhase.Catalog;
    internal StatusCode? Observed { get; private set; }
    private ReflectionRouteKind route;
    private ReflectionCredentialPurpose purpose;

    internal void At(HostFailurePhase phase, ReflectionRouteKind routeKind = ReflectionRouteKind.None,
        ReflectionCredentialPurpose credentialPurpose = ReflectionCredentialPurpose.None)
    {
        Phase = phase; Observed = null; route = routeKind; purpose = credentialPurpose;
    }

    internal void Observe(StatusCode status) { Phase = HostFailurePhase.StatusAssertion; Observed = status; }

    internal Action<HostFailurePhase, Exception, StatusCode?> Recorder(ReflectionRouteKind routeKind, ReflectionCredentialPurpose credentialPurpose) =>
        (phase, error, observed) => Record(phase, error, observed, routeKind, credentialPurpose);

    internal void RecordRoot(HostFailurePhase phase, Exception error, StatusCode? observed) =>
        Record(phase, error, observed, phase == HostFailurePhase.Cleanup ? ReflectionRouteKind.None : route,
            phase == HostFailurePhase.Cleanup ? ReflectionCredentialPurpose.None : purpose);

    private void Record(HostFailurePhase phase, Exception error, StatusCode? observed, ReflectionRouteKind routeKind, ReflectionCredentialPurpose credentialPurpose)
    {
        // Nested scopes rethrow the same primary. Keep every cleanup event, even if a disposer reuses an exception.
        var first = recorded.Add(error);
        if (phase != HostFailurePhase.Cleanup && !first) return;
        if (capture is null) HostFailureDiagnostics.RecordProtected(enabled, phase, error, observed, routeKind, credentialPurpose);
        else capture(Render(enabled, phase, error, observed, routeKind, credentialPurpose));
    }

    internal static string Render(bool enabled, HostFailurePhase phase, Exception error, StatusCode? observed,
        ReflectionRouteKind route, ReflectionCredentialPurpose purpose)
    {
        var stage = phase switch
        {
            HostFailurePhase.Catalog => "catalog",
            HostFailurePhase.Build => "host-build",
            HostFailurePhase.LocalIssuance => "local-issuance",
            HostFailurePhase.Start => "host-start",
            HostFailurePhase.Channel => "channel",
            HostFailurePhase.AdministrativeRead => "administrative-read",
            HostFailurePhase.StaticDeliveryRead => "static-delivery-read",
            HostFailurePhase.RouteDiscovery => "route-discovery",
            HostFailurePhase.ReflectionCall => "reflection-call",
            HostFailurePhase.ReflectionWrite => "reflection-write",
            HostFailurePhase.ReflectionComplete => "reflection-complete",
            HostFailurePhase.ReflectionRead => "reflection-read",
            HostFailurePhase.StatusAssertion => "status-assertion",
            HostFailurePhase.DescriptorAssertion => "descriptor-assertion",
            HostFailurePhase.Cleanup => "cleanup",
            _ => "unknown"
        };
        var routeKind = route switch
        {
            ReflectionRouteKind.None => "none",
            ReflectionRouteKind.V1Alpha => "v1alpha",
            ReflectionRouteKind.V1 => "v1",
            _ => "unknown"
        };
        var credentialPurpose = purpose switch
        {
            ReflectionCredentialPurpose.None => "none",
            ReflectionCredentialPurpose.Anonymous => "anonymous",
            ReflectionCredentialPurpose.StaticDelivery => "static-delivery",
            ReflectionCredentialPurpose.OperatorAdmin => "operator-admin",
            _ => "unknown"
        };
        var status = ((error as RpcException)?.StatusCode ?? observed) switch
        {
            StatusCode.OK => "ok",
            StatusCode.Cancelled => "cancelled",
            StatusCode.Unknown => "unknown",
            StatusCode.InvalidArgument => "invalid-argument",
            StatusCode.DeadlineExceeded => "deadline-exceeded",
            StatusCode.NotFound => "not-found",
            StatusCode.AlreadyExists => "already-exists",
            StatusCode.PermissionDenied => "permission-denied",
            StatusCode.ResourceExhausted => "resource-exhausted",
            StatusCode.FailedPrecondition => "failed-precondition",
            StatusCode.Aborted => "aborted",
            StatusCode.OutOfRange => "out-of-range",
            StatusCode.Unimplemented => "unimplemented",
            StatusCode.Internal => "internal",
            StatusCode.Unavailable => "unavailable",
            StatusCode.DataLoss => "data-loss",
            StatusCode.Unauthenticated => "unauthenticated",
            null => "unavailable-evidence",
            _ => "unknown-status"
        };
        var category = error switch
        {
            RpcException => "rpc",
            Xunit.Sdk.XunitException => "assertion",
            OperationCanceledException => "cancelled",
            IOException => "io",
            _ => "other"
        };
        return Method + "," + (enabled ? "enabled-true" : "enabled-false") + "," + stage + "," + routeKind + "," + credentialPurpose + "," + status + "," + category;
    }
}

internal enum WorkloadAdmissionCheckpoint
{
    TrapBuild, TrapStart, TrapAccept, ScenarioSetup, AdmissionServices, BaselineRead, ConnectionRead, HostBuild, HostStart, Channel, RequestBuild, EnvironmentRead, AnonymousClient, AnonymousDenied, ChangeSetCreate, ChangeSetPresent, PlanCreate, PlanStatus, PlanCurrent, DescriptionStatus, DescriptionItems, DescriptionRedaction, OtherChangeSetCreate, PlanMembership, NativeRequired, NativeChangeSet, NativeChangeSetRead, NativePlan, NativeDescription, NativeCheck, NativeCommit, NativeSubmission, Commit, CommitStatus, Submission, AppliedFlag, FrozenDescription, ActorAuthorization, ActorIdentity, ExecutionGuard, NoProgress, HostStop, RestartBuild, RestartStart, RestartChannel, GenerationRead, GenerationIdentity, PlanIdentity, NativeRead, NativeReadText, NativeReadinessText, Replay, ReplayIdentity, ReceiptRead, ReceiptPresent, SecretReads, PublicConfigurationGuard, VerificationServices, VerificationContext, NoProviderContact, DeploymentRead, DesiredGeneration, AppliedGeneration, NoActionIntents, NoRecipients, NoDocuments, NoIdentities, NoPermissions, NoManagedWorkloads, TargetIdentity, NoOperations, NoCheckpoints, NoJournalProgress, Cleanup
}

// Only the two Workload admission theory rows use this explicit recorder.
internal sealed class WorkloadAdmissionDiagnostics(bool initial, Action<string>? capture = null)
{
    internal const string Method = "Haby.PostgreSql.IntegrationTests.OperatorHostIntegrationTests.Operator_admits_supported_Workload_desired_state_without_execution";
    private readonly HashSet<Exception> recorded = new(ReferenceEqualityComparer.Instance);
    private WorkloadAdmissionCheckpoint checkpoint = WorkloadAdmissionCheckpoint.TrapBuild;

    internal void At(WorkloadAdmissionCheckpoint value) => checkpoint = value;

    internal Task RunAsync(Func<Action<Func<ValueTask>>, Task> body) =>
        HostFailureDiagnostics.RunAsync(body, () => HostFailurePhase.Catalog, () => null, Record);

    private void Record(HostFailurePhase phase, Exception error, StatusCode? observed)
    {
        var first = recorded.Add(error);
        if (phase != HostFailurePhase.Cleanup && !first) return;
        var stage = phase == HostFailurePhase.Cleanup ? WorkloadAdmissionCheckpoint.Cleanup : checkpoint;
        if (capture is null) HostFailureDiagnostics.RecordWorkload(initial, stage, error, observed);
        else capture(Render(initial, stage, error, observed));
    }

    internal static string Render(bool initial, WorkloadAdmissionCheckpoint checkpoint, Exception error, StatusCode? observed)
    {
        var stage = checkpoint switch
        {
            WorkloadAdmissionCheckpoint.TrapBuild => "trap-build",
            WorkloadAdmissionCheckpoint.TrapStart => "trap-start",
            WorkloadAdmissionCheckpoint.TrapAccept => "trap-accept",
            WorkloadAdmissionCheckpoint.ScenarioSetup => "scenario-setup",
            WorkloadAdmissionCheckpoint.AdmissionServices => "admission-services",
            WorkloadAdmissionCheckpoint.BaselineRead => "baseline-read",
            WorkloadAdmissionCheckpoint.ConnectionRead => "connection-read",
            WorkloadAdmissionCheckpoint.HostBuild => "host-build",
            WorkloadAdmissionCheckpoint.HostStart => "host-start",
            WorkloadAdmissionCheckpoint.Channel => "channel",
            WorkloadAdmissionCheckpoint.RequestBuild => "request-build",
            WorkloadAdmissionCheckpoint.EnvironmentRead => "environment-read",
            WorkloadAdmissionCheckpoint.AnonymousClient => "anonymous-client",
            WorkloadAdmissionCheckpoint.AnonymousDenied => "anonymous-denied",
            WorkloadAdmissionCheckpoint.ChangeSetCreate => "change-set-create",
            WorkloadAdmissionCheckpoint.ChangeSetPresent => "change-set-present",
            WorkloadAdmissionCheckpoint.PlanCreate => "plan-create",
            WorkloadAdmissionCheckpoint.PlanStatus => "plan-status",
            WorkloadAdmissionCheckpoint.PlanCurrent => "plan-current",
            WorkloadAdmissionCheckpoint.DescriptionStatus => "description-status",
            WorkloadAdmissionCheckpoint.DescriptionItems => "description-items",
            WorkloadAdmissionCheckpoint.DescriptionRedaction => "description-redaction",
            WorkloadAdmissionCheckpoint.OtherChangeSetCreate => "other-change-set-create",
            WorkloadAdmissionCheckpoint.PlanMembership => "plan-membership",
            WorkloadAdmissionCheckpoint.NativeRequired => "native-required",
            WorkloadAdmissionCheckpoint.NativeChangeSet => "native-change-set",
            WorkloadAdmissionCheckpoint.NativeChangeSetRead => "native-change-set-read",
            WorkloadAdmissionCheckpoint.NativePlan => "native-plan",
            WorkloadAdmissionCheckpoint.NativeDescription => "native-description",
            WorkloadAdmissionCheckpoint.NativeCheck => "native-check",
            WorkloadAdmissionCheckpoint.NativeCommit => "native-commit",
            WorkloadAdmissionCheckpoint.NativeSubmission => "native-submission",
            WorkloadAdmissionCheckpoint.Commit => "commit",
            WorkloadAdmissionCheckpoint.CommitStatus => "commit-status",
            WorkloadAdmissionCheckpoint.Submission => "submission",
            WorkloadAdmissionCheckpoint.AppliedFlag => "applied-flag",
            WorkloadAdmissionCheckpoint.FrozenDescription => "frozen-description",
            WorkloadAdmissionCheckpoint.ActorAuthorization => "actor-authorization",
            WorkloadAdmissionCheckpoint.ActorIdentity => "actor-identity",
            WorkloadAdmissionCheckpoint.ExecutionGuard => "execution-guard",
            WorkloadAdmissionCheckpoint.NoProgress => "no-progress",
            WorkloadAdmissionCheckpoint.HostStop => "host-stop",
            WorkloadAdmissionCheckpoint.RestartBuild => "restart-build",
            WorkloadAdmissionCheckpoint.RestartStart => "restart-start",
            WorkloadAdmissionCheckpoint.RestartChannel => "restart-channel",
            WorkloadAdmissionCheckpoint.GenerationRead => "generation-read",
            WorkloadAdmissionCheckpoint.GenerationIdentity => "generation-identity",
            WorkloadAdmissionCheckpoint.PlanIdentity => "plan-identity",
            WorkloadAdmissionCheckpoint.NativeRead => "native-read",
            WorkloadAdmissionCheckpoint.NativeReadText => "native-read-text",
            WorkloadAdmissionCheckpoint.NativeReadinessText => "native-readiness-text",
            WorkloadAdmissionCheckpoint.Replay => "replay",
            WorkloadAdmissionCheckpoint.ReplayIdentity => "replay-identity",
            WorkloadAdmissionCheckpoint.ReceiptRead => "receipt-read",
            WorkloadAdmissionCheckpoint.ReceiptPresent => "receipt-present",
            WorkloadAdmissionCheckpoint.SecretReads => "secret-reads",
            WorkloadAdmissionCheckpoint.PublicConfigurationGuard => "public-configuration-guard",
            WorkloadAdmissionCheckpoint.VerificationServices => "verification-services",
            WorkloadAdmissionCheckpoint.VerificationContext => "verification-context",
            WorkloadAdmissionCheckpoint.NoProviderContact => "no-provider-contact",
            WorkloadAdmissionCheckpoint.DeploymentRead => "deployment-read",
            WorkloadAdmissionCheckpoint.DesiredGeneration => "desired-generation",
            WorkloadAdmissionCheckpoint.AppliedGeneration => "applied-generation",
            WorkloadAdmissionCheckpoint.NoActionIntents => "no-action-intents",
            WorkloadAdmissionCheckpoint.NoRecipients => "no-recipients",
            WorkloadAdmissionCheckpoint.NoDocuments => "no-documents",
            WorkloadAdmissionCheckpoint.NoIdentities => "no-identities",
            WorkloadAdmissionCheckpoint.NoPermissions => "no-permissions",
            WorkloadAdmissionCheckpoint.NoManagedWorkloads => "no-managed-workloads",
            WorkloadAdmissionCheckpoint.TargetIdentity => "target-identity",
            WorkloadAdmissionCheckpoint.NoOperations => "no-operations",
            WorkloadAdmissionCheckpoint.NoCheckpoints => "no-checkpoints",
            WorkloadAdmissionCheckpoint.NoJournalProgress => "no-journal-progress",
            WorkloadAdmissionCheckpoint.Cleanup => "cleanup",
            _ => "unknown"
        };
        var status = ((error as RpcException)?.StatusCode ?? observed) switch
        {
            StatusCode.OK => "ok",
            StatusCode.Unauthenticated => "unauthenticated",
            StatusCode.InvalidArgument => "invalid-argument",
            StatusCode.FailedPrecondition => "failed-precondition",
            StatusCode.Internal => "internal",
            StatusCode.Unavailable => "unavailable",
            StatusCode.DeadlineExceeded => "deadline-exceeded",
            StatusCode.Cancelled => "cancelled",
            null => "unavailable-evidence",
            _ => "other"
        };
        var category = error switch
        {
            RpcException => "rpc",
            Xunit.Sdk.XunitException => "assertion",
            OperationCanceledException => "cancelled",
            IOException => "io",
            _ => "unknown"
        };
        return Method + "," + (initial ? "initial" : "mixed") + "," + stage + "," + category + "," + status;
    }
}
