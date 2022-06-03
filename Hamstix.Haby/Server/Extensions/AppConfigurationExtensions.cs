using Hamstix.Haby.Server.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Hamstix.Haby.Server.Extensions
{
    public static class AppConfigurationExtensions
    {
        /// <summary>
        /// Read the PostgreSQL connection string from the Environment variable or from the configuration.
        /// </summary>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static string ReadPgConnectionString(this IConfiguration configuration)
        {
            var connectionString = Environment.GetEnvironmentVariable(AppConstants.EnvVariables.PgConnectionString);
            if (!string.IsNullOrEmpty(connectionString))
                return connectionString;

            connectionString = configuration[AppConstants.Configuration.ConnectionString];
            if (!string.IsNullOrEmpty(connectionString))
                return connectionString;

            return string.Empty;
        }

        /// <summary>
        /// Do migration of the <see cref="HabbyContext"/>.
        /// </summary>
        /// <param name="app"></param>
        public static void UpdateDatabase(this IApplicationBuilder app)
        {
            using (var serviceScope = app.ApplicationServices
                .GetRequiredService<IServiceScopeFactory>()
                .CreateScope())
            {
                using (var context = serviceScope.ServiceProvider.GetService<HabbyContext>())
                {
                    context.Database.Migrate();
                }
            }
        }
    }
}
