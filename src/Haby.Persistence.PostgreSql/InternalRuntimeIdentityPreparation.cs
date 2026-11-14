using System.Security.Cryptography;
using System.Text;
using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed record PreparedRuntimeIdentity(ProviderInput.Workload Workload, InternalActionResult Result, Guid? Binding);

/// <summary>Trusted accepted-generation composition; no caller-selected document or trust context.</summary>
internal sealed class InternalRuntimeIdentityPreparation(IDbContextFactory<ManifestCatalogDbContext> contexts,
    InternalWorkloadConfiguration configuration, InternalDurableActions actions, string identityAction)
{
    internal async Task<IReadOnlyList<PreparedRuntimeIdentity>> PrepareAsync(DurableChangeSetCommit accepted, CancellationToken token)
    {
        ProviderInput.Workload[] subjects;
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            var source = await InternalGenerationAuthority.RequireAsync(db, accepted, token);
            subjects = await configuration.RequireCompleteAsync(db, source, token);
        }
        var results = new List<PreparedRuntimeIdentity>();
        foreach (var subject in subjects.OrderBy(x => x.Component.Value, StringComparer.Ordinal).ThenBy(x => x.Id.Value, StringComparer.Ordinal))
        {
            var request = "runtime-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subject.Component.Value + "/" + subject.Id.Value)));
            var key = await actions.AcceptAsync(accepted, subject, identityAction, request, token);
            var result = await actions.ExecuteAsync(key, token);
            Guid? binding = null;
            if (result.Outcome == InternalActionOutcome.Confirmed)
            {
                await using var db = await contexts.CreateDbContextAsync(token);
                var source = await InternalGenerationAuthority.RequireAsync(db, accepted, token);
                await configuration.RequireCompleteAsync(db, source, token);
                binding = await (from row in db.RuntimeIdentityBindings.AsNoTracking()
                                 join workload in db.ManagedWorkloads on row.ManagedWorkloadId equals workload.Id
                                 where workload.DeploymentId == source.DeploymentId && workload.Component == subject.Component.Value && workload.Workload == subject.Id.Value
                                 select (Guid?)row.Id).SingleAsync(token);
            }
            results.Add(new(subject, result, binding));
            if (result.Outcome != InternalActionOutcome.Confirmed) break;
        }
        return results;
    }
}
