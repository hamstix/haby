using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haby.Persistence.PostgreSql.Migrations;

[DbContext(typeof(ManifestCatalogDbContext))]
[Migration("20261116210000_AddConfigurationSecretPurpose")]
public sealed class AddConfigurationSecretPurpose : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
              IF EXISTS (SELECT 1 FROM deployment_plans)
                 OR EXISTS (SELECT 1 FROM application_deployment_generations)
                 OR EXISTS (SELECT 1 FROM configuration_document_revisions) THEN
                RAISE EXCEPTION 'An explicit fresh catalog is required for configuration planning/document formats.';
              END IF;
            END $$;
            ALTER TABLE secrets DROP CONSTRAINT ck_secrets_typed_shape;
            ALTER TABLE secrets ADD CONSTRAINT ck_secrets_typed_shape CHECK (
              sensitivity = 'Credential' AND content_type = 'application/octet-stream'
              AND storage_backend = 'PostgreSqlPlaintext' AND ownership_policy = 'Managed'
              AND ((owner = 'Environment' AND purpose IN ('ProviderAdministrator', 'Configuration')
                    AND application_record_id IS NULL AND resource_local_id IS NULL)
                OR (owner = 'Resource' AND purpose = 'ApplicationLogin' AND name IS NULL
                    AND application_record_id IS NOT NULL AND resource_local_id IS NOT NULL
                    AND resource_local_id ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$')));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
              IF EXISTS (SELECT 1 FROM secrets WHERE purpose = 'Configuration') THEN
                RAISE EXCEPTION 'Configuration secrets prevent downgrade.';
              END IF;
            END $$;
            ALTER TABLE secrets DROP CONSTRAINT ck_secrets_typed_shape;
            ALTER TABLE secrets ADD CONSTRAINT ck_secrets_typed_shape CHECK (
              sensitivity = 'Credential' AND content_type = 'application/octet-stream'
              AND storage_backend = 'PostgreSqlPlaintext' AND ownership_policy = 'Managed'
              AND ((owner = 'Environment' AND purpose = 'ProviderAdministrator'
                    AND application_record_id IS NULL AND resource_local_id IS NULL)
                OR (owner = 'Resource' AND purpose = 'ApplicationLogin' AND name IS NULL
                    AND application_record_id IS NOT NULL AND resource_local_id IS NOT NULL
                    AND resource_local_id ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$')));
            """);
    }
}
