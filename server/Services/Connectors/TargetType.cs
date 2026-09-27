using B1DataImporter.Api.Models;

namespace B1DataImporter.Api.Services.Connectors;

/// <summary>
/// Neutral, system-independent field types the platform understands. Connectors describe their
/// fields with these; the mapping engine's coercion/validation currently keys off the OData
/// "Edm.*" type tags carried on <see cref="TargetProperty.Type"/>, so <see cref="ToEdm"/> bridges the
/// neutral vocabulary onto those existing tags without changing RecordBuilder/PayloadValidator.
/// This keeps B1's own Edm.* metadata working while letting non-B1 connectors speak a clean type
/// language in their manifests.
/// </summary>
public enum TargetType
{
    String,
    Int,
    Long,
    Decimal,
    Double,
    Boolean,
    DateTime,
    Enum,
}

public static class TargetTypes
{
    /// <summary>Map a neutral type to the Edm.* tag the coercion/validation layer understands.</summary>
    public static string ToEdm(TargetType t) => t switch
    {
        TargetType.String   => "Edm.String",
        TargetType.Int      => "Edm.Int32",
        TargetType.Long     => "Edm.Int64",
        TargetType.Decimal  => "Edm.Decimal",
        TargetType.Double   => "Edm.Double",
        TargetType.Boolean  => "Edm.Boolean",
        TargetType.DateTime => "Edm.DateTimeOffset",
        TargetType.Enum     => "Edm.String",
        _                   => "Edm.String",
    };

    /// <summary>Parse a manifest type string ("string", "int", "decimal"…) into a neutral type.</summary>
    public static TargetType Parse(string? raw) => (raw ?? "string").Trim().ToLowerInvariant() switch
    {
        "string" or "text" or "str"          => TargetType.String,
        "int" or "int32" or "integer"        => TargetType.Int,
        "long" or "int64" or "bigint"        => TargetType.Long,
        "decimal" or "money" or "numeric"    => TargetType.Decimal,
        "double" or "float" or "real"        => TargetType.Double,
        "bool" or "boolean" or "bit"         => TargetType.Boolean,
        "datetime" or "date" or "timestamp"  => TargetType.DateTime,
        "enum"                               => TargetType.Enum,
        _                                    => TargetType.String,
    };
}
