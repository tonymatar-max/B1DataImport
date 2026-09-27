using B1DataImporter.Api.Models;

namespace B1DataImporter.Api.Services.Connectors.Rest;

/// <summary>
/// A declarative description of a REST/OData-style target system: how to authenticate, and what
/// entities and fields it exposes. This is the heart of the "codeless connector" idea — adding a
/// new system is a matter of dropping one of these JSON files in the <c>connectors/</c> folder,
/// no code required. URL conventions follow OData v4 (<c>{base}/{Entity}</c>, key predicate in
/// parentheses, <c>$filter</c>/<c>$select</c>/<c>$top</c>), which many REST APIs also accept.
/// </summary>
public class ConnectorManifest
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public ManifestAuth Auth { get; set; } = new();
    public List<ManifestEntity> Entities { get; set; } = new();

    public ManifestEntity? FindEntity(string name)
        => Entities.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Project the manifest's entities into the generic <see cref="TargetEntity"/> metadata shape.</summary>
    public List<TargetEntity> ToEntities() => Entities.Select(e => e.ToTargetEntity()).ToList();
}

/// <summary>How to authenticate to the system. The secret/username come from the connection, not here.</summary>
public class ManifestAuth
{
    /// <summary>none | basic | bearer | apikey</summary>
    public string Type { get; set; } = "none";
    /// <summary>For apikey: the header to place the key in (default "X-API-Key").</summary>
    public string ApiKeyHeader { get; set; } = "X-API-Key";
}

public class ManifestEntity
{
    public string Name { get; set; } = "";
    /// <summary>The key field used for existence checks and PATCH predicates.</summary>
    public string KeyField { get; set; } = "";
    public List<ManifestField> Fields { get; set; } = new();
    public List<ManifestCollection> Collections { get; set; } = new();

    public TargetEntity ToTargetEntity() => new()
    {
        Name = Name,
        EntityType = Name,
        Properties = Fields.Select(f => f.ToProperty(KeyField)).ToList(),
        Collections = Collections.Select(c => c.ToNav()).ToList(),
    };
}

public class ManifestField
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";
    public bool Nullable { get; set; } = true;
    public int? MaxLength { get; set; }
    public bool IsKey { get; set; }
    public List<string>? EnumMembers { get; set; }

    public TargetProperty ToProperty(string entityKeyField) => new()
    {
        Name = Name,
        Type = TargetTypes.ToEdm(TargetTypes.Parse(Type)),
        Nullable = Nullable,
        MaxLength = MaxLength,
        IsKey = IsKey || Name.Equals(entityKeyField, StringComparison.OrdinalIgnoreCase),
        EnumMembers = EnumMembers,
    };
}

public class ManifestCollection
{
    public string Name { get; set; } = "";
    public string TargetEntity { get; set; } = "";
    public List<ManifestField> Fields { get; set; } = new();

    public TargetCollection ToNav() => new()
    {
        Name = Name,
        TargetEntity = TargetEntity,
        Properties = Fields.Select(f => f.ToProperty("")).ToList(),
    };
}
