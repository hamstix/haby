using Hamstix.Haby.Server.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Configuration
{
#pragma warning disable CS1591 // Отсутствует комментарий XML для открытого видимого типа или члена

    public class HabbyContext : DbContext
    {
        public DbSet<ConfigurationUnit> ConfigurationUnits => Set<ConfigurationUnit>();
        public DbSet<ConfigurationKey> ConfigurationKeys => Set<ConfigurationKey>();
        public DbSet<ConfigurationUnitParameter> ConfigurationUnitParameters => Set<ConfigurationUnitParameter>();
        public DbSet<ConfigurationUnitAtService> ConfigurationUnitsAtServices => Set<ConfigurationUnitAtService>();
        public DbSet<RegConfiguration> RegConfiguration => Set<RegConfiguration>();
        public DbSet<Service> Services => Set<Service>();
        public DbSet<Variable> Variables => Set<Variable>();
        public DbSet<Generator> Generators => Set<Generator>();
        public DbSet<SystemVariable> SystemVariables => Set<SystemVariable>();

        static readonly JsonSerializerOptions _jsonIntendedOptions = new JsonSerializerOptions { WriteIndented = true };

        public HabbyContext(DbContextOptions<HabbyContext> options)
                : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ConfigurationUnit>(entity =>
            {
                entity.HasKey(x => x.Id);

                entity.Property(val => val.Id)
                    .ValueGeneratedOnAdd();

                entity.Property(val => val.Version)
                    .IsRequired()
                    .HasMaxLength(30);

                entity.Property(val => val.PreviousVersion)
                    .IsRequired(false)
                    .HasMaxLength(30);

                entity.HasMany(x => x.Keys)
                    .WithOne(x => x.ConfigurationUnit)
                    .HasForeignKey(x => x.ConfigurationUnitId)
                    .HasPrincipalKey(x => x.Id)
                    .IsRequired();

                entity.Property(val => val.Template)
                    .HasConversion(val =>
                        val.ToJsonString(_jsonIntendedOptions),
                        dbVal => JsonNode.Parse(dbVal, default(JsonNodeOptions), default(JsonDocumentOptions)).AsArray()
                    )
                    .HasColumnType("jsonb")
                    .HasDefaultValueSql("'[]'::jsonb")
                    .IsRequired();

                entity.HasMany(x => x.Services)
                    .WithOne(x => x.ConfigurationUnit)
                    .HasForeignKey(x => x.ConfigurationUnitId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasMany(x => x.Parameters)
                    .WithOne(x => x.ConfigurationUnit)
                    .HasForeignKey(x => x.ConfigurationUnitId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<ConfigurationKey>(entity =>
            {
                entity.HasKey(x => new { x.ConfigurationUnitId, x.Name });

                entity.Property(val => val.Name)
                    .HasMaxLength(byte.MaxValue)
                    .ValueGeneratedNever();

                entity.Property(val => val.Configuration)
                    .HasConversion(val =>
                        val.ToJsonString(_jsonIntendedOptions),
                        dbVal => JsonNode.Parse(dbVal, default(JsonNodeOptions), default(JsonDocumentOptions))!.AsObject()
                    )
                    .HasColumnType("jsonb")
                    .HasDefaultValueSql("'{}'::jsonb")
                    .IsRequired();
            });

            modelBuilder.Entity<ConfigurationUnitAtService>(entity =>
            {
                entity.HasKey(val => new { val.ServiceId, val.ConfigurationUnitId, val.Key });

                entity.Property(val => val.RenderedTemplateJson)
                   .HasConversion(val =>
                       val.ToJsonString(_jsonIntendedOptions),
                       dbVal => JsonNode.Parse(dbVal, default(JsonNodeOptions), default(JsonDocumentOptions))
                   )
                   .HasColumnType("jsonb")
                   .HasDefaultValueSql("'{}'::jsonb")
                   .IsRequired(false);

                entity.HasMany(x => x.Variables)
                    .WithOne(x => x.ConfigurationUnitAtService)
                    .HasForeignKey(x => new { x.ServiceId, x.ConfigurationUnitId, x.Key })
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<Variable>(entity =>
            {
                entity.HasKey(val => new { val.ServiceId, val.ConfigurationUnitId, val.Key, val.Name });

                entity.Property(val => val.Value)
                   .HasConversion(val =>
                       val.ToJsonString(_jsonIntendedOptions),
                       dbVal => JsonNode.Parse(dbVal, default(JsonNodeOptions), default(JsonDocumentOptions))
                   )
                   .HasColumnType("jsonb")
                   .HasDefaultValueSql("'{}'::jsonb")
                   .IsRequired();
            });

            modelBuilder.Entity<ConfigurationUnitParameter>(entity =>
            {
                entity.HasKey(val => new { val.ConfigurationUnitId, val.Key, val.Name });

                entity.Property(val => val.Value)
                   .HasConversion(val =>
                       val.ToJsonString(_jsonIntendedOptions),
                       dbVal => JsonNode.Parse(dbVal, default(JsonNodeOptions), default(JsonDocumentOptions))
                   )
                   .HasColumnType("jsonb")
                   .HasDefaultValueSql("'{}'::jsonb")
                   .IsRequired();
            });

            modelBuilder.Entity<ConfigurationUnitParameter>(entity =>
            {
                entity.HasKey(x => new { x.ConfigurationUnitId, x.Name, x.Key });
            });

            modelBuilder.Entity<RegConfiguration>(entity =>
            {
                entity.HasKey(x => x.Key);

                entity.Property(val => val.Key)
                    .HasMaxLength(byte.MaxValue)
                    .ValueGeneratedNever();
            });

            modelBuilder.Entity<Service>(entity =>
            {
                entity.HasKey(x => x.Id);

                entity.Property(val => val.Id)
                    .ValueGeneratedOnAdd();

                entity.Property(val => val.Name)
                    .HasMaxLength(byte.MaxValue)
                    .IsRequired();

                entity.Property(val => val.JsonConfig)
                    .HasConversion(val =>
                        val.ToJsonString(_jsonIntendedOptions),
                        dbVal => JsonObject.Parse(dbVal, default(JsonNodeOptions), default(JsonDocumentOptions)).AsObject()
                    )
                    .HasColumnType("jsonb")
                    .HasDefaultValueSql("'{}'::jsonb")
                    .IsRequired();

                entity.HasMany(x => x.ConfigurationUnits)
                    .WithOne(x => x.Service)
                    .HasForeignKey(x => x.ServiceId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<Generator>(entity =>
            {
                entity.HasKey(x => x.Id);

                entity.Property(val => val.Id)
                    .ValueGeneratedOnAdd();

                entity
                    .HasIndex(x => x.Name)
                    .IsUnique();
            });

            modelBuilder.Entity<SystemVariable>(entity =>
            {
                entity.HasKey(x => x.Id);

                entity.Property(val => val.Id)
                    .ValueGeneratedOnAdd();

                entity.Property(val => val.Value)
                   .HasConversion(val =>
                       val.ToJsonString(_jsonIntendedOptions),
                       dbVal => JsonNode.Parse(dbVal, default(JsonNodeOptions), default(JsonDocumentOptions))
                   )
                   .HasColumnType("jsonb")
                   .HasDefaultValueSql("'{}'::jsonb")
                   .IsRequired();
            });
        }
    }
}
