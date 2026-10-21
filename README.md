# Haby

For the single-instance internal alpha operator path, see the
[local host and `habyctl` workflow](docs/development/alpha-operator-local.md).

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
dotnet restore .\HamstixHaby.slnx --artifacts-path .\.artifacts
dotnet build .\HamstixHaby.slnx --no-restore --artifacts-path .\.artifacts
dotnet test .\HamstixHaby.slnx --no-build --artifacts-path .\.artifacts
dotnet format .\HamstixHaby.slnx --verify-no-changes --severity warn
git diff --check
```

The solution contains the current Domain, Manifest, Application and PostgreSQL
projects and their fast tests. The PostgreSQL integration project is separate
because it requires an ephemeral container runtime. Run it where that runtime
is available:

```powershell
dotnet test .\tests\Haby.PostgreSql.IntegrationTests\Haby.PostgreSql.IntegrationTests.csproj --artifacts-path .\.artifacts
```

Windows hosts with WSLc can use the repository's `scripts/test-postgresql-wslc.ps1`
helper. See the [PostgreSQL integration-test guide](docs/development/postgresql-integration-tests.md)
for runtime checks and the supported commands. CI runs this suite in a dedicated
job. See [CONTRIBUTING.md](CONTRIBUTING.md) for contributor workflow.

## License

Haby is licensed under the [Apache License 2.0](LICENSE).
