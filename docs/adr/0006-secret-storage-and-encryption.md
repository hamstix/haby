# ADR-006: Secret storage and encryption

- Status: Accepted
- Date: 2026-09-21

## Context

Haby provisions infrastructure resources and renders application configuration.
This work produces credentials such as database passwords, API tokens, private
keys and provider administrator credentials. Treating these values as ordinary
generated variables or embedding them in semi-final JSON documents makes them
easy to expose through database access, logs, plans, history, APIs and UI forms.

Haby must remain useful as a standalone open-source product without requiring a
separate secret-management system. Some installations will nevertheless require
Vault, OpenBao, a cloud KMS or another external secret store. The initial model
must permit those integrations without making an external system mandatory or
allowing vendor-specific concepts into the domain.

PostgreSQL remains Haby's source of truth. For secrets, this means it owns stable
identity, ownership, version and lifecycle metadata. It does not require every
secret payload or key-encryption key to remain in PostgreSQL forever.

## Decision

### Secret values are separate domain objects

Haby represents confidential values through stable `Secret` and immutable
`SecretVersion` identities. Application manifests, generated values, managed
resource outputs, plans and configuration-document revisions contain a
`SecretRef`; they do not contain the plaintext value.

A Secret records at least:

- stable ID and environment scope;
- owner and purpose;
- sensitivity and content type;
- storage-backend reference;
- ownership policy;
- current version and lifecycle metadata.

Secret ownership is explicit:

- **Managed**: Haby may create versions, rotate and delete the value according
  to policy;
- **Referenced**: Haby may resolve an externally owned value but must not rotate
  or delete it;
- **Opaque external**: Haby passes a reference to a capable consumer and does
  not resolve the plaintext itself.

Plugin schemas declare secret fields, but a schema annotation alone is not the
storage mechanism. When a plugin generates a password or token, it writes the
value directly to the secret service and returns a `SecretRef`. It must not
return the plaintext through an ordinary generated-value contract.

### PostgreSQL encrypted storage is the initial default

The standard Haby distribution stores managed secret payloads in PostgreSQL
using application-level envelope encryption. Database or disk encryption may be
used in addition, but is not a substitute for this boundary.

Each SecretVersion uses a new cryptographically random data-encryption key
(DEK). Haby encrypts the payload with AES-256-GCM and wraps the DEK with a
key-encryption key (KEK). PostgreSQL stores only:

- ciphertext;
- nonce and authentication tag;
- wrapped DEK;
- encryption format and algorithm version;
- KEK provider, key ID and key version;
- non-confidential SecretVersion metadata.

Authenticated additional data binds the ciphertext to the Secret ID,
SecretVersion, environment, owner, purpose and encryption-format version. This
prevents an encrypted payload from being silently moved to a different record or
scope.

The design supports cryptographic agility through persisted format and algorithm
versions. New writes use the current approved format; old formats remain
readable only for a controlled migration period.

### The KEK is outside PostgreSQL

No usable KEK is stored in the Haby database or its backups. The initial
standalone deployment reads a versioned key ring from an operator-provided
bootstrap source such as:

- a read-only mounted file;
- a Docker secret;
- a Kubernetes Secret volume;
- an operating-system protected store.

An environment variable may be supported for local development or compatibility
but is not the recommended production source. Haby must not automatically write
the bootstrap key into the same PostgreSQL database that contains the encrypted
payloads.

Loss of every KEK version capable of unwrapping stored DEKs makes the affected
secret versions unrecoverable. Backup and restore procedures must therefore
cover the PostgreSQL data and the external key ring separately and must test a
complete restore.

### Secret storage and key protection are separate contracts

M2 defines transport- and persistence-neutral abstractions with byte-oriented
secret material and cancellation-aware operations:

- `ISecretStore` stores, resolves and removes versioned secret payloads;
- `IKeyEncryptionProvider` wraps, unwraps and rewraps DEKs.

Neither contract exposes EF Core entities, ASP.NET Core types, gRPC-generated
types or a vendor SDK. Providers are selected through trusted Haby configuration
and composed at build time under ADR-003.

The initial implementations are PostgreSQL encrypted secret storage and a local
bootstrap key-ring provider. External implementations may later provide:

- Vault or OpenBao KV storage through `ISecretStore`;
- Vault or OpenBao Transit, or a cloud KMS, through
  `IKeyEncryptionProvider`.

Dynamic credentials with leases, renewal and revocation have a different
lifecycle. If required, they will use a separate leased-credential issuer
contract rather than being hidden behind `ISecretStore`.

### Plaintext exists only at controlled boundaries

Haby resolves a SecretRef only for an authorized operation that requires the
value, such as provisioning a resource or materializing a configuration for an
authorized workload.

Plaintext secret material:

