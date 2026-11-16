using Grpc.Core;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class HostFailureDiagnosticsTests
{
    [Fact]
    public async Task Protected_reflection_denial_accepts_exact_status_at_creation_write_completion_or_read()
    {
        var phases = new[] { HostFailurePhase.ReflectionCall, HostFailurePhase.ReflectionWrite, HostFailurePhase.ReflectionComplete, HostFailurePhase.ReflectionRead };
        foreach (var expected in new[] { StatusCode.Unauthenticated, StatusCode.Unimplemented })
            foreach (var failurePhase in phases)
            {
                var reached = new List<HostFailurePhase>();
                StatusCode? observed = null;
                var denial = new RpcException(new Status(expected, string.Empty));
                var actual = await OperatorHostIntegrationTests.AuthDenied(async () =>
                {
                    foreach (var phase in phases)
                    {
                        reached.Add(phase);
                        if (phase != failurePhase) continue;
                        if (phase == HostFailurePhase.ReflectionCall) throw denial;
                        await Task.FromException(denial);
                    }
                }, value => observed = value, expected);
                Assert.Same(denial, actual);
                Assert.Equal(expected, observed);
                Assert.Equal(phases.Take(Array.IndexOf(phases, failurePhase) + 1), reached);
            }
    }

    [Fact]
    public async Task Protected_reflection_denial_refuses_success_wrong_status_and_unrelated_error()
    {
        foreach (var expected in new[] { StatusCode.Unauthenticated, StatusCode.Unimplemented })
            foreach (var error in new Exception?[] { null, new RpcException(new Status(StatusCode.PermissionDenied, string.Empty)), new InvalidOperationException() })
            {
                StatusCode? observed = null;
                var failure = await Record.ExceptionAsync(() => OperatorHostIntegrationTests.AuthDenied(
                    () => error is null ? Task.CompletedTask : Task.FromException(error), value => observed = value, expected));
                Assert.NotNull(failure);
                Assert.Equal(error is RpcException ? StatusCode.PermissionDenied : null, observed);
            }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Anonymous_reflection_assertion_accepts_exact_denial_from_write_or_read(bool earlyWrite)
    {
        var phase = HostFailurePhase.Channel;
        StatusCode? observed = null;
        var reads = 0;
        var denial = new RpcException(new Status(StatusCode.Unauthenticated, string.Empty));
        await AnonymousReflectionAssertion.RequireUnauthenticatedAsync(
            () => earlyWrite ? Task.FromException(denial) : Task.CompletedTask,
            () => { reads++; return Task.FromException(denial); },
            value => phase = value, value => observed = value);
        Assert.Equal(earlyWrite ? 0 : 1, reads);
        Assert.Equal(HostFailurePhase.StatusAssertion, phase);
        Assert.Equal(StatusCode.Unauthenticated, observed);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Anonymous_reflection_assertion_refuses_wrong_status_and_non_rpc_errors(bool earlyWrite, bool nonRpc)
    {
        var phase = HostFailurePhase.Channel;
        StatusCode? observed = null;
        var reads = 0;
        Exception error = nonRpc ? new InvalidOperationException() : new RpcException(new Status(StatusCode.PermissionDenied, string.Empty));
        var failure = await Record.ExceptionAsync(() => AnonymousReflectionAssertion.RequireUnauthenticatedAsync(
            () => earlyWrite ? Task.FromException(error) : Task.CompletedTask,
            () => { reads++; return Task.FromException(error); },
            value => phase = value, value => observed = value));
        Assert.NotNull(failure);
        Assert.Equal(earlyWrite ? 0 : 1, reads);
        if (nonRpc)
        {
            Assert.Null(observed);
            Assert.Equal(earlyWrite ? HostFailurePhase.ReflectionWrite : HostFailurePhase.ReflectionRead, phase);
        }
        else
        {
            Assert.Equal(StatusCode.PermissionDenied, observed);
            Assert.Equal(HostFailurePhase.StatusAssertion, phase);
        }
    }

    [Fact]
    public async Task Anonymous_reflection_assertion_refuses_missing_denial()
    {
        var phase = HostFailurePhase.Channel;
        StatusCode? observed = null;
        var failure = await Record.ExceptionAsync(() => AnonymousReflectionAssertion.RequireUnauthenticatedAsync(
            () => Task.CompletedTask, () => Task.CompletedTask,
            value => phase = value, value => observed = value));
        Assert.NotNull(failure);
        Assert.Null(observed);
        Assert.Equal(HostFailurePhase.ReflectionRead, phase);
    }

    [Theory]
    [InlineData((int)HostFailurePhase.ReflectionWrite)]
    [InlineData((int)HostFailurePhase.ReflectionRead)]
    [InlineData((int)HostFailurePhase.StatusAssertion)]
    public async Task Body_failure_survives_every_failing_disposal_with_separate_safe_evidence(int phaseValue)
    {
        var phase = (HostFailurePhase)phaseValue;
        var primary = new RpcException(new Status(StatusCode.Unauthenticated, "PROTECTED-body"));
        var cleanup = new RpcException(new Status(StatusCode.Internal, "PROTECTED-cleanup"));
        var evidence = new List<string>();
        var disposed = new List<int>();
        var error = await Record.ExceptionAsync(() => HostFailureDiagnostics.RunAsync(defer =>
        {
            for (var i = 0; i < 3; i++)
            {
                var index = i;
                defer(() =>
                {
                    disposed.Add(index);
                    if (index == 0) return ValueTask.FromException(cleanup);
                    throw cleanup;
                });
            }
            throw primary;
        }, () => phase, () => StatusCode.Unauthenticated,
            (stage, failure, observed) =>
            {
                if (stage != HostFailurePhase.Cleanup) Assert.Empty(disposed);
                evidence.Add(HostFailureDiagnostics.Render(stage, failure, observed));
            }));
        Assert.Same(primary, error);
        Assert.Equal([2, 1, 0], disposed);
        Assert.Equal(4, evidence.Count);
        Assert.Equal(HostFailureDiagnostics.Render(phase, primary, null), evidence[0]);
        Assert.All(evidence.Skip(1), line => Assert.Equal("cleanup,internal", line));
        foreach (var initial in new[] { true, false })
        {
            var workloadEvidence = new List<string>();
            var workloadDisposed = new List<int>();
            var workload = new WorkloadAdmissionDiagnostics(initial, line =>
            {
                if (workloadEvidence.Count == 0) Assert.Empty(workloadDisposed);
                workloadEvidence.Add(line);
            });
            var workloadError = await Record.ExceptionAsync(() => workload.RunAsync(async defer =>
            {
                defer(() => { workloadDisposed.Add(1); throw cleanup; });
                await workload.RunAsync(release =>
                {
                    release(() => { workloadDisposed.Add(2); throw cleanup; });
                    workload.At(WorkloadAdmissionCheckpoint.CommitStatus);
                    throw primary;
                });
            }));
            Assert.Same(primary, workloadError);
            Assert.Equal([2, 1], workloadDisposed);
            Assert.Equal([
                WorkloadAdmissionDiagnostics.Render(initial, WorkloadAdmissionCheckpoint.CommitStatus, primary, null),
                WorkloadAdmissionDiagnostics.Render(initial, WorkloadAdmissionCheckpoint.Cleanup, cleanup, null),
                WorkloadAdmissionDiagnostics.Render(initial, WorkloadAdmissionCheckpoint.Cleanup, cleanup, null)
            ], workloadEvidence);
            Assert.All(workloadEvidence, line => Assert.StartsWith(WorkloadAdmissionDiagnostics.Method + ",", line));
        }
    }

    [Fact]
    public async Task Cleanup_only_failure_is_reported_and_rethrown_without_stale_body_status()
    {
        var cleanup = new InvalidOperationException("PROTECTED-cleanup");
        var evidence = new List<string>();
        var error = await Record.ExceptionAsync(() => HostFailureDiagnostics.RunAsync(defer =>
        {
            defer(() => throw cleanup);
            return Task.CompletedTask;
        }, () => HostFailurePhase.StatusAssertion, () => StatusCode.Unauthenticated,
            (stage, failure, observed) => evidence.Add(HostFailureDiagnostics.Render(stage, failure, observed))));
        Assert.Same(cleanup, error);
        Assert.Equal(["cleanup,unavailable-evidence"], evidence);
        var workloadEvidence = new List<string>();
        var workload = new WorkloadAdmissionDiagnostics(false, workloadEvidence.Add);
        var workloadError = await Record.ExceptionAsync(() => workload.RunAsync(async _ =>
        {
            workload.At(WorkloadAdmissionCheckpoint.CommitStatus);
            await workload.RunAsync(release =>
            {
                release(() => throw cleanup);
                return Task.CompletedTask;
            });
        }));
        Assert.Same(cleanup, workloadError);
        Assert.Equal([WorkloadAdmissionDiagnostics.Method + ",mixed,cleanup,unknown,unavailable-evidence"], workloadEvidence);
        var cancelled = new OperationCanceledException("PROTECTED-cancellation");
        var cancelledError = await Record.ExceptionAsync(() => workload.RunAsync(_ => throw cancelled));
        Assert.Same(cancelled, cancelledError);
        Assert.EndsWith(",cancelled,unavailable-evidence", workloadEvidence[^1]);
    }

    [Theory]
    [InlineData(StatusCode.Unauthenticated, "unauthenticated")]
    [InlineData(StatusCode.Internal, "internal")]
    public void Transport_diagnostic_keeps_phase_and_code_without_driver_text(StatusCode code, string expected)
    {
        const string secret = "PROTECTED-driver-credential-payload";
        var error = new RpcException(new Status(code, secret));
        var line = HostFailureDiagnostics.Render(HostFailurePhase.ReflectionWrite, error, null);
        Assert.Equal("reflection-write," + expected, line);
        Assert.DoesNotContain(secret, line);
        var workloadLine = WorkloadAdmissionDiagnostics.Render(true, WorkloadAdmissionCheckpoint.PlanStatus, error, null);
        Assert.Equal(WorkloadAdmissionDiagnostics.Method + ",initial,plan-status,rpc," + expected, workloadLine);
        Assert.DoesNotContain(secret, workloadLine);
        Assert.NotEqual(HostFailureDiagnostics.Method, WorkloadAdmissionDiagnostics.Method);
    }

    [Fact]
    public void Assertion_diagnostic_keeps_observed_status_without_assertion_payload()
    {
        var line = HostFailureDiagnostics.Render(HostFailurePhase.StatusAssertion,
            new InvalidOperationException("PROTECTED-assertion-payload"), StatusCode.Unavailable);
        Assert.Equal("status-assertion,unavailable", line);
        const string secret = "PROTECTED,\r\nBearer secret /private/path";
        var assertion = new Xunit.Sdk.XunitException(secret);
        assertion.Data[secret] = secret;
        foreach (var checkpoint in Enum.GetValues<WorkloadAdmissionCheckpoint>())
        {
            var rendered = WorkloadAdmissionDiagnostics.Render(false, checkpoint, assertion, null);
            Assert.StartsWith(WorkloadAdmissionDiagnostics.Method + ",mixed,", rendered);
            Assert.DoesNotContain(",unknown,", rendered);
            Assert.EndsWith(",assertion,unavailable-evidence", rendered);
            Assert.Equal(5, rendered.Split(',').Length);
            Assert.DoesNotContain(secret, rendered);
            Assert.DoesNotContain('\r', rendered); Assert.DoesNotContain('\n', rendered);
        }
        var hostile = new RpcException(new Status((StatusCode)999, secret), new Metadata { { "private", secret } });
        Assert.Equal(WorkloadAdmissionDiagnostics.Method + ",initial,unknown,rpc,other",
            WorkloadAdmissionDiagnostics.Render(true, (WorkloadAdmissionCheckpoint)999, hostile, null));
    }

    [Fact]
    public void Protected_reflection_diagnostic_has_exact_method_case_and_closed_context()
    {
        var error = new RpcException(new Status(StatusCode.Unauthenticated, "PROTECTED"));
        var line = ProtectedReflectionDiagnostics.Render(true, HostFailurePhase.ReflectionRead, error, null,
            ReflectionRouteKind.V1, ReflectionCredentialPurpose.StaticDelivery);
        Assert.Equal(ProtectedReflectionDiagnostics.Method + ",enabled-true,reflection-read,v1,static-delivery,unauthenticated,rpc", line);
        var disabled = ProtectedReflectionDiagnostics.Render(false, HostFailurePhase.StatusAssertion,
            new Xunit.Sdk.XunitException("PROTECTED"), StatusCode.Unimplemented,
            ReflectionRouteKind.V1Alpha, ReflectionCredentialPurpose.OperatorAdmin);
        Assert.Equal(ProtectedReflectionDiagnostics.Method + ",enabled-false,status-assertion,v1alpha,operator-admin,unimplemented,assertion", disabled);
        Assert.NotEqual(HostFailureDiagnostics.Method, ProtectedReflectionDiagnostics.Method);
    }

    [Fact]
    public void Protected_reflection_diagnostic_drops_hostile_payload_and_unknown_values()
    {
        const string secret = "PROTECTED,\r\nBearer haby_operator_v1.secret /private/route";
        var error = new RpcException(new Status((StatusCode)999, secret), new Metadata { { "private", "PROTECTED-metadata" } });
        error.Data[secret] = secret;
        var line = ProtectedReflectionDiagnostics.Render(true, (HostFailurePhase)999, error, StatusCode.OK,
            (ReflectionRouteKind)999, (ReflectionCredentialPurpose)999);
        Assert.Equal(ProtectedReflectionDiagnostics.Method + ",enabled-true,unknown,unknown,unknown,unknown-status,rpc", line);
        Assert.DoesNotContain(secret, line);
        Assert.Equal(7, line.Split(',').Length);
        Assert.DoesNotContain('\r', line); Assert.DoesNotContain('\n', line);
        Assert.EndsWith(",unavailable-evidence,other", ProtectedReflectionDiagnostics.Render(false, HostFailurePhase.Build,
            new InvalidOperationException(secret), null, ReflectionRouteKind.None, ReflectionCredentialPurpose.None));
    }

    [Fact]
    public async Task Protected_reflection_nested_scopes_preserve_primary_and_each_cleanup_before_next_scope()
    {
        var evidence = new List<string>();
        var order = new List<string>();
        var primary = new RpcException(new Status(StatusCode.Unauthenticated, "PROTECTED-body"));
        var cleanup = new IOException("PROTECTED-cleanup");
        var diagnostic = new ProtectedReflectionDiagnostics(true, evidence.Add);
        var error = await Record.ExceptionAsync(() => HostFailureDiagnostics.RunAsync(async defer =>
        {
            defer(() => { order.Add("host"); throw cleanup; });
            defer(() => { order.Add("channel"); throw cleanup; });
            // Successful stream disposal must finish before the next credential/route starts.
            await HostFailureDiagnostics.RunAsync(release =>
            {
                release(() => { order.Add("first-stream"); return ValueTask.CompletedTask; });
                return Task.CompletedTask;
            }, () => diagnostic.Phase, () => diagnostic.Observed,
                diagnostic.Recorder(ReflectionRouteKind.V1Alpha, ReflectionCredentialPurpose.Anonymous));
            Assert.Equal(["first-stream"], order);
            await HostFailureDiagnostics.RunAsync(release =>
            {
                diagnostic.At(HostFailurePhase.ReflectionRead, ReflectionRouteKind.V1, ReflectionCredentialPurpose.StaticDelivery);
                release(() => { order.Add("second-stream"); throw cleanup; });
                throw primary;
            }, () => diagnostic.Phase, () => diagnostic.Observed,
                diagnostic.Recorder(ReflectionRouteKind.V1, ReflectionCredentialPurpose.StaticDelivery));
        }, () => diagnostic.Phase, () => diagnostic.Observed, diagnostic.RecordRoot));
        Assert.Same(primary, error);
        Assert.Equal(["first-stream", "second-stream", "channel", "host"], order);
        Assert.Equal(4, evidence.Count);
        Assert.Equal(ProtectedReflectionDiagnostics.Method + ",enabled-true,reflection-read,v1,static-delivery,unauthenticated,rpc", evidence[0]);
        Assert.Equal(ProtectedReflectionDiagnostics.Method + ",enabled-true,cleanup,v1,static-delivery,unavailable-evidence,io", evidence[1]);
        Assert.All(evidence.Skip(2), line => Assert.Equal(ProtectedReflectionDiagnostics.Method + ",enabled-true,cleanup,none,none,unavailable-evidence,io", line));
    }

    [Fact]
    public async Task Protected_reflection_cleanup_only_failure_is_not_relabelled_as_body()
    {
        var evidence = new List<string>();
        var cleanup = new InvalidOperationException("PROTECTED-cleanup");
        var diagnostic = new ProtectedReflectionDiagnostics(false, evidence.Add);
        var error = await Record.ExceptionAsync(() => HostFailureDiagnostics.RunAsync(async _ =>
        {
            diagnostic.At(HostFailurePhase.StatusAssertion, ReflectionRouteKind.V1Alpha, ReflectionCredentialPurpose.OperatorAdmin);
            diagnostic.Observe(StatusCode.Unimplemented);
            await HostFailureDiagnostics.RunAsync(release =>
            {
                release(() => throw cleanup);
                return Task.CompletedTask;
            }, () => diagnostic.Phase, () => diagnostic.Observed,
                diagnostic.Recorder(ReflectionRouteKind.V1Alpha, ReflectionCredentialPurpose.OperatorAdmin));
        }, () => diagnostic.Phase, () => diagnostic.Observed, diagnostic.RecordRoot));
        Assert.Same(cleanup, error);
        Assert.Equal([ProtectedReflectionDiagnostics.Method + ",enabled-false,cleanup,v1alpha,operator-admin,unavailable-evidence,other"], evidence);
    }
}
