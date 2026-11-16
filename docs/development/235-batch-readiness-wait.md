# Bounded product and CI readiness batches

`habyctl deployment generation wait-batch --file members.json [--request-id <uuid>]`
uses one authenticated generated Host RPC over explicit exact deployment/generation
references. The CLI never polls or contacts Kubernetes. This is observation of already
submitted supported generations; it does not submit, resume, renew rollout deadlines,
grant access, change desired inputs, retire objects or advance AppliedGeneration.

```json
{
  "members": [
    {"deploymentId": "019a2b70-1234-7123-8123-123456789abc", "generation": 1}
  ],
  "settings": {"concurrency": 4, "maximumReads": 256, "requestMilliseconds": 30000, "startsPerSecond": 10},
  "memberSettings": {"maximumCalls": 64, "maximumRounds": 32, "readTimeoutMilliseconds": 10000, "pollMilliseconds": 1000, "requestMilliseconds": 30000}
}
```

Both settings objects are optional. JSON is strict: unknown or duplicate properties,
null/empty member lists, duplicate canonical references, invalid exact identities and
oversize input reject before any observation. There is no YAML, selector or template
syntax. The file and protobuf request are each limited to 128KiB. The existing global
gRPC receive limit remains 1MiB; this RPC enforces 128KiB on its decoded protobuf at
method entry. The [configured gRPC API](https://github.com/dotnet/aspnetcore.docs/blob/main/aspnetcore/grpc/configuration.md) exposes global/per-service limits, rather than
a public per-method receive-size setting. No global transport limit changes.

## Host settings and work bounds

`Haby:WorkloadBatchObservation` settings are validated at startup. Invalid values fail
startup. A restart applies configuration changes; no live reload is promised.

| Setting | Default | Range | Caller narrowing |
| --- | ---: | --- | --- |
| MaximumMembers | 200 | 1..200 | Supply a smaller list |
| Concurrency | 4 | 1..8 | Lower |
| MaximumReads | 256 | 1..1024 | Lower |
| RequestMilliseconds | 30000 | 1..300000 | Lower |
| StartsPerSecond | 10 | 1..100 | Lower |

The existing [per-generation settings and ranges](233-operator-readiness-observation.md#configuration-and-bounds)
also apply, including per-member request duration and minimum polling interval.
Caller settings never widen Host authority. Concurrency bounds worker tasks and live
observations; there is no task per member. Count and pacing cover every actual
readiness GET, including the existing finite expired-attempt recovery read. A dispatch
is charged regardless of its response. Connection failures before GET are not GETs.

The invocation owns a token bucket with burst capacity equal to effective concurrency,
refilling at effective starts/second. Starts over an interval are bounded by
`burst + rate * elapsed`; this is not a strict sliding-window rate. At most one rate
wait per worker exists. These are per-invocation bounds, not aggregate cluster or
multi-Host guarantees. Separate requests have independent budgets.

One original monotonic deadline includes authority lookup, generation-gate waits,
rate waits, polls, reads and batch catalog closure. RPC cancellation/deadline and the
stable per-member request limit may shorten execution, never extend it. Per-read and
saved rollout limits remain effective; saved rollout expiry still allows the existing
finite recovery observation without renewing the attempt. Current permission is
rechecked after pacing, before GET dispatch. Authorization is point-in-time; an
already admitted request can race revocation.

The batch starts no new API/catalog mutation after budget exhaustion. Disposal and
rollback of already-open resources still run. It does not multiply the separate
single-generation 10-second closure grace periods. Batch commits check the original
monotonic deadline immediately before dispatch and use the linked batch cancellation
token during commit. Cancellation after dispatch
does not prove rollback or known completion; no hard wall-clock bound is promised
for a driver that ignores cancellation. Unclosed durable request/read
reservations remain partial/interrupted history. There is no detached repair or
implicit safe retry.

## Ordered summaries, keys and exits

Before work the Host reserves the worst-case full ordered summary, including every
optional checkpoint UUID, under the 2MiB protobuf reply bound. It never truncates
the list or expands per-Workload evidence into the batch response. Each admitted
reference has exactly one terminal summary with its reference/key, closed outcome,
fresh/history/replay flags and available Submitted/SavedReady checkpoint IDs.
Detailed Workload evidence remains available through exact-generation observation.

Admission exhaustion before a member observation begins returns `NotStartedBudget`
and creates no durable row to cache that outcome. Existing submission attempts and
checkpoints are preserved. A started member can instead be interrupted or exhaust
its read budget. One failed/refused member does not cancel another. Completed
summaries survive total deadline exhaustion when transport still permits a reply;
canceled transport does not confirm delivery or success. Whole-batch success requires
every member to provide fresh complete-set CurrentReady proof in this invocation.

The optional request ID is a nonempty UUID generated once if omitted. Save the batch
and derived member keys. Member keys use `bw1-` plus lowercase SHA-256 of ASCII
`haby.batch-wait/v1`, a big-endian uint32 UTF-8 byte length and batch key bytes,
the canonical 36-byte deployment UUID, and a big-endian uint64 positive generation.
The encoding is independent of list order. The batch key is correlation only:
adding members preserves existing keys and can start new members; removing members
excludes them. There is no durable whole-list idempotency, atomicity or list store.

Canonical stable per-member options and actor remain bound in existing stored input.
Changing them under the same member key conflicts. Remaining transport time and batch
scheduling settings do not change the stored per-member options. Keys grant no rights;
foreign actors cannot inspect saved payload. Safe same-actor historical replay remains
historical after revocation, and cannot make the batch freshly ready. Identical-key
retry may replay interrupted history and **does not guarantee resume** of that member.

Exit codes retain all available ordered summaries: 0 only all freshly CurrentReady;
1 historical/replayed/incomplete/refused/conflicting; 2 invalid input; 3 cancellation,
deadline, unavailable transport/storage or malformed/unavailable response. After
uncertain delivery the CLI prints exact batch/member keys and identical-input
inspect/replay guidance without claiming completion.

## Validation obligations

Focused tests cover bounds, stable key vectors, strict JSON, full reply preflight,
shared actual-read count/rate, GET placement after connection acquisition,
deadline/cancellation, unstarted admission, mixed order, stable-input/actor conflicts,
and independent Hosts. Persisted assertions use separate DbContexts.
Published-native loopback cases exercise 200 references through one RPC on both
required targets. The configured consumer gate retains C01-C11/R01-R06 and adds
B01-B04 for a second exact configured generation, failure alongside success, complete
fresh batch and historical replay. It reuses the existing f362 input/980a consumer
image. Case/method/row multiplicity and exact-source artifacts remain mandatory.
Executable inventory is an obligation, not passing evidence. This slice does not
claim P3 completion or the deferred post-P4 mass-start load evidence.
