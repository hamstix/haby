using System.Globalization;
using System.Text.Json;

namespace Haby.PostgreSql.IntegrationTests;

internal static class WslcPostgreSqlInspection
{
    internal static string Container(string json, WslcPostgreSqlState state)
    {
        using var document = JsonDocument.Parse(json);
        var root = Single(document.RootElement);
        var id = Text(root, "Id");
        if (id.Length != 64 || id.Any(c => !char.IsAsciiHexDigit(c)) ||
            Text(root, "Name").TrimStart('/') != state.Name ||
            state.ContainerId is not null && id != state.ContainerId)
            throw new InvalidDataException();
        Labels(root, state);
        var bindings = new List<(JsonElement Value, bool Requested)>();
        foreach (var (parent, field) in new[] { ("NetworkSettings", "Ports"), ("HostConfig", "PortBindings") })
            if (root.TryGetProperty(parent, out var container) && container.TryGetProperty(field, out var value))
                bindings.Add((value, parent == "HostConfig"));
        if (root.TryGetProperty("Ports"u8, out var ports)) bindings.Add((ports, false));
        if (bindings.Count == 0) throw new InvalidDataException();
        foreach (var (value, _) in bindings)
        {
            JsonElement[] rows;
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("5432/tcp"u8, out var entries) && entries.ValueKind == JsonValueKind.Array)
                rows = entries.EnumerateArray().ToArray();
            else if (value.ValueKind == JsonValueKind.Array)
                rows = value.EnumerateArray().Where(x => Number(x, "ContainerPort") == 5432 && Text(x, "Protocol") == "tcp").ToArray();
            else throw new InvalidDataException();
            if (rows.Length != 1 || Text(rows[0], "HostIp") != state.HostAddress || Number(rows[0], "HostPort") != state.HostPort)
                throw new InvalidDataException();
        }
        var mounts = root.GetProperty("Mounts"u8).EnumerateArray().ToArray();
        if (mounts.Count(x => Text(x, "Type") == "volume" && Text(x, "Name") == state.VolumeName &&
            Text(x, "Destination") == "/var/lib/postgresql/data") != 1)
            throw new InvalidDataException();
        return id;
    }

    internal static void Volume(string json, WslcPostgreSqlState state)
    {
        using var document = JsonDocument.Parse(json);
        var root = Single(document.RootElement);
        if (Text(root, "Name") != state.VolumeName) throw new InvalidDataException();
        Labels(root, state);
    }

    private static void Labels(JsonElement root, WslcPostgreSqlState state)
    {
        var labels = new List<JsonElement>();
        if (root.TryGetProperty("Labels"u8, out var direct)) labels.Add(direct);
        if (root.TryGetProperty("Config"u8, out var config) && config.TryGetProperty("Labels"u8, out var nested)) labels.Add(nested);
        if (labels.Count == 0 || labels.Any(x => Text(x, "haby.test") != "postgresql-fixture" ||
            Text(x, "haby.run") != state.RunId || Text(x, "haby.fixture") != state.FixtureId))
            throw new InvalidDataException();
    }

    internal static bool InventoryContains(string jsonLines, string field, string exactName)
    {
        var count = 0;
        if (jsonLines.AsSpan().TrimStart().StartsWith("[", StringComparison.Ordinal))
        {
            using var array = JsonDocument.Parse(jsonLines);
            count = array.RootElement.EnumerateArray().Count(x => Text(x, field) == exactName);
        }
        else
        foreach (var line in jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (Text(root, field) == exactName) count++;
        }
        if (count > 1) throw new InvalidDataException("Owned resource inventory is ambiguous.");
        return count == 1;
    }

    internal static JsonElement Single(JsonElement root) => root.ValueKind == JsonValueKind.Array
        ? root.GetArrayLength() == 1 ? root[0] : throw new InvalidDataException() : root;
    internal static string Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new InvalidDataException();
    private static int Number(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number ? value.GetInt32() : int.Parse(value.GetString()!, CultureInfo.InvariantCulture);
    }
}
