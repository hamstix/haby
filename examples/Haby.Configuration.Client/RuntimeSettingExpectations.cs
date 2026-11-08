using System.Globalization;
using Npgsql;

namespace Haby.Configuration.Client;

/// <summary>Optional independent rehearsal expectations. Contains only allowlisted nonsecret settings.</summary>
public sealed record RuntimeSettingExpectations(int? Timeout, int? CommandTimeout, SslMode? SslMode)
{
    public static RuntimeSettingExpectations Parse(string? timeout, string? commandTimeout, string? sslMode) =>
        new(Seconds(timeout, 300), Seconds(commandTimeout, 3600), sslMode switch
        {
            null => null,
            "disable" => Npgsql.SslMode.Disable,
            "require" => Npgsql.SslMode.Require,
            "verify-full" => Npgsql.SslMode.VerifyFull,
            _ => throw new RuntimeSettingExpectationException("Invalid runtime setting expectations.")
        });

    private static int? Seconds(string? value, int maximum)
    {
        if (value is null) return null;
        if (value.Length is > 0 and <= 4 && value.All(char.IsAsciiDigit) &&
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 1 && seconds <= maximum)
            return seconds;
        throw new RuntimeSettingExpectationException("Invalid runtime setting expectations.");
    }

    public void AssertMatches(NpgsqlConnectionStringBuilder received)
    {
        if ((Timeout is { } timeout && received.Timeout != timeout) ||
            (CommandTimeout is { } commandTimeout && received.CommandTimeout != commandTimeout) ||
            (SslMode is { } sslMode && received.SslMode != sslMode))
            throw new RuntimeSettingExpectationException("Received configuration settings did not match expectations.");
    }
}

public sealed class RuntimeSettingExpectationException(string message) : Exception(message);
