namespace B1DataImporter.Api.Models;

/// <summary>Connection details for a SAP B1 Service Layer + Company DB.</summary>
public class B1Connection
{
    public string BaseUrl { get; set; } = "";          // e.g. https://sapb1:50000/b1s/v1
    public string CompanyDB { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public bool IgnoreSslErrors { get; set; } = true;   // common for self-signed SL certs
}

/// <summary>One property of a B1 entity, parsed from the $metadata EDMX document.</summary>
public class B1Property
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";             // Edm.String, Edm.Int32, Edm.Double, Edm.DateTime, enum...
    public bool Nullable { get; set; } = true;
    public int? MaxLength { get; set; }
    public bool IsKey { get; set; }
    public bool IsUdf => Name.StartsWith("U_", StringComparison.OrdinalIgnoreCase);
    /// <summary>Allowed values when Type is an enum (e.g. BoYesNoEnum, CardType).</summary>
    public List<string>? EnumMembers { get; set; }
}

/// <summary>A child collection on a B1 entity (e.g. Orders.DocumentLines).</summary>
public class B1NavCollection
{
    public string Name { get; set; } = "";             // navigation property name, e.g. DocumentLines
    public string TargetEntity { get; set; } = "";     // complex/entity type it points to
    public List<B1Property> Properties { get; set; } = new();
}

/// <summary>A B1 entity (object) discovered from $metadata.</summary>
public class B1Entity
{
    public string Name { get; set; } = "";             // EntitySet name used in the URL, e.g. BusinessPartners
    public string EntityType { get; set; } = "";       // fully qualified type name
    public List<B1Property> Properties { get; set; } = new();
    public List<B1NavCollection> Collections { get; set; } = new();
    public bool HasUdfs => Properties.Any(p => p.IsUdf);
}
