using Haby.Application;
using Haby.Operator.Host;
using Haby.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

public sealed class FiveStepCompositionTests
{
    [Theory]
    [InlineData("admission")]
    [InlineData("handler")]
    [InlineData("output")]
    public void Ambiguous_real_production_registration_refuses_Host_construction(string kind)
    {
        var error = Assert.Throws<InvalidOperationException>(() => OperatorHostApplication.Build(
            ["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=0", "--Haby:Tls:Enabled=false"], services =>
            {
                var descriptor = Assert.Single(services, d => kind switch
                {
                    "admission" => d.ServiceType == typeof(IDurableActionAdmissionHandler) && d.ImplementationType == typeof(PostgreSqlDatabaseCreateAdmission),
                    "handler" => d.ServiceType == typeof(IDurableActionHandler) && d.ImplementationType == typeof(PostgreSqlApplicationAccessAction),
                    _ => d.ServiceType == typeof(IExternalOperationOutputContract) && d.ImplementationType == typeof(PostgreSqlApplicationConnectionContract)
                });
                services.Add(descriptor);
            }));
        Assert.Contains("ambiguous", error.Message);
    }

    [Fact]
    public async Task Production_composes_two_exact_admissions_five_real_actions_and_three_external_outputs()
    {
        await using var host = OperatorHostApplication.Build(["--Haby:PostgreSql:ConnectionString=Host=localhost", "--Haby:Port=0", "--Haby:Tls:Enabled=false"]);
        var admissions = host.Services.GetServices<IDurableActionAdmissionHandler>().ToArray();
        Assert.Equal(2, admissions.Count(a => a is not PostgreSqlImportAdmission));
        Assert.Equal(2, admissions.Where(a => a is not PostgreSqlImportAdmission).Select(a => a.Key.Admission).Distinct().Count());
        Assert.Equal(2, admissions.Count(a => a is PostgreSqlImportAdmission));
        Assert.Equal(4, admissions.Select(a => a.Key).Distinct().Count());
        var handlers = host.Services.GetServices<IDurableActionHandler>().ToArray();
        Assert.Equal(5, handlers.Count(h => h is not PostgreSqlImportAction));
        Assert.Equal(2, handlers.Count(h => h is PostgreSqlImportAction));
        Assert.Contains(handlers, h => h is PostgreSqlDatabaseCreateAction);
        Assert.Contains(handlers, h => h is PostgreSqlDatabaseRestrictAction);
        Assert.Contains(handlers, h => h is GeneratedCredentialAction);
        Assert.Contains(handlers, h => h is PostgreSqlApplicationLoginAction);
        Assert.Contains(handlers, h => h is PostgreSqlApplicationAccessAction);
        var outputs = host.Services.GetServices<IExternalOperationOutputContract>().ToArray();
        Assert.Equal(3, outputs.Count(o => o is not PostgreSqlImportOutputContract));
        Assert.Equal(2, outputs.Count(o => o is PostgreSqlImportOutputContract));
    }
}
