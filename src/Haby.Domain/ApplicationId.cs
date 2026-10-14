namespace Haby.Domain;

/// <summary>A portable, installation-scoped Application identity.</summary>
public sealed record ApplicationId
{
    private ApplicationId(string value) => Value = value;

    public string Value { get; }

    public static ApplicationId Create(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < 1 or > 128 || value[0] is < 'a' or > 'z')
            throw new ArgumentException("Application ID must be 1-128 lowercase ASCII characters and start with a letter.", nameof(value));

        var previousWasSeparator = false;
        foreach (var character in value)
        {
            var isSeparator = character is '.' or '-';
            var isLetterOrDigit = character is >= 'a' and <= 'z' or >= '0' and <= '9';
            if ((!isLetterOrDigit && !isSeparator) || (isSeparator && previousWasSeparator))
                throw new ArgumentException("Application ID must match ^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$.", nameof(value));

            previousWasSeparator = isSeparator;
        }

        if (previousWasSeparator)
            throw new ArgumentException("Application ID must end with a letter or digit.", nameof(value));

        return new ApplicationId(value);
    }

    public override string ToString() => Value;
}
