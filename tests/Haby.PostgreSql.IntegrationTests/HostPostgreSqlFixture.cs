namespace Haby.PostgreSql.IntegrationTests;

// A conservative heavy-group lifetime: includes its dedicated server and every
// Host/CLI process in its cases. Intentional concurrency inside a case is intact.
public sealed class HostPostgreSqlFixture : PostgreSqlFixture
{
    public HostPostgreSqlFixture() : base(control: false, heavyHost: true) { }
    internal HostPostgreSqlFixture(bool control) : base(control: control, heavyHost: true) { }
}
