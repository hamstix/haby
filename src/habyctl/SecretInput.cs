using System.Text;

namespace Haby.Cli;

internal interface ISecretTerminal
{
    bool IsInputRedirected { get; }
    Stream OpenStandardInput();
    ConsoleKeyInfo ReadHiddenKey();
}

internal sealed class SecretTerminal : ISecretTerminal
{
    public bool IsInputRedirected => Console.IsInputRedirected;
    public Stream OpenStandardInput() => Console.OpenStandardInput();
    public ConsoleKeyInfo ReadHiddenKey() => Console.ReadKey(intercept: true);
}

internal static class SecretInput
{
    internal const int MaxBytes = 65_536;

    internal static async Task<byte[]> ReadAsync(ISecretTerminal terminal, bool stdin, TextWriter error, CancellationToken token)
    {
        if (stdin)
        {
            using var stream = terminal.OpenStandardInput();
            var buffer = new byte[MaxBytes + 1];
            try
            {
                var count = 0;
                while (count < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(count), token);
                    if (read == 0) break;
                    count += read;
                }
                if (count is < 1 or > MaxBytes) throw InvalidInput();
                return buffer.AsSpan(0, count).ToArray();
            }
            finally { Array.Clear(buffer); }
        }
        if (terminal.IsInputRedirected)
            throw new InvalidDataException("Redirected secret input requires --stdin.");
        error.Write("Secret (hidden): ");
        var characters = new char[MaxBytes + 1];
        try
        {
            var count = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var key = terminal.ReadHiddenKey();
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace) { if (count > 0) characters[--count] = '\0'; continue; }
                if (key.KeyChar == '\0') continue;
                if (count == MaxBytes) throw InvalidInput();
                characters[count++] = key.KeyChar;
            }
            var encoding = new UTF8Encoding(false, true);
            if (encoding.GetByteCount(characters.AsSpan(0, count)) is < 1 or > MaxBytes) throw InvalidInput();
            var result = new byte[encoding.GetByteCount(characters.AsSpan(0, count))];
            encoding.GetBytes(characters.AsSpan(0, count), result);
            return result;
        }
        catch (EncoderFallbackException) { throw InvalidInput(); }
        finally { Array.Clear(characters); error.WriteLine(); }
    }

    private static InvalidDataException InvalidInput() => new("Secret input must contain 1..65,536 bytes.");
}
