using System.Globalization;
using System.Text;

namespace Haby.PostgreSql.IntegrationTests;

// Native Linux units are deliberately distinct from wall-clock DateTime ticks.
internal sealed record LinuxRelayIdentity(int FormatVersion, int Pid, string BootId, ulong StartTime,
    string PidNamespace, string Executable, string[] Arguments);

internal class LinuxRelayIdentityReader
{
    internal virtual LinuxRelayIdentity Read(int pid)
    {
        if (!OperatingSystem.IsLinux() || pid <= 0) throw new InvalidDataException();
        var root = $"/proc/{pid}";
        var boot = ReadText("/proc/sys/kernel/random/boot_id").TrimEnd('\n');
        var currentNamespace = new FileInfo("/proc/self/ns/pid").LinkTarget;
        var processNamespace = new FileInfo(root + "/ns/pid").LinkTarget;
        if (string.IsNullOrEmpty(currentNamespace) || currentNamespace != processNamespace) throw new InvalidDataException();
        var stat = ParseStat(pid, ReadText(root + "/stat"));
        var command = ReadText(root + "/cmdline");
        if (!command.EndsWith('\0')) throw new InvalidDataException();
        var arguments = command[..^1].Split('\0');
        var executable = new FileInfo(root + "/exe").LinkTarget;
        if (string.IsNullOrEmpty(executable) || !Path.IsPathFullyQualified(executable) ||
            arguments.Length < 2 || arguments.Any(string.IsNullOrEmpty) ||
            !Guid.TryParseExact(boot, "D", out var bootId) || bootId == Guid.Empty || bootId.ToString("D") != boot)
            throw new InvalidDataException();
        // Detect changes during the multi-file observation. This is not a pidfd.
        if (ParseStat(pid, ReadText(root + "/stat")) != stat ||
            ReadText("/proc/sys/kernel/random/boot_id").TrimEnd('\n') != boot ||
            new FileInfo(root + "/ns/pid").LinkTarget != currentNamespace) throw new InvalidDataException();
        return new(1, pid, boot, stat, currentNamespace, executable, arguments);
    }

    internal void RequireMatch(LinuxRelayIdentity expected, int pid, string executable, string script, string run, string fixture, string? resolvedExecutable = null)
    {
        ValidateCommand(expected, pid, executable, script, run, fixture, resolvedExecutable);
        var actual = Read(pid);
        ValidateCommand(actual, pid, executable, script, run, fixture, resolvedExecutable);
        if (actual.FormatVersion != expected.FormatVersion || actual.Pid != expected.Pid ||
            actual.BootId != expected.BootId || actual.StartTime != expected.StartTime ||
            actual.PidNamespace != expected.PidNamespace || actual.Executable != expected.Executable ||
            !actual.Arguments.SequenceEqual(expected.Arguments, StringComparer.Ordinal)) throw new InvalidDataException();
    }

    internal static void ValidateCommand(LinuxRelayIdentity identity, int pid, string executable, string script, string run, string fixture, string? resolvedExecutable = null)
    {
        if (identity.FormatVersion != 1 || pid <= 0 || identity.Pid != pid || identity.StartTime == 0 ||
            !Guid.TryParseExact(identity.BootId, "D", out var boot) || boot == Guid.Empty || boot.ToString("D") != identity.BootId ||
            string.IsNullOrEmpty(identity.PidNamespace) ||
            !Guid.TryParseExact(run, "N", out _) || !Guid.TryParseExact(fixture, "N", out _) ||
            !Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(script) ||
            identity.Executable != (resolvedExecutable ?? executable) || identity.Arguments is not { Length: >= 6 } ||
            identity.Arguments[0] != executable || identity.Arguments[1] != script ||
            identity.Arguments.Any(string.IsNullOrEmpty)) throw new InvalidDataException();
        foreach (var pair in new[] { ("--run-id", run), ("--fixture-id", fixture) })
        {
            var positions = Enumerable.Range(2, identity.Arguments.Length - 2).Where(i => identity.Arguments[i] == pair.Item1).ToArray();
            if (positions.Length != 1 || positions[0] + 1 >= identity.Arguments.Length || identity.Arguments[positions[0] + 1] != pair.Item2)
                throw new InvalidDataException();
        }
    }

    internal static ulong ParseStat(int pid, string text)
    {
        var open = text.IndexOf(" (", StringComparison.Ordinal);
        var close = text.LastIndexOf(')');
        if (pid <= 0 || open < 1 || close <= open + 2 || close + 2 >= text.Length || text[close + 1] != ' ' ||
            !int.TryParse(text.AsSpan(0, open), NumberStyles.None, CultureInfo.InvariantCulture, out var actualPid) || actualPid != pid)
            throw new InvalidDataException();
        var fields = text[(close + 2)..].TrimEnd('\n').Split(' ');
        if (fields.Length < 50 || fields[0].Length != 1 || !"RSDTtIWPK".Contains(fields[0], StringComparison.Ordinal)) throw new InvalidDataException();
        foreach (var field in fields.Skip(1))
        {
            if (field.Length == 0 || field[0] == '+' ||
                !long.TryParse(field, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) &&
                !ulong.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out _)) throw new InvalidDataException();
        }
        if (!ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start == 0)
            throw new InvalidDataException();
        return start;
    }

    private static string ReadText(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[65537];
        var length = 0;
        int count;
        while (length < buffer.Length && (count = stream.Read(buffer, length, buffer.Length - length)) != 0) length += count;
        if (length == buffer.Length) throw new InvalidDataException();
        return new UTF8Encoding(false, true).GetString(buffer, 0, length);
    }
}
