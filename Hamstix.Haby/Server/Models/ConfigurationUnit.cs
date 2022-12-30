using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Models;

/// <summary>
/// The configuration unit.
/// </summary>
public class ConfigurationUnit
{
    /// <summary>
    /// Configuration unit id.
    /// </summary>
    public long Id { get; private set; }

    /// <summary>
    /// Configuration unit name.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Configuration unit version.
    /// </summary>
    public string Version { get; set; }

    /// <summary>
    /// Configuration unit previous version.
    /// </summary>
    public string? PreviousVersion { get; set; }

    /// <summary>
    /// List of configuration unit keys.
    /// </summary>
    public ICollection<ConfigurationKey> Keys { get; private set; } =
        new List<ConfigurationKey>();

    /// <summary>
    /// The configuration unit template.
    /// </summary>
    public JsonArray Template { get; set; } = new JsonArray();

    /// <summary>
    /// The list of all services the configuration unit is configured for.
    /// </summary>
    public ICollection<ConfigurationUnitAtService> Services { get; private set; } =
        new List<ConfigurationUnitAtService>();

    /// <summary>
    /// List of all parameters for the configuration unit.
    /// </summary>
    public ICollection<ConfigurationUnitParameter> Parameters { get; private set; } =
        new List<ConfigurationUnitParameter>();

    /// <summary>
    /// The organization unit, the current configuratio unit belogs to.
    /// If null - the configuration unit not belongs to any organization units.
    /// </summary>
    public OrganizationUnit? OrganizationUnit { get; private set; }

    /// <summary>
    /// The organization unit id, the current configuratio unit belogs to.
    /// If null - the configuration unit not belongs to any organization units.
    /// </summary>
    public long? OrganizationUnitId { get; private set; }

    private ConfigurationUnit() { } // EF.

    public ConfigurationUnit(string name, string version)
    {
        Name = name;
        Version = version;
    }

    /// <summary>
    /// Perform a correct installation of the new version of the configuration unit.
    /// </summary>
    /// <param name="newVersion"></param>
    public void UpdateVersion(string newVersion)
    {
        if (string.IsNullOrEmpty(newVersion))
            throw new ArgumentException($"{nameof(newVersion)} is null or empty.", nameof(newVersion));

        if (PreviousVersion is null)
            PreviousVersion = string.Empty;

        var trimmedNewVersion = newVersion.Trim();
        if (trimmedNewVersion == Version)
            return;

        if (Version is null)
        {
            PreviousVersion = string.Empty;
            Version = newVersion;
        }
        else
        {
            PreviousVersion = Version;
            Version = newVersion;
        }
    }

    public void ChangeOrganizationUnit(OrganizationUnit organizationUnit)
    {
        OrganizationUnit = organizationUnit;
        OrganizationUnitId = organizationUnit.Id;
    }

    public void ResetOrganizationUnit()
    {
        OrganizationUnit?.ConfigurationUnits?.Remove(this);
        OrganizationUnit = null;
        OrganizationUnitId = null;
    }
}
