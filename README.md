# Haby

For the single-instance internal alpha operator path, see the
[local host and `habyctl` workflow](docs/development/alpha-operator-local.md) and
[operator token issuance, recovery and configuration](docs/development/150-operator-access-tokens.md).

Haby is an open source project for describing application configuration and
provisioning resources. It is under active development. The repository currently
provides libraries and tests for a narrow manifest-to-PostgreSQL checkpoint,
not an installable production release.

## What works today

- The `haby.dev/v1alpha1` Application manifest contract has a checked-in JSON
  Schema, deterministic parsing and validation diagnostics, and normalized
  manifest data.
- Application manifest validation and immutable version import are available
  as application-layer use cases. Import is side-effect free and equivalent
  retries preserve the existing revision.
- An in-memory desired-state path can plan and commit an exact Application
  revision. It records the accepted desired generation without durable recovery.
- The PostgreSQL checkpoint executor can create one database after the desired
  commit. Its provider is exercised against ephemeral real PostgreSQL in a
  separate integration suite. This is a limited execution slice, not general
  provisioning or a production-ready PostgreSQL service.

A production server, UI, public gRPC endpoint and general plugin SDK are not
available yet. The implementation remains in-process; it does not provide a
supported deployment or migration path from the removed prototype.

See the [roadmap](ROADMAP.md) and
[Application manifest architecture](docs/architecture/application-manifest.md)
for the accepted design and planned work.

## Build and test

Install the .NET 10 SDK selected by [global.json](global.json). From the
repository root, run:

```powershell
$env:HABY_TEST_SOURCE_ROOT = (Get-Location).Path
dotnet restore .\HamstixHaby.slnx --artifacts-path .\.artifacts
dotnet build .\HamstixHaby.slnx --no-restore --artifacts-path .\.artifacts
dotnet test .\HamstixHaby.slnx --no-build --artifacts-path .\.artifacts
dotnet format .\HamstixHaby.slnx --verify-no-changes --severity warn
git diff --check
```

The solution contains the current Domain, Manifest, Application and PostgreSQL
projects and their fast tests. The PostgreSQL integration project is separate
because it requires an ephemeral container runtime. The primary complete gate is
the [hosted Linux suite entrypoint](docs/development/197-suite-routing.md), including
required native CLI publication and functional/qualification accounting. Direct
project execution remains available for focused diagnostics, not full-gate evidence:

```powershell
dotnet test .\tests\Haby.PostgreSql.IntegrationTests\Haby.PostgreSql.IntegrationTests.csproj --artifacts-path .\.artifacts
```

Windows contributors can invoke/inspect Actions without a duplicate local full
PostgreSQL run. The `scripts/test-postgresql-wslc.ps1` helper remains explicitly
legacy; its shared-server mode cannot establish fresh-server qualification. See
the [PostgreSQL integration-test guide](docs/development/postgresql-integration-tests.md)
for supported diagnostics and [CONTRIBUTING.md](CONTRIBUTING.md) for contributor workflow.

## License

Haby is licensed under the [Apache License 2.0](LICENSE).
