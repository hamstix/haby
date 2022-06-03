﻿// <auto-generated />
using Hamstix.Haby.Server.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Hamstix.Haby.Server.Migrations
{
    [DbContext(typeof(HabbyContext))]
    [Migration("20220531181118_m002_Change_ConfigurationKey_PK")]
    partial class m002_Change_ConfigurationKey_PK
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "6.0.5")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("Hamstix.Haby.Server.Models.ConfigurationKey", b =>
                {
                    b.Property<long>("ConfigurationUnitId")
                        .HasColumnType("bigint");

                    b.Property<string>("Name")
                        .HasMaxLength(255)
                        .HasColumnType("character varying(255)");

                    b.Property<string>("Configuration")
                        .IsRequired()
                        .ValueGeneratedOnAdd()
                        .HasColumnType("jsonb")
                        .HasDefaultValueSql("'{}'::jsonb");

                    b.HasKey("ConfigurationUnitId", "Name");

                    b.ToTable("ConfigurationKeys");
                });

            modelBuilder.Entity("Hamstix.Haby.Server.Models.ConfigurationUnit", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("bigint");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<long>("Id"));

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<string>("PreviousVersion")
                        .HasMaxLength(30)
                        .HasColumnType("character varying(30)");

                    b.Property<string>("Template")
                        .IsRequired()
                        .ValueGeneratedOnAdd()
                        .HasColumnType("jsonb")
                        .HasDefaultValueSql("'[]'::jsonb");

                    b.Property<string>("Version")
                        .IsRequired()
                        .HasMaxLength(30)
                        .HasColumnType("character varying(30)");

                    b.HasKey("Id");

                    b.ToTable("ConfigurationUnits");
                });

            modelBuilder.Entity("Hamstix.Haby.Server.Models.ConfigurationUnitAtService", b =>
                {
                    b.Property<long>("ServiceId")
                        .HasColumnType("bigint");

                    b.Property<long>("ConfigurationUnitId")
                        .HasColumnType("bigint");

                    b.Property<string>("Key")
                        .HasColumnType("text");

                    b.Property<string>("RenderedTemplateJson")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("jsonb")
                        .HasDefaultValueSql("'{}'::jsonb");

                    b.HasKey("ServiceId", "ConfigurationUnitId", "Key");

                    b.HasIndex("ConfigurationUnitId");

                    b.ToTable("ConfigurationUnitsAtServices");
                });

            modelBuilder.Entity("Hamstix.Haby.Server.Models.RegConfiguration", b =>
                {
                    b.Property<string>("Key")
                        .HasMaxLength(255)
                        .HasColumnType("character varying(255)");

                    b.Property<string>("Value")
                        .IsRequired()
                        .HasColumnType("text");

                    b.HasKey("Key");

                    b.ToTable("RegConfiguration");

                    b.HasData(
                        new
                        {
                            Key = "initialized",
                            Value = "false"
                        });
                });

            modelBuilder.Entity("Hamstix.Haby.Server.Models.Service", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("bigint");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<long>("Id"));

                    b.Property<string>("JsonConfig")
                        .IsRequired()
                        .ValueGeneratedOnAdd()
                        .HasColumnType("jsonb")
                        .HasDefaultValueSql("'{}'::jsonb");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(255)
                        .HasColumnType("character varying(255)");

                    b.Property<string>("PluginName")
                        .HasColumnType("text");

                    b.Property<string>("Template")
                        .HasColumnType("text");

                    b.HasKey("Id");

                    b.ToTable("Services");
                });

            modelBuilder.Entity("Hamstix.Haby.Server.Models.Variable", b =>
                {
                    b.Property<long>("ServiceId")
                        .HasColumnType("bigint");

                    b.Property<long>("ConfigurationUnitId")
                        .HasColumnType("bigint");

                    b.Property<string>("Key")
                        .HasColumnType("text");

                    b.Property<string>("Name")
                        .HasColumnType("text");

                    b.Property<int>("Type")
                        .HasColumnType("integer");

                    b.Property<string>("Value")
                        .IsRequired()
                        .ValueGeneratedOnAdd()
                        .HasColumnType("jsonb")
                        .HasDefaultValueSql("'{}'::jsonb");

                    b.HasKey("ServiceId", "ConfigurationUnitId", "Key", "Name");

                    b.ToTable("Variables");
                });

            modelBuilder.Entity("Hamstix.Haby.Server.Models.ConfigurationKey", b =>
                {
                    b.HasOne("Hamstix.Haby.Server.Models.ConfigurationUnit", "ConfigurationUnit")
                        .WithMany("Keys")
                        .HasForeignKey("ConfigurationUnitId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("ConfigurationUnit");
                });

            modelBuilder.Entity("Hamstix.Haby.Server.Models.ConfigurationUnitAtService", b =>
                {
                    b.HasOne("Hamstix.Haby.Server.Models.ConfigurationUnit", "ConfigurationUnit")
                        .WithMany("Services")
                        .HasForeignKey("ConfigurationUnitId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("Hamstix.Haby.Server.Models.Service", "Service")
                        .WithMany("ConfigurationUnits")
                        .HasForeignKey("ServiceId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("ConfigurationUnit");

                    b.Navigation("Service");
                });

            modelBuilder.Entity("Hamstix.Haby.Server.Models.Variable", b =>
                {
                    b.HasOne("Hamstix.Haby.Server.Models.ConfigurationUnitAtService", "ConfigurationUnitAtService")
                        .WithMany("Variables")
                        .HasForeignKey("ServiceId", "ConfigurationUnitId", "Key")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("ConfigurationUnitAtService");
                });

            modelBuilder.Entity("Hamstix.Haby.Server.Models.ConfigurationUnit", b =>
                {
                    b.Navigation("Keys");

                    b.Navigation("Services");
                });

            modelBuilder.Entity("Hamstix.Haby.Server.Models.ConfigurationUnitAtService", b =>
                {
                    b.Navigation("Variables");
                });

            modelBuilder.Entity("Hamstix.Haby.Server.Models.Service", b =>
                {
                    b.Navigation("ConfigurationUnits");
                });
#pragma warning restore 612, 618
        }
    }
}
