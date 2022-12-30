namespace Hamstix.Haby.Server.Models;

public class SecurityToken
{
    /// <summary>
    /// The security token that should be used to make requests to the API.
    /// </summary>
    public Guid Token { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// The configuration mask that will be used to check grants to get access to the key configuration.
    /// </summary>
    public string ConfigurationMask { get; set; }

    /// <summary>
    /// The security token description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The date and time when the security token was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}
