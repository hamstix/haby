namespace Haby.Domain;

/// <summary>An opaque producer-supplied Application version.</summary>
public sealed record ApplicationVersion
{
    private ApplicationVersion(string value) => Value = value;

    public string Value { get; }

    public static ApplicationVersion Create(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < 1 or > 128 || !IsAsciiLetterOrDigit(value[0]))
            throw new ArgumentException("Application version must be 1-128 ASCII characters and start with a letter or digit.", nameof(value));

        foreach (var character in value)
        {
            if (!IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '+' or '-'))
                throw new ArgumentException("Application version must match ^[A-Za-z0-9](?:[A-Za-z0-9._+-]{0,127})$.", nameof(value));
        }

        return new ApplicationVersion(value);
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    public override string ToString() => Value;
}
