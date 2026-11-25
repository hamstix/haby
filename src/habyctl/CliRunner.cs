using Haby.Cli.Commands.Manifest;
using System.CommandLine;

namespace Haby.Cli;

internal sealed partial class CliApplication(
    IEndpointSource endpointSource,
    IRegistryClientFactory clientFactory,
    TextWriter output,
    TextWriter error,
    Action<RootCommand>? registerAdditionalCommands = null,
    IConfigEnvironment? configEnvironment = null, ISecretTerminal? secretTerminal = null, HabyCliBuilder? composition = null)
{
    internal const int Success = 0;
    internal const int Rejected = 1;
    internal const int UsageError = 2;
    internal const int TransportError = 3;

    private RootCommand? _root;
    private Option<string?>? _configFile, _contextName, _outputFormat;
    private System.CommandLine.ParseResult? _parsed;
    private readonly Dictionary<Command, string> _paths = [];
    private readonly HashSet<Command> _upstreamGroups = [];
    internal void Initialize()
    {
        if (_root is not null) return;
        var root = composition?.Root ?? new RootCommand("Inspect manifests and plan deployments through a local registry.");
        _root = root;
        var callerCommands = CommandsIn(root);
        var configFile = _configFile = new Option<string?>("--config") { Description = "CLI configuration file.", Recursive = true };
        var contextName = _contextName = new Option<string?>("--context") { Description = "Context for this invocation.", Recursive = true };
        var outputFormat = _outputFormat = new Option<string?>("--output", "-o")
        { Description = "Result format: text (default) or json. Help and version stay human-readable.", Recursive = true };
        root.Options.Add(configFile); root.Options.Add(contextName); root.Options.Add(outputFormat);
        var configuration = new CliConfigurationService(configEnvironment ?? new CliConfigEnvironment());
        var resolver = composition?.Resolver ?? new DefaultCliContextResolver(configuration, endpointSource);
        CliContextSelection Selection() => new(_parsed!.GetValue(configFile), _parsed.GetValue(contextName));
        CliInvocationContext Invocation() => new(output, error, _parsed!.GetValue(outputFormat) == "json", Selection(), resolver,
            composition?.Identity ?? "habyctl");
        var commands = new CliCommandContext(endpointSource, clientFactory, new CliTextPresenter(output, () => RegistryPrefix(root, composition?.RegistryParent ?? root)),
            new CliJsonPresenter(output, () => RegistryPrefix(root, composition?.RegistryParent ?? root)), output, error, configuration,
            () => (_parsed!.GetValue(configFile), _parsed.GetValue(contextName)),
            () => _parsed!.GetValue(outputFormat) == "json", secretTerminal ?? new SecretTerminal(),
            resolver, composition?.Adapter, Invocation,
            () => RegistryPrefix(root, composition?.RegistryParent ?? root));
        if (composition is null || composition.RegistryParent is not null)
        {
            var parent = composition?.RegistryParent ?? root;
            parent.Subcommands.Add(Commands.OperatorAccessCommands.Create(commands));
            parent.Subcommands.Add(ManifestCommands.Create(commands));
            parent.Subcommands.Add(Commands.Configuration.ConfigurationCommands.Create(commands));
            parent.Subcommands.Add(Commands.Profiles.ProfileCommands.Create(commands));
            parent.Subcommands.Add(Commands.Placements.PlacementCommands.Create(commands));
            parent.Subcommands.Add(Commands.Providers.ProviderCommands.Create(commands));
            parent.Subcommands.Add(Commands.Providers.ResourceNamingCommands.Create(commands));
            parent.Subcommands.Add(Commands.Secrets.SecretCommands.Create(commands));
            parent.Subcommands.Add(Commands.Environments.EnvironmentCommands.Create(commands));
            parent.Subcommands.Add(Commands.Planning.PlanningCommands.ChangeSet(commands));
            parent.Subcommands.Add(Commands.Planning.PlanningCommands.Plan(commands));
            parent.Subcommands.Add(Commands.Planning.CommitCommands.Deployment(commands));
            parent.Subcommands.Add(Commands.Operations.OperationCommands.Create(commands));
        }
        if (composition is null || composition.ConfigurationParent is not null)
            (composition?.ConfigurationParent ?? root).Subcommands.Add(Commands.Config.ConfigCommands.Create(commands, composition?.Adapter?.Capabilities));
        foreach (var command in CommandsIn(root).Except(callerCommands))
            if (command.Subcommands.Count > 0 && command.Arguments.Count == 0) _upstreamGroups.Add(command);
        registerAdditionalCommands?.Invoke(root);
        if (composition is not null)
            foreach (var extension in composition.Extensions)
            {
                extension.Command.SetAction((p, ct) => InvokeExtensionAsync(() => extension.Action(p, Invocation(), ct), ct));
                extension.Parent.Subcommands.Add(extension.Command);
            }
        ValidateTree(root);
    }

    internal async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        Initialize();
        ValidateTree(_root!);
        if (VersionOnly(args)) { output.WriteLine(typeof(CliApplication).Assembly.GetName().Version); return Success; }
        _parsed = _root!.Parse(args);
        if (_parsed.Errors.Count > 0)
        { error.WriteLine("Invalid command syntax."); WriteHelp(_parsed.CommandResult.Command, error); return UsageError; }
        if (_parsed.GetValue(_outputFormat!) is { } selectedFormat && selectedFormat is not ("text" or "json"))
        { error.WriteLine("Invalid output format. Use text or json."); return UsageError; }
        if (args.Any(x => x is "--help" or "-h" or "-?" or "/h" or "/?"))
        { WriteHelp(_parsed.CommandResult.Command, output); return Success; }
        if (_upstreamGroups.Contains(_parsed.CommandResult.Command) || _parsed.CommandResult.Command.Action is null ||
            (args.Length == 0 && composition is null))
        { error.WriteLine("A command is required."); WriteHelp(_parsed.CommandResult.Command, error); return UsageError; }
        return await _parsed.InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, cancellationToken);
    }

    private static HashSet<Command> CommandsIn(Command root)
    {
        var result = new HashSet<Command>(); var pending = new Stack<Command>(); pending.Push(root);
        while (pending.TryPop(out var command))
            if (result.Add(command)) foreach (var child in command.Subcommands) pending.Push(child);
        return result;
    }

    private async Task<int> InvokeExtensionAsync(Func<Task<int>> action, CancellationToken cancellationToken)
    {
        try
        {
            var exit = await action();
            return exit is >= 0 and <= 3 ? exit : TransportError;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { error.WriteLine("Operation canceled."); return TransportError; }
        catch (Exception exception)
        { error.WriteLine(CliApplication.SafeError(exception)); return TransportError; }
    }

    private void ValidateTree(RootCommand root)
    {
        _paths.Clear();
        var visiting = new HashSet<Command>();
        void Visit(Command command, string path, HashSet<string> inherited)
        {
            if (!visiting.Add(command)) throw new InvalidOperationException("A command must be mounted exactly once.");
            _paths.Add(command, path);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in command.Subcommands)
                foreach (var name in new[] { child.Name }.Concat(child.Aliases))
                    if (!names.Add(name)) throw new InvalidOperationException("Command name or alias conflict.");
            var options = new HashSet<string>(inherited, StringComparer.Ordinal);
            foreach (var option in command.Options)
                foreach (var name in new[] { option.Name }.Concat(option.Aliases))
                    if (!options.Add(name)) throw new InvalidOperationException("Option name or alias conflict.");
            var recursive = new HashSet<string>(inherited, StringComparer.Ordinal);
            foreach (var option in command.Options.Where(x => x.Recursive))
                foreach (var name in new[] { option.Name }.Concat(option.Aliases)) recursive.Add(name);
            foreach (var child in command.Subcommands) Visit(child, path + " " + child.Name, recursive);
        }
        Visit(root, composition?.Identity ?? "habyctl", []);
        if (composition?.RegistryParent is { } registry && !_paths.ContainsKey(registry) ||
            composition?.ConfigurationParent is { } config && !_paths.ContainsKey(config))
            throw new InvalidOperationException("Mount parent must belong to the root command tree.");
    }
    private string RegistryPrefix(RootCommand root, Command parent) => _paths.TryGetValue(parent, out var value) ? value : composition?.Identity ?? "habyctl";
    private void WriteHelp(Command command, TextWriter writer)
    {
        writer.WriteLine(command.Description);
        writer.WriteLine("Usage:"); writer.WriteLine($"  {_paths[command]}{(command.Subcommands.Count > 0 ? " <command>" : "")} [options]");
        foreach (var argument in command.Arguments) writer.WriteLine($"  <{argument.Name}> {argument.Description}");
        writer.WriteLine("Options:");
        var options = command.Options.AsEnumerable();
        var current = command;
        while (current != _root)
        {
            var parent = _paths.Keys.Single(x => x.Subcommands.Contains(current));
            options = parent.Options.Where(x => x.Recursive).Concat(options); current = parent;
        }
        foreach (var option in options.Distinct())
            writer.WriteLine($"  {option.Name} {string.Join(" ", option.Aliases)} {option.Description}");
        if (command.Subcommands.Count > 0)
        {
            writer.WriteLine("Commands:");
            foreach (var child in command.Subcommands) writer.WriteLine($"  {child.Name} {string.Join(" ", child.Aliases)} {child.Description}");
        }
    }

    private static bool VersionOnly(string[] args)
    {
        var version = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--version" && !version) version = true;
            else if (args[index] is "--config" or "--output" or "-o" && index + 1 < args.Length)
            {
                if (args[index] is "--output" or "-o" && args[index + 1] is not ("text" or "json"))
                    return false;
                index++;
            }
            else return false;
        }
        return version;
    }
}
