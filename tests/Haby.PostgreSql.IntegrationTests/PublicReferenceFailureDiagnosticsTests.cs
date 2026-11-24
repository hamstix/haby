using System.Security.Cryptography;
using System.Text;
using Haby.Operator.Contracts;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class HostFailureDiagnosticsTests
{
    [Fact]
    public async Task Public_Reference_evidence_preserves_primary_cleanup_and_cleanup_only_failure()
    {
        const string secret = "PROTECTED-public-reference-body";
        var lines = new List<string>();
        var diagnostic = new PublicReferenceFailureDiagnostics(lines.Add);
        var primary = new Xunit.Sdk.XunitException(secret);
        var cleanup = new IOException(secret);
        var error = await Record.ExceptionAsync(() => diagnostic.RunAsync(async defer =>
        {
            defer(() => throw cleanup);
            await diagnostic.RunAsync(release =>
            {
                release(() => throw cleanup);
                diagnostic.At(PublicReferenceCheckpoint.PreparationResult);
                throw primary;
            });
        }));
        Assert.Same(primary, error);
        Assert.Equal(3, lines.Count);
        Assert.Contains(",primary,PreparationResult,XunitException,", lines[0]);
        Assert.All(lines.Skip(1), line => Assert.Contains(",cleanup,Cleanup,IOException,", line));
        Assert.All(lines, line => Assert.DoesNotContain(secret, line));
        lines.Clear();
        error = await Record.ExceptionAsync(() => diagnostic.RunAsync(defer =>
        {
            defer(() => throw cleanup);
            return Task.CompletedTask;
        }));
        Assert.Same(cleanup, error);
        Assert.Single(lines);
        var unavailable = new PublicReferenceFailureDiagnostics(_ => throw new HostileSubmissionFailure());
        error = await Record.ExceptionAsync(() => unavailable.RunAsync(_ => throw primary));
        Assert.Same(primary, error);
    }

    [Fact]
    public void Public_Reference_evidence_has_closed_phase_type_values_counts_and_exact_case_identity()
    {
        const string secret = "PROTECTED,\r\nBearer /private/subject";
        var lines = new List<string>();
        var diagnostic = new PublicReferenceFailureDiagnostics(lines.Add);
        diagnostic.Startup(1);
        diagnostic.At(PublicReferenceCheckpoint.PreparationResult);
        var reply = new WorkloadSubmissionReply { Outcome = "stopped", Stage = "Deployment", Progress = new() };
        reply.Progress.Members.Add(new WorkloadSubmissionMember { Documents = "prepared" });
        diagnostic.Observe(reply, 2);
        Assert.Contains(",Stopped,Deployment,1,1,2,", Assert.Single(lines));
        diagnostic.ChildExit(PublicReferenceChild.Restart, 137);
        Assert.EndsWith(",Restart,137", lines.Last());
        Assert.Equal(PublicReferenceFailureDiagnostics.Case, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            PublicReferenceFailureDiagnostics.Method + "\n" + PublicReferenceFailureDiagnostics.Method))).ToLowerInvariant());
        reply.Outcome = secret; reply.Stage = secret; reply.Progress = null;
        diagnostic.Observe(reply, int.MaxValue);
        diagnostic.Startup(999);
        diagnostic.At((PublicReferenceCheckpoint)999);
        var line = diagnostic.Render(secret, new HostileSubmissionFailure(), (PublicReferenceChild)999, int.MinValue);
        Assert.Equal(13, line.Split(',').Length);
        Assert.Contains(",-1,unknown,Unknown,Other,Other,Other,-1,-1,-1,Unknown,-2147483648", line);
        Assert.DoesNotContain(secret, line);
        Assert.DoesNotContain('\r', line); Assert.DoesNotContain('\n', line);
    }
}
