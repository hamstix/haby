# Authenticated operator administration

Issue #150 implements [ADR-043](../adr/0043-operator-access-tokens-and-bootstrap.md).
All administrative RPCs now require installation-wide `operator-admin`, including
reads, validation, bootstrap, planning, execution, recovery and both grant families.
The shared Host has one listener. The independent configuration-delivery service
continues to require its exact document-scoped delivery credential.

## Upgrade and first issuance

`AddOperatorAccess` adds `operator_access_grants` to the admitted current catalog.
It changes no historical rows or document formats, issues no credentials and needs
no catalog reset. Existing alpha schema admission checks still apply. Upgrading
immediately denies anonymous operator clients, including on loopback/Development.
The Host may run with zero operator grants; ordinary startup never issues one.

Supply the same `Haby:PostgreSql:ConnectionString` configuration used by the Host
through protected process environment or deployment configuration. Do not put
credential-bearing connection strings in shell arguments/history or logs. Invoke
the explicit local administrative command, independently of ordinary startup:

```powershell
# Haby__PostgreSql__ConnectionString is supplied by the local secret mechanism.
dotnet run --project src/Haby.Operator.Host --no-launch-profile -- operator-token-issue --display-name first-operator --token-output-file D:/protected/operator.token
```

A deployed build can use `dotnet Haby.Operator.Host.dll operator-token-issue ...`.
This command uses the configured catalog, applies admitted migrations, issues one
grant and exits. It never builds/starts a web Host or listener and needs no TLS or
port settings. Possession of this local catalog authority is deliberate privileged
administration, not a remotely authenticated identity. Its audit actor is
`haby-local-operator-issuance`.

The destination must be new. It is opened exclusively before issuance; bytes are
flushed to disk and the file closed before success. Unix files are mode 0600;
on Windows use a protected directory with restrictive ACLs. The alternative
`--token-stdout` explicitly writes just the token to stdout, with identity/status
on stderr. Pipe it to a protected durable destination; never transfer through
ordinary logs, transcripts, screenshots or container log extraction.

Optional `--expires-at 2027-01-01T00:00:00Z` issues a temporary token with explicit
future UTC expiry. Default is no expiry. There is no renewal or automatic retry.
A failed response/write may leave an active grant and/or an empty destination.
Inspect/list/revoke using an available authenticated credential; if none survives,
use explicit local recovery to issue a different grant. Never assume rollback or
attempt to recover raw material from the catalog.

EF's PostgreSQL migration lock serializes migration application. The tested
sequence is first local issuance/migration, normal Host startup, then concurrent
local issuance against that running current-schema Host. Separate contexts and
UUID identities allow concurrent issuance; each invocation creates a separate
grant. Stop/migrate first when the installation's upgrade policy requires it;
stopping Host is not a prerequisite for current-schema recovery. In Kubernetes,
operations policy may permit `exec` or a one-shot Job running the same command
with catalog settings and a protected durable output volume. No extra listener,
anonymous bootstrap RPC or habyctl database access is involved.

## Configure the selected registry

Credentials belong to named registry instances, shared by their selecting contexts:

```yaml
formatVersion: 1
currentContext: pilot
instances:
  local:
    endpoint: https://localhost:7443
    tokenFile: 'D:/protected/operator.token'
contexts:
  pilot:
    instance: local
    environmentId: pilot
```

`token` is the supported plaintext inline alternative; never combine it with
`tokenFile`. Protect this file and keep it outside Git. Token files are opened
once per invocation, bounded to 512 bytes and decoded as strict UTF-8. Accept no
BOM, spaces, bare CR or multiple newlines: either no newline, one LF or one CRLF.
Relative token-file paths are relative to the process working directory; prefer
absolute paths. Invalid selected credentials never fall back to another instance,
context or endpoint. Endpoint-environment fallback carries no implicit credential.

To set credentials without putting a raw token in command arguments, explicitly
write a *new normalized configuration*, retaining the original as the backup:

```powershell
habyctl config set-credential local --token-file D:/protected/operator.token --normalize --new-config-file D:/protected/authenticated.yaml --config D:/protected/original.yaml
# Alternatively pipe a token into the explicit --stdin input.
habyctl environment list --config D:/protected/authenticated.yaml
```

