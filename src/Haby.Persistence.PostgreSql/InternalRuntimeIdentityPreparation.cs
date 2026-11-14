using System.Security.Cryptography;
using System.Text;
using Haby.Application;
using Haby.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haby.Persistence.PostgreSql;

internal sealed record PreparedRuntimeIdentity(ProviderInput.Workload Workload, InternalActionResult Result, Guid? Binding,
    RuntimeBindingState? CurrentBinding = null);

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
            // Inspect every existing binding before any account action. A revoked binding
            // is authentic retained history, never an invitation to create a replacement.
            foreach (var subject in subjects)
            {
                var existing = await (from row in db.RuntimeIdentityBindings.AsNoTracking()
                                      join workload in db.ManagedWorkloads on row.ManagedWorkloadId equals workload.Id
                                      where workload.DeploymentId == source.DeploymentId && workload.Component == subject.Component.Value && workload.Workload == subject.Id.Value
                                      select row).SingleOrDefaultAsync(token);
                if (existing is null) continue;
                var state = InternalRuntimePublication.BindingState(existing);
                await actions.RequireIdentityOriginAsync(db, existing, token);
                if (state.Status == "Revoked") return [new(subject, new(InternalActionOutcome.Refused, "runtime-binding-revoked"), existing.Id, state)];
            }
        }
        var results = new List<PreparedRuntimeIdentity>();
        foreach (var subject in subjects.OrderBy(x => x.Component.Value, StringComparer.Ordinal).ThenBy(x => x.Id.Value, StringComparer.Ordinal))
        {
            var request = "runtime-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subject.Component.Value + "/" + subject.Id.Value)));
            var key = await actions.AcceptAsync(accepted, subject, identityAction, request, token);
            var result = await actions.ExecuteAsync(key, token);
            Guid? binding = null;
            RuntimeBindingState? current = null;
            if (result.Outcome == InternalActionOutcome.Confirmed)
            {
                await using var db = await contexts.CreateDbContextAsync(token);
                var source = await InternalGenerationAuthority.RequireAsync(db, accepted, token);
                await configuration.RequireCompleteAsync(db, source, token);
                var saved = await (from row in db.RuntimeIdentityBindings.AsNoTracking()
                                   join workload in db.ManagedWorkloads on row.ManagedWorkloadId equals workload.Id
                                   where workload.DeploymentId == source.DeploymentId && workload.Component == subject.Component.Value && workload.Workload == subject.Id.Value
                                   select row).SingleAsync(token);
                binding = saved.Id; current = InternalRuntimePublication.BindingState(saved);
                if (current.Status == "Revoked") result = new(InternalActionOutcome.Refused, "runtime-binding-revoked");
            }
            results.Add(new(subject, result, binding, current));
            if (result.Outcome != InternalActionOutcome.Confirmed) break;
        }
        return results;
    }
}
