# In-memory desired-state checkpoint

`InMemoryDesiredStateStore` implements the one-Application, one-Environment
`DeployRevision` path from ADR-016. A caller imports a manifest with
`ImportApplicationManifest`, creates a ChangeSet for its exact
`ManifestRevisionRef`, plans that ChangeSet, then commits the Plan. Import and
planning do not create an ApplicationDeployment. Commit accepts desired state
atomically and records an immutable generation snapshot; it does not call a
Provider or advance `AppliedGeneration`.

The caller supplies an explicit `EnvironmentRecordId` and either
`ExpectedAbsence` or `ExpectedGeneration`. The fake Environment catalog maps
Provider aliases to exact Provider instance, configuration revision and
implementation identities. Planning captures those results. Apply checks the
ChangeSet precondition before comparing the current exact planning inputs to
the Plan. A completed ChangeSet returns its original commit on retry.

This checkpoint cannot yet resolve cross-Application export Bindings or
parameter definitions and assignments. Such declarations return the typed
`UnsupportedDependency` planning result and cannot be committed. This result
has no stable public error code; the ADR-018 export planner and ADR-019
parameter resolver are separate follow-up contracts. Missing Provider aliases
return `ProviderAliasNotResolved`.

The store is a test boundary, not durable persistence, reconciliation or a
public transport API. It supports one exact member and present desired state;
multi-Application transactions and explicit removal remain future slices.

`PostgreSqlCheckpointExecutor` is invoked separately after Commit for a
generation containing exactly one `postgresql.database/v1alpha1` Resource. It
uses the Plan's exact Provider selection, records local execution status, and
advances `AppliedGeneration` only after its Provider confirms creation. A
confirmed retry performs no further SQL; an uncertain result requires attention.
The build-time-composed implementation and its operator-owned connection
configuration live in `Haby.PostgreSql`, outside this assembly.
