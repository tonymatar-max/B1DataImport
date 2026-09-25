namespace B1DataImporter.Api.Domain;

public enum TransformKind
{
    Direct,        // copy the source column
    Constant,      // fixed value
    Expression,    // {Col} tokens + functions: upper/lower/trim/substr/concat
    DateFormat,    // parse with a format, emit ISO
    StaticLookup,  // value -> value dictionary defined here
    B1Lookup,      // resolve against a live B1 entity (e.g. "Main Warehouse" -> WhsCode 01)
}

/// <summary>Resolve a source value into a B1 code by querying B1 itself.</summary>
public class B1LookupSpec
{
    /// <summary>Entity to search, e.g. Warehouses, Items, BusinessPartners.</summary>
    public string Entity { get; set; } = "";
    /// <summary>Property to match the source value against, e.g. WarehouseName.</summary>
    public string MatchField { get; set; } = "";
    /// <summary>Property to return, e.g. WarehouseCode.</summary>
    public string ReturnField { get; set; } = "";
    /// <summary>Fail the row when no match is found (vs. passing the raw value through).</summary>
    public bool FailIfMissing { get; set; } = true;
    /// <summary>Extra OData filter ANDed onto the match, e.g. "Frozen eq 'tNO'".</summary>
    public string? ExtraFilter { get; set; }
}

/// <summary>One target field and how to produce its value.</summary>
public class FieldSpec
{
    public string TargetField { get; set; } = "";
    public string? SourceColumn { get; set; }
    public TransformKind Transform { get; set; } = TransformKind.Direct;

    public string? ConstantValue { get; set; }
    public string? Expression { get; set; }
    public string? DateFormat { get; set; }
    public Dictionary<string, string>? StaticLookup { get; set; }
    public B1LookupSpec? B1Lookup { get; set; }

    public string? DefaultValue { get; set; }
    /// <summary>Treat as mandatory even if B1 metadata says nullable (business rule).</summary>
    public bool Required { get; set; }
    /// <summary>Skip the whole record when this field is empty.</summary>
    public bool SkipRowIfEmpty { get; set; }
}

/// <summary>Mapping for a child collection, e.g. Orders.DocumentLines.</summary>
public class LineSpec
{
    public string TargetCollection { get; set; } = "";
    public List<FieldSpec> Fields { get; set; } = new();
    /// <summary>Skip a line when every mapped field is empty.</summary>
    public bool SkipEmptyLines { get; set; } = true;
}

/// <summary>The complete mapping stored on a Scenario (as MappingJson).</summary>
public class MappingSpec
{
    public List<FieldSpec> Header { get; set; } = new();
    public List<LineSpec> Lines { get; set; } = new();

    /// <summary>
    /// Source column that groups rows into one document (header + N lines).
    /// Null = one B1 record per source row (master data).
    /// </summary>
    public string? GroupBy { get; set; }

    /// <summary>Source column shown in the run log so a human can identify the row.</summary>
    public string? SourceKeyColumn { get; set; }
}