The input config must already contain the named instance. `--token-file` saves a
validated absolute reference; `--stdin` saves an inline credential. Both require
`--normalize` and a new `--new-config-file`. Every other validated setting is
preserved; comments/formatting remain in the untouched original, not the normalized
copy. New output uses a flushed user-only temporary file and rename without
overwrite. The command never silently switches the selected config path. Existing
`--config`, distribution-specific environment/default-path injection and neutral
registry/context wording remain supported.

`config view` text/JSON uses only a validated redacted model. It redacts inline
tokens and credential-file references, never opens credential files or dumps YAML.
Help, version and local inspection stay offline. Both normal and larger-response
operator channels use the selected credential and generated contracts.

HTTPS permits remote registry endpoints with ordinary platform TLS validation.
Plain HTTP is limited to loopback unless that named instance explicitly sets
`allowInsecureHttp: true` for an isolated contour. URLs cannot carry credentials,
paths, queries or fragments. The existing pilot-only
`HABY_OPERATOR_SKIP_TLS_VALIDATION=true` is an explicit certificate-validation
exception, never enabled automatically; its tests retain the opt-in behavior.
Use valid trusted certificates for ordinary deployment.

## Lifecycle, audit and independent delivery

```powershell
habyctl operator access list --output json
habyctl operator access create --display-name replacement --token-output-file D:/protected/next.token --output json
habyctl operator access get <grant-id> --output json
# Transfer/configure next.token, verify a successful administration call, then:
habyctl operator access revoke <old-grant-id> --output json
```

Creation requires exactly one new `--token-output-file` or `--token-stdout`.
Only explicit creation with `--token-stdout --output json` includes material in
JSON; file creation and all get/list/revoke output are metadata-only. List uses
bounded pages (`--page-size 1..100`, default 50, `--after-grant-id` cursor).
Optional display names are not accounts, audit actors or authentication selectors.
New persisted operations derive their actor as `operator-grant:<grant UUID>` from
the verified server identity; request bodies cannot select it. Historical audit
rows are unchanged. Metadata read and idempotent revoke expose no verifier.

Revoking the current or last grant is permitted. Subsequent authentication fails;
already-authorized work may finish. Every request rechecks hash/purpose/expiry/
revocation with no positive cache. Storage failures safely deny authentication.
The format `haby_operator_v1.<identity>.<256 random secret bits>` is distinct from
`haby_delivery_v1`. Only SHA-256 verifiers and safe lifecycle/audit state persist.

Continue [configuration delivery](148-authorized-configuration-delivery.md) using
its independently scoped delivery token. Delivery credentials cannot administer
anything or inspect reflection; an operator token cannot itself retrieve resolved
configuration. Operators can explicitly create a separate document grant through
the existing workflow. Neither runtime contract nor runtime client gained operator
dependencies or a reflection requirement.

## Reflection and restore

Reflection is enabled by default in all environments/bindings and requires
operator-admin on every registered protocol endpoint. Operators can inspect both
service descriptors without gaining delivery authority. Set
`Haby__Reflection__Enabled=false` to disable mapping; malformed boolean settings
refuse startup. Generated operator and runtime calls still work independently.

Current-build restart and real `pg_dump`/`pg_restore` retain verifier, expiry,
revocation and original audit. Restoring an older backup restores that snapshot's
revocation state: revocations made after the backup do **not** survive rollback.
Retained token files remain necessary; backups cannot recover raw tokens. Catalog
backups also retain the existing pilot's plaintext Provider/application secrets and
must be protected accordingly. No persistent catalog is reset by these commands.

The tests cover complete registered administrative coverage before business work,
credential faults/storage failure, separate purposes, explicit Host-process/local
concurrent recovery, derived actors, sensitive transfer/redaction, selected-instance
isolation on both channels, real restart/restore and published Native AOT use.
Full unfiltered PostgreSQL measurements/artifact safety and Windows/Linux Native
AOT gates remain in CI. Exact executed counts, HEAD, warnings and CI belong in the
PR validation report; documentation does not substitute for execution evidence.
