using System.Text;

namespace Haby.Cli;

internal static class CliOperatorCredential
{
    internal static bool Valid(string value) => value.Length == 114 && value.StartsWith("haby_operator_v1.", StringComparison.Ordinal) &&
        value[49] == '.' && Guid.TryParseExact(value.AsSpan(17, 32), "N", out var id) && id != Guid.Empty &&
        value.AsSpan(50).IndexOfAnyExcept("0123456789abcdef") < 0;

    internal static async Task<string?> ResolveAsync(CliInstance instance, CancellationToken token)
    {
        if (instance.TokenFile is { } file)
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            return await ReadAsync(stream, token);
        }
        return instance.Token;
    }
    internal static async Task<string> ReadAsync(Stream stream, CancellationToken token)
    {
        var bytes = new byte[513];
        try
        {
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), token);
                if (read == 0) break;
                count += read;
            }
            if (count > 512) throw new InvalidDataException();
            string value;
            try { value = new UTF8Encoding(false, true).GetString(bytes, 0, count); }
            catch (DecoderFallbackException) { throw new InvalidDataException(); }
            if (value.EndsWith("\r\n", StringComparison.Ordinal)) value = value[..^2];
            else if (value.EndsWith('\n')) value = value[..^1];
            if (!Valid(value)) throw new InvalidDataException();
            return value;
        }
        finally { Array.Clear(bytes); }
    }
}