- is never written to logs, traces, exception details, plans or audit payloads;
- is never returned by ordinary read/list APIs;
- is not stored in a rendered configuration revision;
- is not placed in a persistent plaintext cache;
- is held in memory for the shortest practical time using byte-oriented buffers
  where the downstream protocol permits it;
- is redacted consistently by API, UI and plugin diagnostics.

A stored Configuration document contains SecretRefs. Its effective revision
fingerprint uses Secret IDs and SecretVersion IDs, not a hash of plaintext.
Materialization resolves those exact versions just in time for delivery.

The UI treats an existing value as write-only. Editing unrelated settings does
not round-trip or overwrite a secret. It shows presence, backend, version,
rotation status and provenance instead of the value. Any future reveal operation
requires a separate permission, explicit audit and a new decision.

### Key rotation and secret rotation are distinct

KEK rotation changes key protection without changing the logical secret. Haby
rewraps DEKs under the new KEK, records durable progress and retains the old KEK
until every required version has been rewrapped and verified.

Secret rotation creates a new SecretVersion and can require external resource
changes and workload rollout. It uses the normal plan, operation, audit and
reconciliation model.

Haby does not claim that a previous SecretVersion is still accepted by an
external system merely because the encrypted value remains available. Rollback
of configuration and rollback of an external credential are separate planned
operations.

### External stores are deferred implementations

M2 establishes the domain model and interfaces, and the following reliability
work implements encrypted PostgreSQL storage. Haby does not require Vault,
OpenBao or a cloud KMS for its first usable distribution.

External adapters are planned with standard provider integrations after the
local model has been exercised. OpenBao is the preferred open-source validation
target; HashiCorp Vault compatibility may be provided through a separately
tested adapter. Vendor names do not appear in the public core contracts.

Credentials used by Haby to authenticate to an external secret system are
bootstrap credentials. They must come from workload identity, a mounted secret,
a client certificate or another source outside the target secret store. Haby
must not create a recursive dependency by storing that credential through the
same provider it unlocks.

## Consequences

### Positive

- A standalone Haby installation needs only PostgreSQL and separately supplied
  key material.
- A stolen database dump does not disclose secret plaintext without the KEK.
- Plugins, manifests and configuration history exchange stable references
  instead of confidential values.
- KEK rotation can rewrap small DEKs instead of decrypting and re-encrypting all
  payloads.
- External secret stores and KMS products can be added without changing the
  domain model or configuration-document format.
- Secret rotation participates in the same durable operation and audit model as
  resource provisioning.

### Negative

- Operators must back up, restore and rotate a key ring separately from
  PostgreSQL.
- Losing the key ring permanently loses encrypted secrets.
- A compromised Haby process that is authorized to unwrap keys can still read
  plaintext; envelope encryption primarily protects the database and backups,
  not a fully compromised runtime.
- Just-in-time materialization makes secret-backend availability part of
  provisioning and workload startup.
- Searching, sorting or comparing secrets by plaintext is not supported.
- Secret replacement, KEK rewrap and external credential rotation require
  explicit operational state and tests.

## Rejected alternatives

### Store plaintext in PostgreSQL and rely on database encryption

This exposes values to database readers, dumps, diagnostics and accidental JSON
serialization and does not establish a domain boundary for secrets.

### Require Vault or OpenBao for every installation

This makes the first OSS deployment operationally heavier and introduces an
availability and bootstrap dependency before the secret model has been proven.

### Store the KEK in the same PostgreSQL database

An attacker obtaining the database would obtain both encrypted values and the
material needed to decrypt them, defeating the main purpose of envelope
encryption.

### Use one interface for storage, encryption and leased credentials

KV storage, cryptographic key protection and dynamically issued credentials have
different ownership, availability, rotation and revocation semantics. A single
interface would either leak vendor concepts or become too vague to enforce the
required invariants.

### Encrypt complete configuration JSON blobs

This prevents per-secret ownership, versioning, rotation, redaction and reuse.
It also forces unrelated configuration edits to decrypt and rewrite every
secret. Configuration documents therefore retain SecretRefs and are
materialized only at a controlled delivery boundary.

## Follow-up decisions

Separate ADRs are required before adding:

- a production external KV secret-store adapter;
- a Transit or cloud-KMS key-encryption provider;
- dynamic credentials and lease renewal;
- a plaintext secret-reveal API;
- a persistent decrypted cache or offline startup fallback.

## References

- [OWASP Cryptographic Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Cryptographic_Storage_Cheat_Sheet.html)
- [HashiCorp Vault Transit secrets engine](https://developer.hashicorp.com/vault/docs/secrets/transit)
- [OpenBao Transit secrets engine](https://openbao.org/docs/secrets/transit/)
- [OpenBao KV v2 secrets engine](https://openbao.org/docs/secrets/kv/kv-v2/)
