using System.Text;
using Google.Protobuf;
using Haby.Cli;
using Haby.Operator.Contracts;
using Xunit;

namespace Haby.Cli.Tests;

public sealed class SecretInputTests
{
    internal sealed class Terminal(byte[] bytes, bool redirected = true, string? characters = null) : ISecretTerminal
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new((characters ?? "").Select(c =>
            new ConsoleKeyInfo(c, c == '\r' ? ConsoleKey.Enter : c == '\b' ? ConsoleKey.Backspace : ConsoleKey.A, false, false, false)));
        public bool IsInputRedirected => redirected;
        public Stream OpenStandardInput() => new MemoryStream(bytes, writable: false);
        public ConsoleKeyInfo ReadHiddenKey() => _keys.Dequeue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65_536)]
    [InlineData(65_537)]
    public async Task Stdin_is_bounded_and_preserves_exact_bytes(int size)
    {
        var input = new byte[size];
        Random.Shared.NextBytes(input);
        var terminal = new Terminal(input);
        if (size is 0 or 65_537)
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => SecretInput.ReadAsync(terminal, true, TextWriter.Null, default));
            Assert.Equal("Secret input must contain 1..65,536 bytes.", error.Message);
        }
        else
        {
            var read = await SecretInput.ReadAsync(terminal, true, TextWriter.Null, default);
            Assert.True(read.AsSpan().SequenceEqual(input), "Input bytes must be preserved exactly.");
        }
    }

    [Fact]
    public async Task Stdin_preserves_BOM_CRLF_and_LF_while_interactive_excludes_Enter_and_handles_backspace()
    {
        var input = new byte[] { 0xef, 0xbb, 0xbf, 65, 13, 10, 10 };
        var read = await SecretInput.ReadAsync(new Terminal(input), true, TextWriter.Null, default);
        Assert.True(read.AsSpan().SequenceEqual(input), "BOM and newline bytes must remain unchanged.");
        using var prompts = new StringWriter();
        var interactive = await SecretInput.ReadAsync(new Terminal([], false, "ab\b\u00e9\t\r"), false, prompts, default);
        Assert.True(interactive.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("a\u00e9\t")), "Interactive input must use UTF-8 without Enter.");
        Assert.Contains("hidden", prompts.ToString());
        Assert.True(!prompts.ToString().Contains("a\u00e9", StringComparison.Ordinal), "Prompts must not echo protected input.");
        await Assert.ThrowsAsync<InvalidDataException>(() => SecretInput.ReadAsync(new Terminal(input), false, TextWriter.Null, default));
    }

    [Fact]
    public async Task Interactive_byte_limit_and_empty_input_are_rejected_without_echo()
    {
        foreach (var input in new[] { "\r", new string('\u00e9', 32_769) + "\r", new string('a', 65_537) + "\r" })
        {
            using var prompts = new StringWriter();
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() => SecretInput.ReadAsync(new Terminal([], false, input), false, prompts, default));
            Assert.Equal("Secret input must contain 1..65,536 bytes.", failure.Message);
            Assert.True(!prompts.ToString().Contains('\u00e9'), "Prompts must not echo protected input.");
        }
        var read = await SecretInput.ReadAsync(new Terminal([], false, new string('a', 65_536) + "\r"), false, TextWriter.Null, default);
        Assert.Equal(65_536, read.Length);
    }

    [Fact]
    public void Generated_request_ToString_is_safe_without_material_names_or_request_snapshots()
    {
        var bytes = new byte[32];
        Random.Shared.NextBytes(bytes);
        var request = new CreateSecretRequest { Name = "input-name", RequestId = Guid.NewGuid().ToString(), Material = ByteString.CopyFrom(bytes) };
        Assert.True(request.ToString() == "{ \"request\": \"CreateSecretRequest\", \"material\": \"[redacted]\" }", "Request diagnostics must use the safe redacted representation.");
        Assert.True(!request.ToString().Contains(Convert.ToBase64String(bytes), StringComparison.Ordinal), "Request diagnostics must redact protected input.");
    }
}
