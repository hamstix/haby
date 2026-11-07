using System.Globalization;
using Haby.Application;
using Haby.Domain;
using Npgsql;

namespace Haby.PostgreSql;

public sealed record PostgreSqlConsumerSettings(int Timeout, int CommandTimeout, string SslMode)
{
    public ConfigurationObject ToValue() => new(new Dictionary<string, ConfigurationValue>
    {
        ["timeout"] = ConfigurationNumber.Parse(Timeout.ToString(CultureInfo.InvariantCulture)),
        ["commandTimeout"] = ConfigurationNumber.Parse(CommandTimeout.ToString(CultureInfo.InvariantCulture)),
        ["sslMode"] = new ConfigurationString(SslMode)
    });
}

/// <summary>Closed adapter allow-list. Exact ConfigurationNumber equality avoids floating point conversions.</summary>
public static class PostgreSqlConsumerOptions
{
    public const string Schema = """
        {"type":"object","additionalProperties":false,"properties":{
        "timeout":{"type":"integer","minimum":1,"maximum":300},
        "commandTimeout":{"type":"integer","minimum":1,"maximum":3600},
        "sslMode":{"type":"string","enum":["verify-full","require","disable"]}}}
        """;
    public static ConfigurationObject Defaults { get; } = new PostgreSqlConsumerSettings(15, 30, "verify-full").ToValue();
    public static bool Validate(ConfigurationObject value, bool complete = false) =>
        (!complete || value.Members.Count == 3) && value.Members.All(x => x.Key switch
        {
            "timeout" => Integer(x.Value, 300) is not null,
            "commandTimeout" => Integer(x.Value, 3600) is not null,
            "sslMode" => x.Value is ConfigurationString { Value: "verify-full" or "require" or "disable" },
            _ => false
        });
    public static PostgreSqlConsumerSettings? Parse(ConfigurationObject value) => Validate(value, true)
        ? new(Integer(value.Members["timeout"], 300)!.Value, Integer(value.Members["commandTimeout"], 3600)!.Value,
            ((ConfigurationString)value.Members["sslMode"]).Value) : null;
    private static int? Integer(ConfigurationValue value, int maximum)
    {
        if (value is not ConfigurationNumber number) return null;
        var parts = number.CanonicalText.Split('e');
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var coefficient) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var exponent) || exponent is < 0 or > 3) return null;
        var result = (long)coefficient;
        for (var i = 0; i < exponent; i++) result *= 10;
        return result >= 1 && result <= maximum ? (int)result : null;
    }
    public static ConfigurationObject Normalize(ConfigurationObject value)
    {
        if (!Validate(value)) throw new ArgumentException("Invalid PostgreSQL consumer settings.");
        return new(value.Members.Select(x => new KeyValuePair<string, ConfigurationValue>(x.Key, x.Key switch
        {
            "timeout" => ConfigurationNumber.Parse(Integer(x.Value, 300)!.Value.ToString(CultureInfo.InvariantCulture)),
            "commandTimeout" => ConfigurationNumber.Parse(Integer(x.Value, 3600)!.Value.ToString(CultureInfo.InvariantCulture)),
            _ => x.Value
        })));
    }
}

public sealed record PostgreSqlConnectionRendererContract() : ConfigurationRendererContract(
    PostgreSqlConnectionRendering.Renderer, PostgreSqlApplicationLoginContract.Id,
    PostgreSqlApplicationConnectionContract.Contract, PostgreSqlApplicationConnectionContract.Key,
    OperationId.Create("app-login-access"), PostgreSqlApplicationAccessAction.RegistrationKey, PostgreSqlConnectionRendering.Formatter)
{
    public override ConfigurationObject Defaults => PostgreSqlConsumerOptions.Defaults;
    public override bool ValidateOptions(ConfigurationObject options, bool complete) => PostgreSqlConsumerOptions.Validate(options, complete);
    public override ConfigurationObject NormalizeOptions(ConfigurationObject options) => PostgreSqlConsumerOptions.Normalize(options);
}

public sealed class PostgreSqlConnectionRenderer : IConfigurationRenderer
{
    public ConfigurationRendererId Contract => PostgreSqlConnectionRendering.Renderer;
    public ManifestLocalId Identity => PostgreSqlConnectionRendering.Implementation;
    public ConfigurationFragment? Render(ExternalOperationOutput confirmedOutput, ConfigurationObject settings)
    {
        var connection = PostgreSqlApplicationConnectionContract.Decode(confirmedOutput);
        if (connection is null || PostgreSqlConsumerOptions.Parse(settings) is null) return null;
        return new(new ConfigurationDeferredValue(PostgreSqlConnectionRendering.Formatter, Arguments(connection, settings)));
    }
    internal static ConfigurationObject Arguments(PostgreSqlApplicationConnection connection, ConfigurationObject settings) => new(new Dictionary<string, ConfigurationValue>
    {
        ["host"] = new ConfigurationString(connection.Host),
        ["port"] = ConfigurationNumber.Parse(connection.Port.ToString(CultureInfo.InvariantCulture)),
        ["database"] = new ConfigurationString(connection.DatabaseName),
        ["schema"] = new ConfigurationString(connection.Schema),
        ["username"] = new ConfigurationString(connection.UserName),
        ["password"] = new ConfigurationSecretReference(connection.Password),
        ["settings"] = settings
    });
}

