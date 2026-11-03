using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Haby.PostgreSql.IntegrationTests;

// Test-only EF migration fixtures. Runtime migration discovery is EF-owned reflection;
// no application production assembly discovers or activates types by reflection.
public sealed class OwnerMigrationContext(DbContextOptions<OwnerMigrationContext> options) : DbContext(options)
{
    public DbSet<OwnerMigrationItem> Items => Set<OwnerMigrationItem>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("migration_owned");
        modelBuilder.HasSequence<long>("item_sequence");
        modelBuilder.Entity<OwnerMigrationItem>(b =>
        {
            b.ToTable("items"); b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasDefaultValueSql("nextval('migration_owned.item_sequence')");
            b.Property(x => x.Name).IsRequired(); b.HasIndex(x => x.Name).IsUnique();
            b.Property(x => x.Revision).HasDefaultValue(0);
        });
    }
}
public sealed class OwnerMigrationItem
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int Revision { get; set; }
}

[DbContext(typeof(OwnerMigrationContext))]
[Migration("20261103000100_OwnerInitial")]
public sealed class OwnerInitialMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema("migration_owned");
        migrationBuilder.CreateSequence<long>("item_sequence", "migration_owned");
        migrationBuilder.CreateTable("items", schema: "migration_owned", columns: table => new
        {
            Id = table.Column<long>(nullable: false, defaultValueSql: "nextval('migration_owned.item_sequence')"),
            Name = table.Column<string>(nullable: false)
        }, constraints: table => table.PrimaryKey("PK_items", x => x.Id));
        migrationBuilder.CreateIndex("IX_items_Name", "items", "Name", "migration_owned", unique: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException();
}
[DbContext(typeof(OwnerMigrationContext))]
[Migration("20261103000200_OwnerRevision")]
public sealed class OwnerRevisionMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<int>("Revision", "items", schema: "migration_owned", nullable: false, defaultValue: 0);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException();
}

internal static class OwnerMigrationEvidence
{
    internal static async Task RunAsync(string connection)
    {
        var options = new DbContextOptionsBuilder<OwnerMigrationContext>().UseNpgsql(connection,
            p => p.MigrationsHistoryTable("__EFMigrationsHistory", "migration_owned")).Options;
        await using (var first = new OwnerMigrationContext(options))
        {
            await first.GetService<IMigrator>().MigrateAsync("20261103000100_OwnerInitial");
            Assert.Single(await first.Database.GetAppliedMigrationsAsync());
        }
        await using (var second = new OwnerMigrationContext(options))
        {
            await second.Database.MigrateAsync();
            Assert.Equal(2, (await second.Database.GetAppliedMigrationsAsync()).Count());
        }
        long id;
        await using (var insert = new OwnerMigrationContext(options))
        {
            var item = new OwnerMigrationItem { Name = "inserted" }; insert.Items.Add(item);
            await insert.SaveChangesAsync(); id = item.Id; Assert.True(id > 0);
        }
        await using (var read = new OwnerMigrationContext(options))
            Assert.Equal("inserted", (await read.Items.AsNoTracking().SingleAsync(x => x.Id == id)).Name);
        await using (var update = new OwnerMigrationContext(options))
            Assert.Equal(1, await update.Items.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "updated").SetProperty(x => x.Revision, 2)));
        await using (var verify = new OwnerMigrationContext(options))
        {
            var item = await verify.Items.AsNoTracking().SingleAsync(); Assert.Equal("updated", item.Name); Assert.Equal(2, item.Revision);
        }
        await using (var delete = new OwnerMigrationContext(options))
            Assert.Equal(1, await delete.Items.Where(x => x.Id == id).ExecuteDeleteAsync());
        await using (var verify = new OwnerMigrationContext(options)) Assert.Empty(await verify.Items.AsNoTracking().ToArrayAsync());
    }
}
