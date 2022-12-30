namespace Hamstix.Haby.Server.Models;

/// <summary>
/// The organization unit aka category.
/// </summary>
public class OrganizationUnit
{
    /// <summary>
    /// Organization unit id.
    /// </summary>
    public long Id { get; private set; }

    /// <summary>
    /// Organization unit name.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// The child organization units related to the current organization unit.
    /// </summary>
    public ICollection<OrganizationUnit> Children { get; private set; } 
        = new List<OrganizationUnit>();

    /// <summary>
    /// The parent organization unit. If null - then the organization unit is the root.
    /// </summary>
    public OrganizationUnit? Parent { get; private set; }

    /// <summary>
    /// The parent organization unit id. If null - then the organization unit is the root.
    /// </summary>
    public long? ParentId { get; private set; }

    /// <summary>
    /// The list of the configuration units, that is belongs to the current organization unit.
    /// </summary>
    public ICollection<ConfigurationUnit> ConfigurationUnits { get; private set; } 
        = new List<ConfigurationUnit>();

    private OrganizationUnit() { } // EF.

    public OrganizationUnit(string name)
    {
        Name = name;
    }

    public OrganizationUnit(string name, OrganizationUnit parent)
        : this(name)
    {
        Parent = parent;
        ParentId = parent.Id;
        parent.Children.Add(this);
    }
}
