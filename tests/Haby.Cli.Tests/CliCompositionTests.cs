using System.CommandLine;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class CliCompositionTests
{
    [Fact]
    public async Task Complete_registry_tree_mounts_without_local_configuration_and_preserves_options()
    {
        RootCommand? original = null;
        var baseline = new CliApplication(new NoEndpoint(), new NoClient(), new StringWriter(), new StringWriter(), r => original = r);
        baseline.Initialize();
        var resolver = new Resolver(); var builder = NewBuilder(resolver); var parent = new Command("registry");
        builder.Root.Subcommands.Add(parent); builder.MountRegistry(parent); builder.Build();
        Assert.Equal(Inventory(original!, false), Inventory(parent, true));
        Assert.DoesNotContain(parent.Subcommands, x => x.Name == "config");
        Assert.Equal(new[] { "--config", "--context", "--output" }, builder.Root.Options.Where(x => x.Name != "--help" && x.Name != "--version").Select(x => x.Name));
        using var output = new StringWriter(); using var error = new StringWriter();
        var cli = NewBuilder(resolver); cli.Output = output; cli.Error = error;
        var group = new Command("registry"); cli.Root.Subcommands.Add(group); cli.MountRegistry(group);
        var runtime = cli.Build();
        Assert.Equal(2, await runtime.RunAsync(["registry", "manifest", "--output", "json"]));
        Assert.Contains("downstream registry manifest", error.ToString()); Assert.Contains("Commands:", error.ToString());
        Assert.Equal("", output.ToString()); Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, await runtime.RunAsync(["--config", "absent", "registry", "manifest", "--help"]));
        Assert.Contains("downstream registry manifest", output.ToString()); Assert.Equal(0, resolver.Calls);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("alias")]
    [InlineData("option")]
    public void Finalization_rejects_conflicts_before_any_resolver_or_handler(string conflict)
    {
        var resolver = new Resolver(); var builder = NewBuilder(resolver); builder.MountRegistry(builder.Root);
        var command = new Command(conflict == "command" ? "manifest" : "custom");
        if (conflict == "alias") command.Aliases.Add("manifest");
        if (conflict == "option") command.Options.Add(new Option<string>("--context"));
        builder.AddCommand(builder.Root, command, (_, _, _) => throw new InvalidOperationException("Invoked"));
        Assert.Throws<InvalidOperationException>(() => builder.Build()); Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task Custom_context_gets_options_at_root_and_nested_positions_without_resolving_credentials()
    {
        var resolver = new Resolver(); var builder = NewBuilder(resolver);
        var parent = new Command("tools"); builder.Root.Subcommands.Add(parent);
        var command = new Command("inspect");
        CliContextSelection? captured = null;
        builder.AddCommand(parent, command, (_, context, _) => { captured = context.Selection; return Task.FromResult(0); });
        var runtime = builder.Build();
        Assert.Equal(0, await runtime.RunAsync(["--context", "first", "tools", "inspect", "--config", "chosen", "--output", "json"]));
        Assert.Equal(new("chosen", "first"), captured); Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, await runtime.RunAsync(["tools", "--context", "next", "inspect", "--config", "other"]));
        Assert.Equal(new("other", "next"), captured); Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task Custom_session_validates_transport_before_lazy_credentials_and_keeps_caller_ownership()
    {
        var source = new OwnedSource();
        var builder = new HabyCliBuilder("downstream", "Ownership", source) { Output = new StringWriter(), Error = new StringWriter() };
        builder.AddCommand(builder.Root, new Command("call"), async (_, context, token) =>
        { using var session = await context.OpenRegistryAsync(token); return 0; });
        var runtime = builder.Build();
        Assert.Equal(2, await runtime.RunAsync(["call"]));
        Assert.Equal(1, source.Calls); Assert.Equal(0, source.CredentialCalls); Assert.False(source.Disposed);
        source.Endpoint = new Uri("http://127.0.0.1:1");
        Assert.Equal(0, await runtime.RunAsync(["call"]));
        Assert.Equal(2, source.Calls); Assert.Equal(1, source.CredentialCalls); Assert.False(source.Disposed);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Equal(3, await runtime.RunAsync(["call"], canceled.Token));
        Assert.Equal(2, source.Calls); Assert.Equal(1, source.CredentialCalls); Assert.False(source.Disposed);
        source.Dispose(); Assert.True(source.Disposed);
    }

    [Fact]
    public async Task Custom_failures_and_cancellation_are_safe_and_runtime_remains_reusable()
    {
        var builder = NewBuilder(new Resolver()); var error = new StringWriter(); builder.Error = error;
        builder.AddCommand(builder.Root, new Command("fail"), (_, _, _) => throw new InvalidOperationException("credential-sentinel"));
        builder.AddCommand(builder.Root, new Command("file-fail"), (_, _, _) => throw new IOException("credential-sentinel"));
        builder.AddCommand(builder.Root, new Command("cancel"), async (_, _, token) => { await Task.Delay(Timeout.Infinite, token); return 0; });
        builder.AddCommand(builder.Root, new Command("ok"), (_, _, _) => Task.FromResult(0));
        var runtime = builder.Build();
        Assert.Equal(3, await runtime.RunAsync(["fail"])); Assert.DoesNotContain("credential-sentinel", error.ToString());
        Assert.Equal(3, await runtime.RunAsync(["file-fail"])); Assert.DoesNotContain("credential-sentinel", error.ToString());
        using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        Assert.Equal(3, await runtime.RunAsync(["cancel"], canceled.Token));
        Assert.Equal(0, await runtime.RunAsync(["ok"]));
    }

    [Fact]
    public async Task Caller_group_action_is_preserved_alongside_its_subcommands()
    {
        var builder = NewBuilder(new Resolver()); var calls = 0;
        var parent = new Command("tools"); parent.Subcommands.Add(new Command("child"));
        builder.AddCommand(builder.Root, parent, (_, _, _) => { calls++; return Task.FromResult(0); });
        Assert.Equal(0, await builder.Build().RunAsync(["tools"])); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("resolver")]
    [InlineData("view")]
    public async Task Adapter_failures_do_not_expose_exception_material(string mode)
    {
        var source = new ThrowingAdapter(); using var error = new StringWriter();
        var builder = new HabyCliBuilder("downstream", "Failure", source) { Output = new StringWriter(), Error = error };
        builder.MountRegistry(builder.Root).MountConfiguration(builder.Root, source);
        var args = mode == "resolver" ? new[] { "operator", "access", "list" } : new[] { "config", "view" };
        Assert.Equal(3, await builder.Build().RunAsync(args));
        Assert.DoesNotContain("credential-sentinel", error.ToString());
    }
    private sealed class ThrowingAdapter : ICliContextResolver, ICliConfigurationAdapter
    {
        public CliConfigurationCapabilities Capabilities => CliConfigurationCapabilities.View;
        public ValueTask<CliResolvedContext> ResolveAsync(CliContextSelection selection, CancellationToken cancellationToken) => throw new InvalidOperationException("credential-sentinel");
        public ValueTask<string?> GetCurrentContextAsync(CliContextSelection selection, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public ValueTask WriteSafeViewAsync(CliContextSelection selection, CliInvocationContext invocation, CancellationToken cancellationToken) => throw new InvalidOperationException("credential-sentinel");
        public ValueTask ApplyCredentialAsync(CliContextSelection selection, CliCredentialChange change, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private sealed class OwnedSource : ICliContextResolver, ICliCredentialSource, IDisposable
    {
        public int Calls, CredentialCalls; public bool Disposed;
        public Uri Endpoint = new("http://remote.example");
        public ValueTask<CliResolvedContext> ResolveAsync(CliContextSelection selection, CancellationToken cancellationToken)
        { Calls++; return ValueTask.FromResult(new CliResolvedContext(Endpoint, "dev", this)); }
        public ValueTask<string?> GetCredentialAsync(CancellationToken cancellationToken)
        { CredentialCalls++; return ValueTask.FromResult<string?>(null); }
        public void Dispose() => Disposed = true;
    }

    private static HabyCliBuilder NewBuilder(Resolver resolver) => new("downstream", "Composition test", resolver) { Output = new StringWriter(), Error = new StringWriter() };
    private static string[] Inventory(Command root, bool all)
    {
        var values = new List<string>();
        void Visit(Command command, string path)
        {
            foreach (var c in command.Subcommands.Where(c => all || c.Name != "config"))
            {
                var name = path + c.Name; values.Add(name + "|" + string.Join(',', c.Aliases) + "|" + string.Join(',', c.Options.Select(x => x.Name)) + "|" + string.Join(',', c.Arguments.Select(x => x.Name)));
                Visit(c, name + " ");
            }
        }
        Visit(root, ""); return values.Order(StringComparer.Ordinal).ToArray();
    }
    private sealed class Resolver : ICliContextResolver
    {
        public int Calls;
        public ValueTask<CliResolvedContext> ResolveAsync(CliContextSelection selection, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("Unexpected resolver"); }
    }
    private sealed class NoEndpoint : IEndpointSource
    { public string? GetAddress() => null; public string? GetSkipTlsValidation() => null; }
    private sealed class NoClient : IRegistryClientFactory
    { public IRegistryClient Create(Uri address, bool skipTlsValidation, string? credential = null) => throw new InvalidOperationException(); }
}
