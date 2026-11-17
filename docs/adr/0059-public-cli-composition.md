# ADR-059: Public CLI composition and downstream configuration adapters

- Status: Accepted
- Date: 2026-11-17
- Scope: Explicit Native AOT CLI composition; implementation after the operational ADR-058 Deployment update track
- Implementation: Not implemented by this documentation

## Context

A downstream executable needs the common registry commands beneath a chosen parent
without copying handlers, spawning habyctl or changing server contracts. Existing
internal command registration and concrete configuration storage do not constitute
a supported public composition boundary. The public repository remains product-neutral.

## Decisions

### 1. A small public library

Extract Haby.Cli as a public composition library. The habyctl executable uses that
same implementation. A public-only external sample establishes the boundary; NuGet
publication is not a prerequisite. Do not make all internal handlers public or add
InternalsVisibleTo for downstream consumers.

### 2. Independent composition steps

Separate common runtime/options registration, complete registry command registration
beneath a supplied parent, and optional local config command registration. Downstream
commands can be added alongside common commands using explicit typed registration.
The upstream registry commands stay at the root. No runtime plugin discovery or
reflection-based loading is introduced.

### 3. Common options and conflicts

Register --config, --context and --output once at the root and make them available
to nested common commands. Document and test supported argument positions. Detect
command-name, alias and common-option collisions at composition finalization before
invoking handlers. Do not silently replace a command or option.

### 4. Shared invocation behavior

Retain common help, cancellation, stdout/stderr, text/JSON output, safe diagnostics
and exit codes. Expose a small invocation context for extension commands, not the
entire set of internal presenters. Preserve source-generated production JSON metadata
and explicit typed registration suitable for Native AOT.

### 5. Context resolution independent of file schema

Registry commands consume a typed resolver for the selected config file/context and
explicit invocation options. Its resolved context supplies endpoint, Environment,
transport settings and a credential source. Registry handlers do not know a fixed
file location or the complete YAML schema. Downstream supplies its own reader/writer
and configuration model without reimplementing authorization or client creation.

### 6. Credential and transport safety

Resolve credentials lazily through a separate source; do not put a printable token
into ordinary context metadata. Retain token-file, TLS and safe-error rules. An
invalid explicitly selected configuration never silently falls back. Help and local
configuration inspection do not require a network client or credential resolution.

### 7. Config adapter boundary

A resolver supports registry command execution. A separate optional config adapter
supports local read, typed mutation and safe presentation. Reuse standard commands
for contexts, instances and credentials only where the adapter supports their
semantics; downstream registers commands for its own additional sections. Universal
editing of arbitrary YAML is outside this boundary.

### 8. Preserve the full downstream model

The adapter owns the full document and persistence. Common commands submit typed
changes, not an incomplete replacement document. Preserve downstream values on write;
reject a write if preservation cannot be guaranteed. Preservation of comments,
formatting and original property order is not promised in the first implementation.

### 9. Safe config view

The adapter produces an explicitly redacted view of its complete model, including
its own secret fields. The common library does not serialize an arbitrary raw model
or infer confidentiality from field names. Credential modification stays explicit;
view never resolves or displays secret material by default.

### 10. Configurable paths

Selection precedence is explicit --config, then the composition-selected environment
variable, then the composition-selected default path. The upstream composition sample
uses ~/.haby/config.yaml, with home expansion handled explicitly. Downstream can
replace that default and environment variable. This sample decision does not silently
migrate or change the existing habyctl configuration location; any change to its
installed default requires an explicit migration decision.

### 11. Mount-aware presentation

Executable identity and registry command prefix are supplied by composition. Help,
usage and recovery hints must render the actual path, for example a nested
registry deployment generation get command. Result semantics and JSON contracts
remain common; a different executable name is not an API behavior difference.

### 12. Client and resource ownership

Keep standard gRPC client construction and security enforcement shared. A downstream
consumer need not reimplement a growing interface for all registry RPCs. Document
ownership: the runtime disposes resources it creates; caller-owned services remain
caller-owned. Commands stay API-only; no direct database or Kubernetes management.

### 13. Executable acceptance

Publish and run a public-only external Native AOT executable with registry commands
nested under a parent and an additional root command. Prove equivalent requests,
results, diagnostics and exit codes; offline help/config; authorized RPC; extended
configuration preservation and redaction; wrong configuration and token-file refusal.
Use existing loopback venues, not a new Kubernetes/PostgreSQL qualification campaign.
IsAotCompatible alone is not executable proof.

## Requested command usability

A bare command group such as habyctl manifest displays the missing-command diagnostic
and contextual usage, options and subcommands, rather than only directing the user
to --help. Retain a usage-error result for a missing required command; explicit help
succeeds. Keep diagnostics/help human-readable even when --output json is selected,
without a misleading JSON business result. The help path is offline and works at
both root and downstream nesting levels. Generate help from the actual command tree.

Shell autocomplete for Windows and Linux is required future CLI work. Use the actual
composed command tree for commands, options and closed values, including downstream
commands. Shell activation is explicit; do not edit user profiles automatically.
Completion must not execute a business command, contact the registry, read credentials
or emit secrets. The implementation card must name supported shells, installation
steps and runtime prerequisites, and prove completion against published Native AOT
executables. Do not assume installing the command-line library activates shell
integration. Shell-specific packaging/API details remain an implementation checkpoint,
not permission for an unrelated library upgrade or reflection fallback.

## Sequence and boundaries

Preserve issue #252 -> issue #253, then finish the operational Deployment update
sequence in ADR-058, including retirement, corrective generation and integrated
rehearsal. CLI composition must not delay that adoption-blocking functionality.
Afterward implement command composition/context resolution, then config adapters and
full-model preservation. Contextual group help and shell completion belong to the
CLI workstream. The complete downstream request is not done after extraction alone.

Private product commands, endpoints, packages and legacy compatibility remain outside
upstream scope. This does not introduce a general Provider SDK or server contract
changes. Accepted architecture is distinct from implementation and release evidence.
