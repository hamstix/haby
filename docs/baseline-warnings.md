# M0 baseline warnings

The initial M0 build was captured on September 16, 2026 with .NET SDK 10.0.401.

```powershell
dotnet restore .\HamstixHaby.slnx --artifacts-path .\.artifacts
dotnet build .\HamstixHaby.slnx --no-restore --artifacts-path .\.artifacts
```

The pinned-SDK clean build succeeds with 96 warnings and no errors. These are existing issues,
not failures introduced by the M0 test package:

- `NETSDK1138`: the current `net7.0` target is out of support. The target-framework migration
  is intentionally deferred to M1.
- `CS86xx`: existing nullable-reference warnings in the client, server, models and plugin core.
- `CS8509`: existing non-exhaustive JSON-node switch expressions.
- Protobuf compiler warnings for unused imports in the existing contracts.

M0 keeps these warnings visible instead of suppressing them. New code is expected not to add
warnings, and CI runs the same restore, build, test and formatting checks documented in the
README.

M1 supersedes the target-framework item above by moving every project to `net10.0`. The other
entries remain a historical record of the M0 baseline and are not a description of current
build output.
