namespace B1DataImporter.Api.Models;

// Generic, system-neutral description of a target object and its fields. Populated by any
// connector: SAP B1 parses it from the Service Layer $metadata; the REST connector projects it
// from a manifest. The mapping engine (RecordBuilder/PayloadValidator) consumes only these types.
// Field types are carried as OData "Edm.*" tags today; see TargetType/TargetTypes for the neutral
// vocabulary connectors use to produce them.

/// <summary>One field of a target object.</summary>
public class TargetProperty
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";             // Edm.String, Edm.Int32, Edm.Double, Edm.DateTime, enum...
    public bool Nullable { get; set; } = true;
    public int? MaxLength { get; set; }
    public bool IsKey { get; set; }
    /// <summary>SAP B1 user-defined field convention (U_*); harmless for other systems.</summary>
    public bool IsUdf => Name.StartsWith("U_", StringComparison.OrdinalIgnoreCase);
    /// <summary>Allowed values when the field is an enum.</summary>
    public List<string>? EnumMembers { get; set; }
}

/// <summary>A child/line collection on a target object (e.g. Orders.DocumentLines).</summary>
public class TargetCollection
{
    public string Name { get; set; } = "";             // navigation/collection property name
    public string TargetEntity { get; set; } = "";     // the type it points to
    public List<TargetProperty> Properties { get; set; } = new();
}

/// <summary>A writable target object (entity) and its fields, discovered from a connector.</summary>
public class TargetEntity
{
    public string Name { get; set; } = "";             // set/collection name used in the URL
    public string EntityType { get; set; } = "";       // fully qualified type name
    public List<TargetProperty> Properties { get; set; } = new();
    public List<TargetCollection> Collections { get; set; } = new();
    public bool HasUdfs => Properties.Any(p => p.IsUdf);
}
