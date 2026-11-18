# Configured qualification lifecycle budgets

[Issue #269](https://github.com/hamstix/haby/issues/269) repairs fixture budget
composition after the configured drain failure on
[PR #266](https://github.com/hamstix/haby/pull/266). It changes test tooling only.
It does not establish the historical remaining Pod or cancellation source.

| Boundary | Cap | Cancellation / scope |
| --- | ---: | --- |
| Configured child process | 240 seconds | Launcher owns the process tree; other contours retain 180 seconds |
| Cooperative configured lifetime | 240 seconds | Established once at configured entry; linked to user cancellation |
| Functional scenario | 150 seconds | Includes existing configured functional work, not post-U12 drain |
| Restart/delivery | 35 seconds | Links functional and overall; U12 requires actual exact document delivery |
| Graceful owned drain | 45 seconds | Starts after successful delivery; links overall, not expired functional/delivery |
| Namespace cleanup | 20 seconds | Links overall; remains independent of functional failure |
| Held owned-marker release | At most 20 seconds | Applicable cleanup is also capped by remaining overall lifetime |
| Configured native CLI | 30 seconds | Links functional/overall; bounded owned kill/output draining on failure |
| Kubernetes API | 10 seconds | Unchanged |

The drain helper consumes one caller allocation without renewing it. Its finite
181 observations / 250ms polling bound permits the accepted 45-second phase;
API time also consumes that same phase. Normal Pod grace remains 30 seconds.
All three confirmed Deployment receipts, UID/resourceVersion guarded scale,
complete ReplicaSet/Pod ownership inventory, no Pod DELETE, no grace override and
absence of every terminating/pre-restart/replacement Pod remain required.

Functional failure never fabricates U12 or authorizes successful drain. Existing
owned cleanup runs separately and retains the primary exception alongside any
partial cleanup failures. An expired overall/user lifetime cannot be renewed by
starting cleanup. Normal success still requires `DrainComplete`, namespace absence,
catalog cleanup and the launcher's container/volume/private-directory proof.

## Process boundary and truthful limitations

The 240-second limit is the configured **child process** cap. The launcher's
pre-fixture build (180s), cluster launch (180s), readiness (120s), copy commands,
CI job and outer owned container/volume/private-directory cleanup are separate.
Existing five-second owned termination/output tails are also separate. Total wall
clock is not promised to be at most 240 seconds. The other modes' caps and the
existing CI job timeout remain unchanged.

The tool's cooperative clock starts at configured entry; the launcher starts its
hard process clock earlier. The hard bound can therefore preempt cooperative
work. The nominal 25-second orchestration allocation is not a guaranteed margin.
Held-marker release, Host start/stop/disposal and process overhead consume the
same overall lifetime rather than gaining free time.

PostgreSQL fixture initialization/disposal do not accept the caller token. This
repair preserves their existing owning lifecycle; it does not abandon a live
operation through `WaitAsync` or claim it stopped cooperatively. Host lifecycle
helpers also retain the hard process boundary where cancellation is not accepted.
An overall expiry/hard kill is failed qualification. In-process catalog/namespace
cleanup is unknown unless its actual proof was produced. Removing the outer owned
container is distinct from proving namespace/catalog absence inside it.

The configured-only CLI adapter preserves the existing command/config contract.
Its 30-second cap links to the current functional/overall token. Cancellation
kills the owned tree, then bounds termination and output draining by five seconds;
normal exit also requires complete bounded output. Missing output or failed
termination is failure, never a successful CLI call. Unrelated CLI helpers and
their callers are unchanged.

## Safe diagnostics and validation

Closed `configured budget` records contain only phase/state, elapsed milliseconds,
effective remaining milliseconds, overall remaining milliseconds and a finite
cancellation category. They contain no identifiers, endpoints or secrets. Failure
records distinguish user, overall, functional-parent, own-phase and CLI
output/termination causes. These facts do not retroactively identify the cause of
the original failure.

Focused controllable-clock tests cover elapsed delivery followed by full graceful
drain, phase/overall/user cancellation, no overall reset, failed functional work
and partial cleanup, and owned CLI termination/output failure. Unit tests use no
real sleeps. Existing drain ownership and all-Pod absence assertions remain.

Run focused controls before the existing real configured contour and final gates:

```powershell
$env:HABY_TEST_SOURCE_ROOT = (Get-Location).Path
dotnet test tests/Haby.Kubernetes.Tests/Haby.Kubernetes.Tests.csproj --filter 'FullyQualifiedName~ConfiguredLifecycle|FullyQualifiedName~ConfiguredFixtureDrainTests'
pwsh -NoProfile -File scripts/test-kubernetes-qualification.ps1
dotnet run --project tools/Haby.Kubernetes.Qualification --no-build --no-restore -- --configured-setup-controls
```

The existing hosted `configured-deployment` job executes the actual pinned Linux
Docker contour; it can supply the single real representative and final configured
gate on the final candidate. This is a venue choice, not a finding that local
Kubernetes is unavailable. Existing PostgreSQL, both Native AOT and applicable
Kubernetes final checks remain mandatory. No dispatcher, protection transition,
new image or environment is introduced. A new required hosted failure stops new
cycles pending bounded disposition; no unchanged blind rerun.
