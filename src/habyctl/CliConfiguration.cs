using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Haby.Cli;

internal sealed record CliInstance(string Endpoint);
internal sealed record CliNamedContext(string Instance, string EnvironmentId);
internal sealed record CliConfiguration(
    string? CurrentContext,
    IReadOnlyDictionary<string, CliInstance> Instances,
    IReadOnlyDictionary<string, CliNamedContext> Contexts)
{
    internal static CliConfiguration Empty { get; } = new(null,
        new SortedDictionary<string, CliInstance>(StringComparer.Ordinal),
        new SortedDictionary<string, CliNamedContext>(StringComparer.Ordinal));
}

internal interface IConfigEnvironment
{
    string? GetConfigPath();
    string GetDefaultPath();
}

internal sealed class CliConfigEnvironment : IConfigEnvironment
{
    public string? GetConfigPath() => Environment.GetEnvironmentVariable("HABYCTL_CONFIG");
    public string GetDefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "habyctl", "config.yaml");
}

internal sealed class CliConfigurationService(IConfigEnvironment environment)
{
    internal const int MaxBytes = 64 * 1024;
    private const int MaxEntries = 32;
    private const int MaxScalarLength = 2048;

    internal async Task<CliConfiguration> LoadAsync(string? overridePath, CancellationToken cancellationToken)
    {
        var selectedPath = overridePath ?? environment.GetConfigPath();
        var explicitSelection = selectedPath is not null;
        var path = selectedPath ?? environment.GetDefaultPath();
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous);
            if (stream.Length > MaxBytes) throw Invalid();
            var buffer = new byte[MaxBytes + 1];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken);
                if (read == 0) break;
                count += read;
            }
            if (count > MaxBytes) throw Invalid();
            var text = new UTF8Encoding(false, true).GetString(buffer, 0, count);
            if (text.StartsWith('\uFEFF')) text = text[1..];
            return Parse(text);
        }
        catch (FileNotFoundException) when (!explicitSelection)
        {
            return CliConfiguration.Empty;
        }
        catch (DirectoryNotFoundException) when (!explicitSelection)
        {
            return CliConfiguration.Empty;
        }
        catch (Exception exception) when (exception is DecoderFallbackException or YamlException or
            ArgumentException or NotSupportedException)
        {
            throw Invalid();
        }
    }

    internal static CliConfiguration Parse(string text)
    {
        try
        {
            using (var scan = new StringReader(text))
            {
                var parser = new Parser(scan);
                var depth = 0;
                while (parser.MoveNext())
                {
                    if (parser.Current is AnchorAlias) throw Invalid();
                    if (parser.Current is MappingStart or SequenceStart && ++depth > 8) throw Invalid();
                    if (parser.Current is MappingEnd or SequenceEnd) depth--;
                }
            }
            var yaml = new YamlStream();
            yaml.Load(new StringReader(text));
            if (yaml.Documents.Count != 1) throw Invalid();
            var root = Mapping(yaml.Documents[0].RootNode, 0);
            Fields(root, "formatVersion", "currentContext", "instances", "contexts");
            if (Scalar(Required(root, "formatVersion"), 1, true) != "1") throw Invalid();
            var current = root.TryGetValue("currentContext", out var currentNode)
                ? Scalar(currentNode, 1) : null;
            var instances = new SortedDictionary<string, CliInstance>(StringComparer.Ordinal);
            foreach (var (name, node) in Mapping(Required(root, "instances"), 1))
            {
                Name(name);
                var values = Mapping(node, 2);
                Fields(values, "endpoint");
                var endpoint = Scalar(Required(values, "endpoint"), 3);
                if (CliApplication.ParseEndpoint(endpoint) is null) throw Invalid();
                instances.Add(name, new CliInstance(endpoint));
            }
            var contexts = new SortedDictionary<string, CliNamedContext>(StringComparer.Ordinal);
            foreach (var (name, node) in Mapping(Required(root, "contexts"), 1))
            {
                Name(name);
                var values = Mapping(node, 2);
                Fields(values, "instance", "environmentId");
                var instance = Scalar(Required(values, "instance"), 3);
                var environmentId = Scalar(Required(values, "environmentId"), 3);
                Name(instance);
                Name(environmentId);
                contexts.Add(name, new CliNamedContext(instance, environmentId));
            }
            foreach (var context in contexts.Values)
                if (!instances.ContainsKey(context.Instance)) throw Invalid();
            if (current is not null && !contexts.ContainsKey(current)) throw Invalid();
            return new CliConfiguration(current, instances, contexts);
        }
        catch (Exception exception) when (exception is YamlException or ArgumentException or
            InvalidOperationException or OverflowException)
        {
            throw Invalid();
        }
    }

    private static Dictionary<string, YamlNode> Mapping(YamlNode node, int depth)
    {
        CheckNode(node, depth);
        if (node is not YamlMappingNode mapping || mapping.Children.Count > MaxEntries) throw Invalid();
        var result = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        foreach (var (key, value) in mapping.Children)
        {
            var name = Scalar(key, depth + 1);
            if (!result.TryAdd(name, value)) throw Invalid();
        }
        return result;
    }

    private static string Scalar(YamlNode node, int depth, bool version = false)
    {
        CheckNode(node, depth);
        if (node is not YamlScalarNode scalar || scalar.Value is not { Length: > 0 and <= MaxScalarLength } value)
            throw Invalid();
        if (!version && scalar.Style == ScalarStyle.Plain &&
            (value is "true" or "false" or "True" or "False" or "null" or "Null" or "NULL" or "~" ||
             double.TryParse(value, System.Globalization.NumberStyles.Float,
                 System.Globalization.CultureInfo.InvariantCulture, out _))) throw Invalid();
        return value;
    }

    private static void CheckNode(YamlNode node, int depth)
    {
        if (depth > 8 || !node.Anchor.IsEmpty || !node.Tag.IsEmpty)
            throw Invalid();
    }

    private static YamlNode Required(Dictionary<string, YamlNode> mapping, string name) =>
        mapping.TryGetValue(name, out var node) ? node : throw Invalid();

    private static void Fields(Dictionary<string, YamlNode> mapping, params string[] allowed)
    {
        foreach (var key in mapping.Keys)
            if (!allowed.Contains(key, StringComparer.Ordinal) || key == "<<") throw Invalid();
        if (mapping.Count > MaxEntries) throw Invalid();
    }

    private static void Name(string value)
    {
        if (value.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            throw Invalid();
    }

    private static InvalidDataException Invalid() => new("Invalid CLI configuration.");
}
