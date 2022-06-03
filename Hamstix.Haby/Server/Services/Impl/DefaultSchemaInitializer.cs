using Hamstix.Haby.Server.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Hamstix.Haby.Server.Services.Impl
{
    public class DefaultSchemaInitializer : ISchemaInitializer
    {
        readonly DbContextOptions<HabbyContext> _contextOptions;

        public DefaultSchemaInitializer(DbContextOptions<HabbyContext> contextOptions)
        {
            _contextOptions = contextOptions;
        }

        public async Task InitializeSchema()
        {
            using var context = new HabbyContext(_contextOptions);
            var migrator = context.Database.GetService<IMigrator>();
            await migrator.MigrateAsync();
        }

        public async Task<bool> IsSchemaInitialized()
        {
            using var context = new HabbyContext(_contextOptions);
            var creatorContext = context.Database.GetService<IRelationalDatabaseCreator>();

            // TODO: Check if the schema is ours and don't contains other tables.
            return await creatorContext.HasTablesAsync();
        }
    }
}