/// <summary>Pure output-derived authority; no renderer, Provider lookup or material I/O.</summary>
public sealed class PostgreSqlConnectionResolutionBinding : IConfigurationResolutionBinding
{
    public bool ValidateMaterial(ReadOnlySpan<char> material)
    {
        if (material.Length != 43) return false;
        foreach (var c in material)
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')) return false;
        return "AEIMQUYcgkosw048".Contains(material[^1], StringComparison.Ordinal);
    }
    public ConfigurationFormatterId Formatter => PostgreSqlConnectionRendering.Formatter;
    public ManifestLocalId Implementation => PostgreSqlConnectionRendering.Implementation;
    public SecretResolutionReference? Bind(ConfigurationRenderPin pin, ExternalOperationOutput output, ConfigurationDeferredValue value)
    {
        if (pin.Renderer != PostgreSqlConnectionRendering.Renderer || pin.RendererImplementation != Implementation ||
            pin.Formatter != Formatter || pin.FormatterImplementation != Implementation || value.Formatter != Formatter ||
            pin.Resource != PostgreSqlApplicationLoginContract.Id || pin.OutputContract != PostgreSqlApplicationConnectionContract.Contract ||
            pin.OutputKey != PostgreSqlApplicationConnectionContract.Key || pin.Producer != OperationId.Create("app-login-access") ||
            pin.ProducerAction != PostgreSqlApplicationAccessAction.RegistrationKey || !PostgreSqlConsumerOptions.Validate(pin.Options, true) ||
            !PostgreSqlConnectionFormatter.Valid(value.Arguments) || PostgreSqlApplicationConnectionContract.Decode(output) is not { } connection ||
            !value.Arguments.Equals(PostgreSqlConnectionRenderer.Arguments(connection, pin.Options))) return null;
        return new(connection.Password, SecretPurpose.ApplicationLogin);
    }
}

public sealed record PostgreSqlConnectionFormatterContract() : ConfigurationFormatterContract(PostgreSqlConnectionRendering.Formatter)
{
    public override bool ValidateArguments(ConfigurationObject arguments) => PostgreSqlConnectionFormatter.Valid(arguments);
}

public sealed class PostgreSqlConnectionFormatter : IConfigurationFormatter
{
    public ConfigurationFormatterId Contract => PostgreSqlConnectionRendering.Formatter;
    public ManifestLocalId Identity => PostgreSqlConnectionRendering.Implementation;
    internal static bool Valid(ConfigurationObject a)
    {
        if (a.Members.Count != 7 || !a.Members.Keys.Order(StringComparer.Ordinal).SequenceEqual(
            new[] { "database", "host", "password", "port", "schema", "settings", "username" })) return false;
        return a.Members["host"] is ConfigurationString h && Uri.CheckHostName(h.Value) != UriHostNameType.Unknown &&
            h.Value.Length <= 255 && Port(a.Members["port"]) is not null &&
            a.Members["database"] is ConfigurationString d && PostgreSqlDatabaseGateway.SafeDatabase.IsMatch(d.Value) &&
            a.Members["schema"] is ConfigurationString s && PostgreSqlApplicationLoginContract.ValidSchema(s.Value) &&
            a.Members["username"] is ConfigurationString u && System.Text.RegularExpressions.Regex.IsMatch(u.Value, @"\Ahaby_a_[a-f0-9]{48}\z") &&
            a.Members["password"] is ConfigurationSecretReference && a.Members["settings"] is ConfigurationObject settings &&
            PostgreSqlConsumerOptions.Parse(settings) is not null;
    }
    private static int? Port(ConfigurationValue value)
    {
        if (value is not ConfigurationNumber n) return null;
        var parts = n.CanonicalText.Split('e');
        if (!int.TryParse(parts[0], out var coefficient) || !int.TryParse(parts[1], out var exponent) || exponent is < 0 or > 4) return null;
        var result = (long)coefficient;
        for (var i = 0; i < exponent; i++) result *= 10;
        return result is >= 1 and <= 65535 ? (int)result : null;
    }
    public ConfidentialConfigurationString? Format(ConfigurationObject arguments, ReadOnlySpan<char> material)
    {
        if (!Valid(arguments) || material.IsEmpty || material.Length > 65536 || material.Contains('\0')) return null;
        var settings = PostgreSqlConsumerOptions.Parse((ConfigurationObject)arguments.Members["settings"])!;
        string Text(string key) => ((ConfigurationString)arguments.Members[key]).Value;
        try
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = Text("host"),
                Port = Port(arguments.Members["port"])!.Value,
                Database = Text("database"),
                Username = Text("username"),
                Password = material.ToString(),
                GssEncryptionMode = GssEncryptionMode.Disable,
                Timeout = settings.Timeout,
                CommandTimeout = settings.CommandTimeout,
                SslMode = settings.SslMode switch { "verify-full" => Npgsql.SslMode.VerifyFull, "require" => Npgsql.SslMode.Require, _ => Npgsql.SslMode.Disable }
            };
            return new(builder.ConnectionString);
        }
        catch (Exception) { return null; }
    }
}

public static class PostgreSqlConnectionRendering
{
    public static ConfigurationRendererId Renderer { get; } = ConfigurationRendererId.Create("postgresql.npgsql-connection/v1alpha1");
    public static ConfigurationFormatterId Formatter { get; } = ConfigurationFormatterId.Create("postgresql.npgsql-connection-string/v1alpha1");
    public static ManifestLocalId Implementation { get; } = ManifestLocalId.Create("postgresql-npgsql-v1");
    public static ConfigurationFormatterCatalog FormatterCatalog() => new([new PostgreSqlConnectionFormatterContract()], [new(Implementation, Formatter)]);
    public static ConfigurationRenderingCatalog CreateCatalog() => new([new PostgreSqlConnectionRendererContract()],
        [new PostgreSqlConnectionRenderer()], FormatterCatalog(), [new PostgreSqlConnectionFormatter()]);
}
