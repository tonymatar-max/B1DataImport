using System.Data;
using System.Globalization;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Data.SqlClient;
using B1DataImporter.Api.Models;

namespace B1DataImporter.Api.Services;

/// <summary>Reads schema + rows from a source. One implementation per source type.</summary>
public interface ISourceReader
{
    SourceSchema Inspect(SourceHandle handle, int previewRows = 20);
    /// <summary>Stream every row for the import run.</summary>
    IEnumerable<Dictionary<string, object?>> ReadAll(SourceHandle handle);
}

public class ExcelSourceReader : ISourceReader
{
    public SourceSchema Inspect(SourceHandle handle, int previewRows = 20)
    {
        using var wb = new XLWorkbook(handle.FilePath!);
        var ws = string.IsNullOrEmpty(handle.SheetOrTable)
            ? wb.Worksheets.First()
            : wb.Worksheet(handle.SheetOrTable);
        var range = ws.RangeUsed();
        var schema = new SourceSchema { SourceType = "excel", ObjectName = ws.Name };
        if (range == null) return schema;

        var headerRow = range.FirstRow();
        var headers = headerRow.Cells().Select(c => c.GetString().Trim()).ToList();
        schema.Columns = headers.Select(h => new SourceColumn(h, "string")).ToList();

        int taken = 0;
        foreach (var row in range.Rows().Skip(1))
        {
            if (taken++ >= previewRows) break;
            schema.PreviewRows.Add(RowToDict(headers, row));
        }
        schema.TotalRows = range.RowCount() - 1;
        return schema;
    }

    public IEnumerable<Dictionary<string, object?>> ReadAll(SourceHandle handle)
    {
        using var wb = new XLWorkbook(handle.FilePath!);
        var ws = string.IsNullOrEmpty(handle.SheetOrTable)
            ? wb.Worksheets.First()
            : wb.Worksheet(handle.SheetOrTable);
        var range = ws.RangeUsed();
        if (range == null) yield break;
        var headers = range.FirstRow().Cells().Select(c => c.GetString().Trim()).ToList();
        foreach (var row in range.Rows().Skip(1))
            yield return RowToDict(headers, row);
    }

    private static Dictionary<string, object?> RowToDict(List<string> headers, IXLRangeRow row)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Count; i++)
        {
            var cell = row.Cell(i + 1);
            dict[headers[i]] = cell.IsEmpty() ? null : cell.Value.ToString();
        }
        return dict;
    }

    public static List<string> ListSheets(string path)
    {
        using var wb = new XLWorkbook(path);
        return wb.Worksheets.Select(w => w.Name).ToList();
    }
}

public class CsvSourceReader : ISourceReader
{
    public SourceSchema Inspect(SourceHandle handle, int previewRows = 20)
    {
        var schema = new SourceSchema { SourceType = "csv", ObjectName = Path.GetFileName(handle.FilePath!) };
        using var reader = new StreamReader(handle.FilePath!);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { DetectDelimiter = true });
        csv.Read(); csv.ReadHeader();
        var headers = csv.HeaderRecord ?? Array.Empty<string>();
        schema.Columns = headers.Select(h => new SourceColumn(h.Trim(), "string")).ToList();
        int taken = 0;
        while (csv.Read() && taken++ < previewRows)
            schema.PreviewRows.Add(headers.ToDictionary(h => h, h => (object?)csv.GetField(h), StringComparer.OrdinalIgnoreCase));
        return schema;
    }

    public IEnumerable<Dictionary<string, object?>> ReadAll(SourceHandle handle)
    {
        using var reader = new StreamReader(handle.FilePath!);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { DetectDelimiter = true });
        csv.Read(); csv.ReadHeader();
        var headers = csv.HeaderRecord ?? Array.Empty<string>();
        while (csv.Read())
            yield return headers.ToDictionary(h => h, h => (object?)csv.GetField(h), StringComparer.OrdinalIgnoreCase);
    }
}

public class SqlSourceReader : ISourceReader
{
    public SourceSchema Inspect(SourceHandle handle, int previewRows = 20)
    {
        var schema = new SourceSchema { SourceType = "sql", ObjectName = handle.SheetOrTable ?? "query" };
        var sql = handle.Query ?? $"SELECT * FROM [{handle.SheetOrTable}]";
        using var conn = new SqlConnection(handle.ConnectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn);
        using var reader = cmd.ExecuteReader();

        for (int i = 0; i < reader.FieldCount; i++)
            schema.Columns.Add(new SourceColumn(reader.GetName(i), reader.GetFieldType(i).Name));

        int taken = 0;
        while (reader.Read() && taken++ < previewRows)
            schema.PreviewRows.Add(ReadRow(reader));
        return schema;
    }

    public IEnumerable<Dictionary<string, object?>> ReadAll(SourceHandle handle)
    {
        var sql = handle.Query ?? $"SELECT * FROM [{handle.SheetOrTable}]";
        using var conn = new SqlConnection(handle.ConnectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            yield return ReadRow(reader);
    }

    private static Dictionary<string, object?> ReadRow(IDataReader reader)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            var v = reader.GetValue(i);
            dict[reader.GetName(i)] = v == DBNull.Value ? null : v;
        }
        return dict;
    }

    public static List<string> ListTables(string connectionString)
    {
        var tables = new List<string>();
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = new SqlCommand(
            "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES " +
            "WHERE TABLE_TYPE IN ('BASE TABLE','VIEW') ORDER BY TABLE_SCHEMA, TABLE_NAME", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tables.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
        return tables;
    }
}

public class SourceReaderFactory
{
    public ISourceReader Get(string sourceType) => sourceType.ToLowerInvariant() switch
    {
        "excel" => new ExcelSourceReader(),
        "csv" => new CsvSourceReader(),
        "sql" => new SqlSourceReader(),
        _ => throw new NotSupportedException($"Source type '{sourceType}' is not supported."),
    };
}
