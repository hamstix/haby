# ADR-027: Operator CLI foundation and Native AOT delivery

- Status: Accepted
- Date: 2026-10-28
- Scope: `habyctl` and its internal alpha gRPC client

## Context

ADR-004 introduced a narrow `habyctl` for the shadow pilot and did not require
Native AOT publication. The CLI is becoming the operator's primary interface
to Haby. The current implementation has a manual argument switch and one
endpoint environment variable. It can be published as a Windows Native AOT
binary, but that alone does not provide a maintainable command tree, context
selection, structured output, or an ongoing AOT compatibility gate.

Downstream distributions may add commands at build time. The OSS CLI must stay
useful on its own, while its commands must not implement server-side resource
management through direct database, Kubernetes, Consul, or other infrastructure
access. This decision supersedes only ADR-004's statement that the early CLI
does not require AOT publication; its internal alpha API and network boundary
remain in force.

## Decision

### Use System.CommandLine 2.x and an explicit command tree

Use the stable `System.CommandLine` 2.x package. Build and register the command
tree explicitly; do not copy older beta-era handler/hosting APIs or discover
commands by scanning assemblies. Command actions receive cancellation and
invoke a narrow registry-client abstraction over the generated gRPC client.
Parsing, context resolution, transport, result formatting, and command
orchestration remain separate concerns that can be tested independently.

Preserve the existing `manifest validate`, `manifest import`,
`manifest revision get`, and `manifest revision preview` paths while moving
them to the new tree. Add future commands in coherent vertical slices rather
than pre-registering empty command groups. Use neutral user-facing wording such
as "registry", "instance", and "context"; the executable's name and package
identity may remain product-specific.

### Make local contexts first-class

Provide a versioned local CLI configuration with a current context, named
instances, and named contexts. A context selects an instance and an Environment
identifier; the instance records the registry endpoint and transport settings.
This follows the established `config view`, `config current-context`,
`config use-context`, and `config get|set|rename|delete instance|context`
command vocabulary. Do not copy unrelated downstream configuration concepts.

Use YAML as the operator-facing local configuration format so a file containing
many instances and contexts remains practical to inspect and edit by hand.
The file has an explicit format version and a restricted, predictable shape:
one mapping document with known fields and scalar types. Reject duplicate
mapping keys, unknown fields, aliases, merge keys, custom tags and multiple
documents rather than allowing parser-dependent or surprising resolution.
Parse into a typed configuration model without reflection-based object
activation; select the YAML library only after a Native AOT publish-and-run
spike proves the chosen parsing path. The source-generated `System.Text.Json`
requirement still applies to JSON command output, not to parsing the YAML file.

The configuration-file service owns path selection, bounded reading, validation,
and atomic replacement. CLI writes must not silently discard unrelated
operator comments or formatting: preserve them where supported, or make any
normalization and backup/confirmation behavior explicit before enabling a
mutating command. By default the file lives in the platform's per-user
configuration directory under the executable's configurable identity
(`habyctl` for the OSS distribution). `--config` overrides the file for one
invocation; the application-identity-specific `HABYCTL_CONFIG` environment
variable overrides the default path. A downstream fork changes that identity
and variable name at composition time, rather than sharing operator state.
Invalid references fail closed rather than silently falling back to a
different instance.

Command-level `--context` overrides the saved current context for one
invocation without rewriting the file. Existing endpoint environment-variable
usage may remain as a documented transitional fallback only when no context
was selected. It must never override an explicit or saved context. Contexts
do not bypass ADR-004's current loopback-only, unauthenticated pilot boundary:
different instances can be reached through distinct local forwarding endpoints.
Direct remote access requires a later security decision.

No authentication token is stored in the first configuration schema because
the alpha operator API does not yet authenticate callers. Reserve a separate
credential-reference concept for the authenticated phase. Do not reproduce a
legacy CLI's plaintext token option or raw configuration dump; `config view`
must redact all future secret-bearing fields in both output formats.

### Keep secret input and disclosure explicit

