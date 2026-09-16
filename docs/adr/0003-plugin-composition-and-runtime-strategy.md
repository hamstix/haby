# ADR-003: Plugin composition and runtime strategy

- Status: Accepted
- Date: 2026-09-16

## Context

Haby currently discovers plugin bootstraps by inspecting runtime assemblies and
creating their implementations through reflection. The plugin projects are
nevertheless referenced by the server project and are therefore already selected
at build time.

Haby continues to use CoreCLR as its primary runtime. The project intends to make
incremental progress towards Native AOT compatibility, starting with partial
trimming where it is practical. Native AOT does not support loading arbitrary
managed assemblies after publication, so runtime DLL loading cannot be the
foundation of the public plugin API.

The initial users and plugin authors are expected to work closely with the Haby
codebase. A separately deployed provider protocol would add lifecycle,
compatibility, security and operational concerns before there is enough product
experience to design it well.

## Decision

### Build-time composition is the supported plugin model

Official plugins and other trusted modules are selected and composed when a Haby
distribution is built. Official plugin projects may live in the main solution.
An external plugin may be referenced as a project or package, but it must depend
only on the public plugin abstractions and not on `Haby.Server`.

Plugin registration will become explicit and compatible with trimming. Runtime
assembly scanning is not part of the target architecture. A source generator may
reduce registration boilerplate in the future, but all generated registrations
must still be determined at build time.

Official distributions may include a standard set of plugins. A downstream user
may create a small composition project that references Haby and a different set
of trusted plugins, then builds its own distribution.

### Public contracts remain transport-neutral

Plugin contracts must describe Haby capabilities such as validation,
provisioning, publishing and commands without depending on their execution
transport. They must not expose server implementation details such as EF Core
entities, ASP.NET Core endpoints or generated server transport types.

Transport-neutral does not mean that an out-of-process transport must be
implemented now. M2 will provide only the in-process, build-time composition
model. The contracts should avoid decisions that would unnecessarily prevent a
future adapter, but no speculative gRPC protocol or provider lifecycle will be
designed as part of M2.

### CoreCLR remains supported

Haby will continue to run on CoreCLR. Native AOT is a direction for improving
deployment characteristics, not a current requirement for every Haby project or
distribution.

The next compatibility step is partial trimming. Trimming and AOT analysis will
be enabled incrementally for the front end and for library projects that
explicitly claim compatibility. The server and persistence integration are not
required to pass a warning-free Native AOT gate while their dependencies and
data-access strategy remain incompatible or unproven.

New public libraries and plugin abstractions should avoid unnecessary reflection,
runtime code generation and implicit type discovery. Any project that claims
trimming or AOT compatibility must validate that claim in CI.

### Runtime installation is deferred

Haby will not currently provide an API for installing and loading arbitrary
managed DLLs. A CoreCLR-only DLL loader is not planned for M2 and will not be
developed as a compatibility mode without a separate use case and architectural
decision.

Runtime extensibility through an external process or container and a versioned
protocol remains a possible future design. It will be considered only after the
in-process contracts and operation model have been validated by real deployments.

WebAssembly is not a planned plugin execution model.

## Consequences

- Plugin selection requires building and publishing a Haby distribution.
- Official plugin projects can be developed and tested in the main solution.
- Consumers can create custom distributions without forking or modifying the
  Haby server project.
- Plugin startup does not depend on runtime assembly scanning.
- The public plugin API must remain smaller and more stable than the host's DI
  and persistence implementation.
- Native AOT work can proceed incrementally without removing CoreCLR support.
- M2 does not incur the cost of process management, protocol negotiation or
  remote plugin security.
- Installing a DLL through the administration UI is not a supported extension
  workflow.

## Deferred decisions

The following require separate ADRs if product experience demonstrates a need:

- an out-of-process provider protocol and provider lifecycle;
- discovery, packaging and distribution of external providers;
- a CoreCLR-only managed DLL compatibility loader;
- the point at which a Native AOT server distribution becomes supported;
- replacement or adaptation of persistence dependencies for Native AOT.
