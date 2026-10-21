using System.Text.RegularExpressions;

namespace Haby.Domain;

/// <summary>An installation-wide, stable logical Environment key.</summary>
public sealed record EnvironmentId
{
    private static readonly Regex Pattern = new(@"\A[a-z][a-z0-9]*(?:-[a-z0-9]+)*\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private EnvironmentId(string value) => Value = value;
    public string Value { get; }
    public static EnvironmentId Create(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 64 || !Pattern.IsMatch(value)) throw new ArgumentException("Invalid Environment ID.", nameof(value));
        return new(value);
    }
    public override string ToString() => Value;
}

public sealed record EnvironmentRecordId
{
    private readonly Guid _value;
    private EnvironmentRecordId(Guid value) => _value = value;
    public static EnvironmentRecordId New() => new(Guid.CreateVersion7());
    public static EnvironmentRecordId Parse(string value) => new(UuidV7.Parse(value));
    public override string ToString() => _value.ToString("D");
}

public sealed record ApplicationDeploymentId
{
    private readonly Guid _value;
    private ApplicationDeploymentId(Guid value) => _value = value;
    public static ApplicationDeploymentId New() => new(Guid.CreateVersion7());
    public static ApplicationDeploymentId Parse(string value) => new(UuidV7.Parse(value));
    public override string ToString() => _value.ToString("D");
}

public sealed record DeploymentChangeSetId
{
    private readonly Guid _value;
    private DeploymentChangeSetId(Guid value) => _value = value;
    public static DeploymentChangeSetId New() => new(Guid.CreateVersion7());
    public static DeploymentChangeSetId Parse(string value) => new(UuidV7.Parse(value));
    public override string ToString() => _value.ToString("D");
}

public sealed record DeploymentPlanId
{
    private readonly Guid _value;
    private DeploymentPlanId(Guid value) => _value = value;
    public static DeploymentPlanId New() => new(Guid.CreateVersion7());
    public static DeploymentPlanId Parse(string value) => new(UuidV7.Parse(value));
    public override string ToString() => _value.ToString("D");
}

/// <summary>A positive semantic desired-state generation.</summary>
public sealed record DeploymentGeneration
{
    private DeploymentGeneration(long value) => Value = value;
    public long Value { get; }
    public static DeploymentGeneration Create(long value) => value > 0 ? new(value) :
        throw new ArgumentOutOfRangeException(nameof(value));
}
