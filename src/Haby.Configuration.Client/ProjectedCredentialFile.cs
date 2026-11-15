using System.Security.Cryptography;

namespace Haby.Configuration.Client;

internal static class ProjectedCredentialFile
{
    internal const int MaximumTokenBytes = 16384;

    // Called on the startup's single owned read worker, including synchronous open/metadata I/O.
    internal static async Task<byte[]> ReadAsync(string path, CancellationToken token)
    {
        var storage = new byte[MaximumTokenBytes + 3];
        try
        {
            token.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0) throw new InvalidDataException();
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var count = 0;
            while (count < storage.Length)
            {
                token.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(storage.AsMemory(count), token);
                if (read == 0) break;
                count += read;
            }
            token.ThrowIfCancellationRequested();
            if (count > MaximumTokenBytes + 2) throw new InvalidDataException();
            if (count > 0 && storage[count - 1] == '\n')
            {
                count--;
                if (count > 0 && storage[count - 1] == '\r') count--;
            }
            if (count is 0 or > MaximumTokenBytes) throw new InvalidDataException();
            foreach (var value in storage.AsSpan(0, count))
                if (!(value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9') &&
                    !"-._~+/="u8.Contains(value)) throw new InvalidDataException();
            return storage.AsSpan(0, count).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(storage); }
    }
}
