namespace B1DataImporter.Api.Models;

/// <summary>A column discovered in a source (Excel sheet, SQL table, CSV file).</summary>
public record SourceColumn(string Name, string DataType, bool Nullable = true);

/// <summary>Schema + sample rows returned when a source is inspected.</summary>
public class SourceSchema
{
    public string SourceType { get; set; } = "";      // "excel" | "csv" | "sql"
    public string ObjectName { get; set; } = "";       // sheet name / table name / file name
    public List<SourceColumn> Columns { get; set; } = new();
    public List<Dictionary<string, object?>> PreviewRows { get; set; } = new();
    public int? TotalRows { get; set; }
}

/// <summary>Handle to a stored source dataset the UI can inspect and import from.</summary>
public class SourceHandle
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceType { get; set; } = "";
    public string DisplayName { get; set; } = "";
    // Where to read the full data from at import time:
    public string? FilePath { get; set; }             // for excel/csv
    public string? SheetOrTable { get; set; }
    public string? ConnectionString { get; set; }     // for sql
    public string? Query { get; set; }                // for sql (SELECT ...)
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
