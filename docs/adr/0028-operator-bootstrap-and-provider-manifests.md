# ADR-028: Operator bootstrap and Provider manifests

- Status: Accepted
- Date: 2026-10-28
- Scope: Internal alpha operator API and Native AOT CLI bootstrap

## Context

The usable alpha pilot needs an operator-supported path from an empty catalog
to an Environment, administrative secret, Provider configuration revision and
validated alias. Trusted internal APIs currently prepare these records. This
decision exposes that workflow incrementally without moving resource-management
logic into the CLI or making the first PostgreSQL implementation universal.

## Decision

### Separate local configuration from server state

`config ...` manages only the CLI's local YAML configuration. Server commands
such as `environment ...`, `secret ...` and `provider ...` invoke registry APIs.
Creating a local context never implicitly creates an Environment on the server.

The intended default local path is the user's `.haby/config.yaml`; this is not
a file discovered relative to the working directory. Preserve explicit
`--config`, `HABYCTL_CONFIG` and build-time downstream identity customization.
This supersedes ADR-027's platform configuration-directory default. Changing
the implemented path requires documented handling of the previous location;
do not silently mix configurations or lose existing contexts.

The OSS path is a distribution default, not a hardcoded requirement of shared
CLI services. At build-time composition a downstream fork may configure its
per-user directory and filename, executable/application identity and configuration
environment-variable name. Path resolution, commands, help and diagnostics use
this explicit distribution configuration consistently; they must not reconstruct
the OSS path independently. Keep the user-home root independent of the working
directory and preserve the precedence of explicit --config, the configured
environment variable and the distribution default.

A fork may therefore retain its existing hidden per-user configuration directory
and config.yaml filename instead of moving operator state to .haby. The OSS build
must not probe private downstream locations or include their product names.
Retaining a path does not imply compatibility with a legacy file schema. A fork
may register an explicit version-aware compatibility reader or migration; never
silently reinterpret or overwrite an unsupported legacy file. Test an alternate
neutral distribution identity/path to prove shared services contain no OSS-path
assumptions. These are compile-time extension points, not runtime plugin loading.

YAML is reserved for this local configuration. Server declarations use JSON
and explicit source-generated System.Text.Json metadata.

### Use names for operator selection and exact IDs for durable references

An Environment has a registry-unique logical name. A Provider has a logical
name unique within its Environment. An administrative Secret also has a name
unique within its Environment under [ADR-029](0029-named-administrative-secret-bootstrap.md).
CLI commands accept those names and the server resolves them to persisted
identities. Renaming is deferred.

Names improve selection; they do not replace internal IDs or become live
execution pointers. Plans, generations and operations retain exact persisted
Environment, Provider, revision and SecretRef identities. Requests must not
silently change Environment because a local current context changes.

For example, `provider get pg-main --environment production` selects a named
Provider. It does not rewrite a generation's pinned Provider reference.

### Declare Provider configuration through a JSON manifest

Use a versioned JSON declaration with `kind: "Provider"`. Its common envelope
identifies the Provider name, target Environment and explicit configuration
revision. The declaration identifies the registered configuration contract and
implementation and contains typed configuration properties.

The exact wire schema and field names must be documented before its coding
slice. Keep the envelope provider-neutral: no mandatory PostgreSQL host, port,
administrator or database fields in shared DTOs. Registered typed adapters own
their properties, validation and source-generated JSON metadata. Unknown fields,
unsupported versions and unregistered implementations fail closed. No reflection
discovery, arbitrary CLR type names or unconstrained payload interpretation.

Applying a declaration creates or identifies immutable state; it never silently
overwrites a revision, switches an alias, provisions resources or selects the
latest revision. Revision numbers are explicit. Equivalent repeat returns the
existing revision; different contents at the same revision are a conflict.
Compare validated configuration, not whitespace or source-file bytes.

### Keep bootstrap steps explicit and retries safe

Provide the following operator workflow in coherent implementation slices:

1. Create or find an Environment by its unique name.
2. Create an administrative secret and obtain its exact SecretRef.
3. Create a named Provider and its immutable configuration revisions.
4. Check a proposed exact revision without provisioning resources.
5. Assign or switch an alias with an explicit expected old binding or absence.

Alias switching repeats mandatory server-side preflight and atomic
compare-and-swap under ADR-024. A prior successful check is not reusable
authorization or a guarantee of future provisioning success.

Environment creation by the same logical name returns its existing identity.
Named Provider and numbered revision uniqueness protect their identities.
Named administrative secret creation combines natural-name equivalence with
durable request receipts under ADR-029. Request IDs are optional operator
input and are generated by the CLI when omitted. A same-request retry returns
the original result; incompatible reuse is a typed, value-free conflict. No
duplicate object or secret version may be created solely because the original
response was lost. Never expose secret-derived fingerprints in diagnostics or
ordinary APIs. Other bootstrap commands must specify their own retry contract
before implementation.

Include bounded metadata get/list operations so operators can rediscover
identities without recording UUIDs manually. Ordinary reads do not dump secret
material or full Provider payloads. Deletion, cleanup and rename are deferred.

### Implement SecretRef input first; design inline credentials separately

The first Provider manifest accepts secret references without inline material.
An operator reference uses a Secret name and explicit version number; the
server resolves it to an exact internal SecretRef when accepting the Provider
revision and persists that resolution immutably under ADR-029. An implicit
`latest` is forbidden. The exact Provider input schema remains to be agreed.
Interactive secret creation uses hidden input; automation can explicitly use
`--stdin`. Do not accept secret literals in command-line arguments or print
secret values.

A later slice may add a separate input-only credential block alongside the
non-secret Provider configuration. It is not a generic Provider property or
part of the stored immutable payload. Each declared credential input maps to
a registered Provider-defined secret slot. Supplying inline material and a
SecretRef for the same slot is invalid.

The server extracts inline input into SecretStore and persists only exact
SecretRefs in the accepted configuration revision. Do not retain the original
secret-bearing request as manifest history, return it through get/list or
include it in logs, diagnostics, Plans or generation snapshots. Define atomic
secret/revision acceptance and request retry semantics before implementing this
path. Failure must not publish a partial usable Provider revision or create
new secret versions on a retry.

This permits a future input-only bootstrap credential exception; it does not
relax ADR-006/024 restrictions on stored manifests, payloads or ordinary output.
Do not implement or silently accept the inline block in the SecretRef-only slice.

### Treat future local authentication tokens explicitly

When operator authentication is introduced, a plaintext registry access token
in the per-user local YAML configuration is an allowed supported mode. This
supersedes ADR-027's prohibition on a future plaintext-token option, but does
not introduce authentication or a token field in today's unauthenticated schema.

Restrict local file permissions, never commit the configuration, and always
redact tokens from config view, text/JSON output, diagnostics and logs. An
operator authentication token is distinct from a Provider administrative
credential; local contexts must not become storage for Provider passwords.

## Implementation sequence and consequences

First expose Environment and metadata selection, then scoped secret creation,
then Provider declarations/revisions and revision checks/alias switching.
Implement the input-only credential block in a separate later slice after the
SecretRef workflow is verified. Local configuration writes retain ADR-027's
comment/formatting and atomic replacement requirements.

Before each slice, finalize the relevant DTO/schema, name constraints,
idempotency outcomes and tests. This decision accepts architecture and workflow,
not a complete executable Provider manifest schema. It does not introduce a
bootstrap wizard, stable public API, remote exposure, secret reveal, automatic
execution or a general plugin framework.