When a future command needs a secret entered interactively, use a small
testable terminal-input abstraction over .NET's hidden character input. Avoid
adding a prompt library solely for password masking. Do not place literal
passwords or tokens in command-line arguments, shell history, local CLI
configuration, diagnostics, or ordinary JSON output. A non-interactive command
may explicitly read secret bytes from standard input; it must not silently
fall back to prompting when standard input is redirected. Prompts and status
messages go to standard error so standard output remains suitable for pipes.

Secret metadata and secret material are different outputs. Ordinary `get`,
`list`, `config view`, and JSON formatters never reveal material. A future
authorized disclosure, if accepted under a separate security decision, uses
an explicit command such as `secret reveal <secret-id> --version <version-id>
--output raw`. It writes only the exact returned secret bytes to standard
output, without a newline, JSON envelope, status line, or terminal prompt.
This is not a global `--show-secrets` formatter option. The server must
authorize access to the exact SecretRef and audit the disclosure without
recording its value; the CLI cannot make that decision locally.

ADR-006 still requires a separate decision on reveal permission and audit.
The current unauthenticated alpha API must not expose plaintext reveal merely
because a CLI command shape has been described here. PostgreSQL `Plaintext`
storage mode does not imply permission to disclose a stored secret.

### Separate command results from their presentation

Each command returns a typed, secret-safe result. A formatter renders it as
`text` (default) or `json`, selected through a recursive `--output` option.
JSON uses explicit output DTOs and source-generated serialization metadata;
it must not depend on reflection-based serialization of protobuf messages or
anonymous objects. Standard output contains results, including expected
validation diagnostics; standard error contains usage and transport failures.
Keep exit codes consistent for
success, expected rejection, usage/configuration error, and transport failure.
The JSON envelope carries a `formatVersion` from its first implementation.
Machine-readable output remains alpha until a separate compatibility policy
exists.

### Keep resource management server-side

The CLI may read local manifests and its own configuration, call the registry
API, and format results. It must not connect directly to the Haby catalog or
provider databases, invoke Kubernetes tools, or reconcile external resources.
The server owns validation, planning, state changes, provider execution, and
recovery decisions. A future convenience command may orchestrate multiple
explicit API calls but may not replace their commit/execute confirmation
boundaries or invent server-side decisions locally.

Build-time extensions register command groups explicitly against narrow CLI
services. The OSS distribution requires no runtime DLL loader, reflection
scanning, or product-specific references. A downstream fork may add release
commands while reusing the core parser, context resolver, output formatters,
and registry client. Extension commands are subject to the same API-only
resource-management boundary.

### Treat Native AOT as a release gate

Production `habyctl` artifacts are Native AOT builds. Add `IsAotCompatible`
and a publish/smoke gate for supported Windows and Linux runtime identifiers.
Treat new trim/AOT warnings as failures. Smoke tests exercise help, config,
text/JSON output, and at least one real gRPC request using the published binary
against a disposable loopback host. A successful ordinary build, or a publish
that is never executed, is insufficient evidence. The server is not made
Native AOT by this decision.

## Initial implementation slices

1. Introduce `System.CommandLine`, an explicit command registry and the
   configuration-file abstraction; preserve existing command behavior and
   loopback security checks.
2. Add the context command vocabulary, deterministic precedence, and
   secret-safe text/JSON output; test two independent instances and malformed
   configuration without a live server.
3. Add Native AOT publish and executable smoke gates on Windows and Linux;
   document distribution and troubleshooting.
4. Expose durable planning and execution use cases in separate operator API
   slices, keeping each mutating step explicit.

## Deferred security decisions

- Choose the future credential backend and authenticated remote-access policy
  before adding `config set credential` or non-loopback endpoints.
- Define server-side authorization, exact-version access, audit, transport,
  and output safeguards in a separate ADR before implementing `secret reveal`.

## Consequences

- The CLI becomes a maintained OSS product surface even while the gRPC wire
  contract remains internal alpha.
- A downstream fork needs only compile-time command registration, not copied
  parser infrastructure or direct access to Haby's persistence adapters.
- Native AOT compatibility is verified continuously rather than inferred from
  the package's stated trimming support.
