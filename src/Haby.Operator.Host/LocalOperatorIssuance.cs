using System.Globalization;
using System.Text;
using Haby.Application;
using Haby.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;

namespace Haby.Operator.Host;

/// <summary>Explicit privileged local operation. Never starts a web Host or listener.</summary>
public static class LocalOperatorIssuance
{
    public const string LocalActor = "haby-local-operator-issuance";
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken token = default)
    {
        FileStream? destination = null;
        string? grantId = null;
        var attempted = false;
        try
        {
            string? file = null, name = null, expiryText = null;
            var stdout = false;
            var settings = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--token-stdout": if (stdout) throw new ArgumentException(); stdout = true; break;
                    case "--token-output-file": if (file is not null || ++i >= args.Length) throw new ArgumentException(); file = args[i]; break;
                    case "--display-name": if (name is not null || ++i >= args.Length) throw new ArgumentException(); name = args[i]; break;
                    case "--expires-at": if (expiryText is not null || ++i >= args.Length) throw new ArgumentException(); expiryText = args[i]; break;
                    default:
                        if (!args[i].StartsWith("--Haby:PostgreSql:ConnectionString=", StringComparison.Ordinal)) throw new ArgumentException();
                        settings.Add(args[i]); break;
                }
            }
            if ((file is null) == !stdout || name is not null && (name.Length is < 1 or > 128 || name.Any(char.IsControl))) throw new ArgumentException();
            DateTimeOffset? expiry = null;
            if (expiryText is not null)
            {
                if (!(expiryText.EndsWith('Z') || expiryText.EndsWith("+00:00", StringComparison.Ordinal)) ||
                    !DateTimeOffset.TryParse(expiryText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) || parsed.Offset != TimeSpan.Zero || parsed <= DateTimeOffset.UtcNow)
                    throw new ArgumentException();
                expiry = parsed;
            }
            // Configuration is shared with ordinary Host startup; no listener/TLS settings are needed.
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(settings.ToArray());
            builder.Logging.ClearProviders();
            var connection = builder.Configuration["Haby:PostgreSql:ConnectionString"];
            if (string.IsNullOrWhiteSpace(connection)) throw new ArgumentException();
            builder.Services.AddDbContextFactory<ManifestCatalogDbContext>(o => o.UseNpgsql(connection));
            builder.Services.AddSingleton<ManifestCatalogMigrationInitializer>();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<IOperatorAccessStore, PostgreSqlOperatorAccessStore>();
            using var services = builder.Build();
            if (file is not null)
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                destination = new FileStream(file, options);
            }
            await services.Services.GetRequiredService<ManifestCatalogMigrationInitializer>().MigrateAsync(token);
            attempted = true;
            var created = await services.Services.GetRequiredService<IOperatorAccessStore>().CreateAsync(name, LocalActor, expiry, token);
            grantId = created.Grant.Id.ToString();
            if (destination is not null)
            {
                var bytes = Encoding.ASCII.GetBytes(created.Credential + "\n");
                try { await destination.WriteAsync(bytes, token); await destination.FlushAsync(token); destination.Flush(true); }
                finally { Array.Clear(bytes); }
                await destination.DisposeAsync(); destination = null;
                output.WriteLine("Operator access created; grant=" + grantId);
            }
            else
            {
                error.WriteLine("Operator access created; grant=" + grantId + ". Retain the credential durably.");
                output.WriteLine(created.Credential);
            }
            return 0;
        }
        catch (Exception)
        {
            error.WriteLine(attempted
                ? "Credential transfer was not confirmed. A grant may be active" + (grantId is null ? "." : ": " + grantId + ".") + " No rollback, token recovery or automatic retry. Use authenticated operator access list/get/revoke, or explicit local recovery issuance."
                : "Local issuance failed before token creation. Check catalog settings, future UTC expiry and exactly one --token-output-file <new-path> or --token-stdout.");
            return 2;
        }
        finally
        {
            if (destination is not null)
            {
                try { await destination.DisposeAsync(); }
                catch (Exception) { error.WriteLine("Credential file close failed. Inspect issuance state before recovery; no rollback or automatic retry."); }
            }
        }
    }
}
