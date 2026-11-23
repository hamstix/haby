using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Haby.Application;
using Haby.Domain;
using Haby.Persistence.PostgreSql;
using Haby.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed partial class PostgreSqlPairImportTests
{
    [Fact]
    public async Task Server_preparation_observes_exact_oids_and_pins_provider_without_staging_or_external_mutation()
    {
        var pair = await ExistingPair();
        await using var passwordGuard = await PasswordGuard(pair);
        var setup = await Prepare(pair, PostgreSqlTransportMode.Disable);
        var before = await Snapshot(pair);
        await VerificationLogging();
        var request = new ResourceImportPreparationRequest(Guid.NewGuid(), setup.Owner.EnvironmentRecordId,
            setup.Plan.Inputs.Revision, PostgreSqlImportPreparer.Encode(new(pair.Database, pair.Login, "public")));
        OperatorImportRequestBinding binding;
        await using (var services = Services(setup.Connection))
        {
            var result = await services.GetRequiredService<PrepareResourceImport>().ExecuteAsync(request, default);
            Assert.Equal(ResourceImportPreparationStatus.Prepared, result.Status);
            binding = result.Binding!;
            Assert.Equal(request.RequestId, binding.RequestId);
            Assert.All(binding.Input.Resources, x => Assert.Null(x.Credential));
            Assert.All(binding.Input.Resources, x => Assert.Equal(pair.Input, PostgreSqlImportContracts.Decode(x.Input)));
            var accepted = await services.GetRequiredService<IManifestRevisionQuery>().FindAsync(request.Revision);
            var inputs = setup.Plan.Inputs with { Import = binding.Attach(setup.Staged) };
            Assert.NotNull(PostgreSqlImportAdmission.Prepare(accepted!.Declaration.Spec, inputs));
            // Pure admission must refuse the same observed OIDs prepared against another exact Provider revision.
            var changed = binding.Input.Resources.Select(x =>
            {
                var document = JsonNode.Parse(x.Input.Json)!;
                document["providerRevision"] = 999;
                return x with { Input = new(x.Input.Format, document.ToJsonString()) };
            });
            Assert.Null(PostgreSqlImportAdmission.Prepare(accepted.Declaration.Spec,
                inputs with
                {
                    Import = new OperatorImportRequestBinding(binding.RequestId, binding.Environment, binding.Revision,
                    binding.CredentialResource, new(changed)).Attach(setup.Staged)
                }));
            await using var db = await services.GetRequiredService<IDbContextFactory<ManifestCatalogDbContext>>().CreateDbContextAsync();
            Assert.Single(await db.ProtectedApplicationCredentials.AsNoTracking().Select(x => x.Id).ToArrayAsync());
            Assert.Single(await db.DurableChangeSets.AsNoTracking().Select(x => x.Id).ToArrayAsync());
            Assert.Empty(await db.OperatorImportRequestRecords.AsNoTracking().ToArrayAsync());
            Assert.Empty(await db.ResourceObservationReceipts.AsNoTracking().ToArrayAsync());
        }
        Assert.True(before == await Snapshot(pair), "Preparation changed external identities, owners, ACLs or data.");
        Assert.True(await PasswordUnchanged(passwordGuard, pair), "Preparation changed the existing password verifier.");
        var logs = await fixture.ReadServerLogsAsync();
        Assert.False(logs.Contains(pair.Password, StringComparison.Ordinal), "Credential material appeared in server logs.");
        var verification = logs.Split('\n').Where(x => x.StartsWith(PostgreSqlImportVerifier.ApplicationName + "|" + pair.Database + "|", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(verification);
        Assert.False(Regex.IsMatch(string.Join('\n', verification), @"(?:statement:|execute[^:]*:)\s*(?:CREATE|ALTER|GRANT|REVOKE|DROP|TRUNCATE|INSERT|UPDATE|DELETE)\b", RegexOptions.IgnoreCase),
            "Preparation dispatched an external mutation command.");
    }
}
