# Public CLI composition

`Haby.Cli` is a product-neutral, Native AOT compatible assembly. `habyctl` is
its thin executable entry point. The library explicitly links the established
command implementation from `src/habyctl`; the executable compiles only its
entry point. This keeps one command implementation without a bulk source move.
No package publication or SDK installation is required to use the project reference.

## Compose one command tree

See the public-only [executable example](../../examples/Haby.Cli.Composition/Program.cs).
It references `Haby.Cli`, uses no internal types or friend assembly, and nests
all 13 upstream remote command groups under `registry`. Local `config` commands
are mounted separately. Custom commands and their root options remain caller-owned.

```csharp
var builder = new HabyCliBuilder("my-tool", "My operator tool", resolver);
var registry = new Command("registry", "Registry operations");
builder.Root.Subcommands.Add(registry);
builder.MountRegistry(registry);
builder.MountConfiguration(builder.Root, configurationAdapter); // optional
builder.AddCommand(builder.Root, new Command("inspect"), async (_, context, ct) =>
{
    using var session = await context.OpenRegistryAsync(ct);
    // session.Client is the standard generated ManifestOperator client.
    // Pass ct to the generated call. session.Environment is the selected environment.
    return 0;
});
return await builder.Build().RunAsync(args);
```

`Build` finalizes registration once. Command names, aliases and inherited option
aliases must be unique; conflicts fail before handlers or resolvers run. The
same command cannot be mounted twice. The command tree remains inspectable;
mutating it after finalization is unsupported and invocation revalidates conflicts.
Do not replace upstream actions or security services after finalization.
The runtime serializes invocations because option selection belongs to an invocation.

`--config`, `--context` and `--output`/`-o` are registered once as recursive root
options. They work before and after nested command names. Text/JSON output keeps
the existing result contracts and exit codes: 0 success, 1 domain rejection,
2 usage/configuration failure, 3 transport/cancellation failure. Help and version
remain human-readable; bare groups show contextual usage without resolving
configuration, endpoints or credentials. Help and recovery instructions use the
selected executable identity and registry mount path. Shell completion is separate.

## Resolve metadata, then credentials

`ICliContextResolver` receives the explicit configuration/context selection and
returns `CliResolvedContext`: endpoint, environment, transport policy and an
optional `ICliCredentialSource`. Metadata selection must not read or print token
material. A credential source is invoked only when an RPC needs credentials,
after endpoint policy validation. Help, custom offline commands and configuration
view do not invoke it. `CliCredentials.ReadTokenFileAsync` reuses the bounded
upstream token parser. The source and resolver remain caller-owned; the library
does not dispose them. A returned `CliRegistrySession` owns its channel and must
be disposed by the custom command, including on errors and cancellation.

Custom commands get bounded invocation output, selection and source-generated JSON
output, plus a standard authenticated generated client through `OpenRegistryAsync`.
They do not gain database, Kubernetes or server implementation access. The same
bearer-token grammar and endpoint policy apply: HTTPS normally, loopback HTTP for
local operation, and remote insecure HTTP only by explicit selected policy. TLS
validation bypass also requires explicit selected policy. Cancellation is passed
through resolution, credential reads and RPC calls. Never log resolver/source
objects or credential values. Exception details from custom handlers are withheld.

## Select configuration capabilities explicitly

Without `MountConfiguration`, no local `config` group is installed. An optional
`ICliConfigurationAdapter` declares `View`, `CurrentContext` and/or `SetCredential`;
only those existing common commands appear. There is no implicit generic
context/instance CRUD editor. Unsupported mutations must refuse before writing.
`ApplyCredentialAsync` receives a typed mutation, while the adapter owns the whole
model, validation and persistence. Preserve every downstream field or refuse the
write. Never round-trip an unknown schema through the smaller upstream YAML model.

`WriteSafeViewAsync` must deliberately map the complete model to a safe DTO with
source-generated JSON metadata. There is no reflection/object/dictionary fallback
for arbitrary downstream models and no claim that arbitrary fields are safe by
name. The example treats its `note` and `values` as declared public data and maps
both its token-file field and downstream-only secret to redacted placeholders.
Its closed versioned JSON document uses JSON syntax (a YAML subset), rejects
unknown schema fields, and preserves every supported field during a file-token
change. It refuses inline credentials and unknown instances. Writes use a new
file, an owned temporary file, cancellation and restrictive Unix creation mode;
the original is never overwritten. This is an example adapter, not a general YAML editor.

`CliConfigurationPaths` defines distribution-specific environment/default paths
and explicit `~` expansion. Explicit `--config` wins and invalid explicit files
never fall back. `CreateDefault()` preserves `habyctl` configuration defaults;
`CreateDefault(identity, paths)` reuses upstream YAML semantics with selected paths.
The example deliberately defaults to `~/.haby/config.yaml`, overridable by
`HABY_SAMPLE_CONFIG` and then `--config`.

## Verification boundary

The external executable test uses only public references and disposable loopback
RPC hosts. It checks root custom commands, nested help, authenticated built-in and
custom generated calls, configuration precedence, explicit redaction, preserved
downstream values and refusal paths. The existing Windows/Linux Native AOT matrix
publishes both `habyctl` and this example with warnings as errors, requires the
published example path in its loopback test, and records its executable hash.
A managed local run is not published Native AOT or installed Final evidence.
No server RPC contract, public deployment capability, NuGet release or shell
completion is introduced by this composition surface.
