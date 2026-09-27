using System.Globalization;
using System.Text.RegularExpressions;
using B1DataImporter.Api.Domain;
using B1DataImporter.Api.Models;
using B1DataImporter.Api.Services.B1;

namespace B1DataImporter.Api.Services.Mapping;

public record BuildOutcome(
    bool Ok, Dictionary<string, object?>? Payload, List<string> Errors, bool Skipped);

/// <summary>
/// Turns a group of source rows into a B1-shaped payload: applies each field's transform
/// (including live B1 lookups), coerces to the metadata type, and builds child lines.
/// </summary>
public class RecordBuilder
{
    private static readonly Regex TokenRx = new(@"\{([^}]+)\}", RegexOptions.Compiled);

    public async Task<BuildOutcome> BuildAsync(
        MappingSpec spec,
        List<Dictionary<string, object?>> groupRows,
        TargetEntity entity,
        Connectors.ITargetLookup? lookups,
        CancellationToken ct = default)
    {
        var errors = new List<string>();
        var header = groupRows[0];
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in spec.Header)
        {
            var (value, err, skip) = await ResolveAsync(f, header, lookups, ct);
            if (skip) return new BuildOutcome(false, null, errors, true);
            if (err != null) { errors.Add($"{f.TargetField}: {err}"); continue; }

            var prop = entity.Properties.FirstOrDefault(p =>
                p.Name.Equals(f.TargetField, StringComparison.OrdinalIgnoreCase));
            payload[f.TargetField] = Coerce(value, prop);
        }

        foreach (var lineSpec in spec.Lines)
        {
            var coll = entity.Collections.FirstOrDefault(c =>
                c.Name.Equals(lineSpec.TargetCollection, StringComparison.OrdinalIgnoreCase));
            var lines = new List<Dictionary<string, object?>>();

            for (int i = 0; i < groupRows.Count; i++)
            {
                var row = groupRows[i];
                var line = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                var lineHasError = false;

                foreach (var f in lineSpec.Fields)
                {
                    var (value, err, skip) = await ResolveAsync(f, row, lookups, ct);
                    if (skip) { lineHasError = true; break; }
                    if (err != null)
                    {
                        errors.Add($"{lineSpec.TargetCollection}[{i}].{f.TargetField}: {err}");
                        lineHasError = true;
                        continue;
                    }
                    var prop = coll?.Properties.FirstOrDefault(p =>
                        p.Name.Equals(f.TargetField, StringComparison.OrdinalIgnoreCase));
                    line[f.TargetField] = Coerce(value, prop);
                }

                if (lineHasError) continue;
                if (lineSpec.SkipEmptyLines && line.Values.All(v => v is null or "")) continue;
                lines.Add(line);
            }
            payload[lineSpec.TargetCollection] = lines;
        }

        return new BuildOutcome(errors.Count == 0, payload, errors, false);
    }

    /// <summary>Produce one field's raw string value. Returns (value, error, skipRow).</summary>
    private async Task<(string? value, string? error, bool skip)> ResolveAsync(
        FieldSpec f, Dictionary<string, object?> row, Connectors.ITargetLookup? lookups, CancellationToken ct)
    {
        string? raw = f.Transform switch
        {
            TransformKind.Constant     => f.ConstantValue,
            TransformKind.Expression   => EvalExpression(f.Expression, row),
            TransformKind.DateFormat   => FormatDate(f, Get(row, f.SourceColumn)),
            TransformKind.StaticLookup => StaticLookup(f, Get(row, f.SourceColumn)),
            _                          => Get(row, f.SourceColumn),
        };

        if (string.IsNullOrWhiteSpace(raw) && !string.IsNullOrEmpty(f.DefaultValue))
            raw = f.DefaultValue;

        if (f.Transform == TransformKind.B1Lookup && f.B1Lookup != null && lookups != null)
        {
            var (ok, resolved, err) = await lookups.ResolveAsync(f.B1Lookup, raw, ct);
            if (!ok) return (null, err, false);
            raw = resolved;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            if (f.SkipRowIfEmpty) return (null, null, true);
            if (f.Required) return (null, "required but empty", false);
        }
        return (raw, null, false);
    }

    private static string? Get(Dictionary<string, object?> row, string? col)
        => col != null && row.TryGetValue(col, out var v) ? v?.ToString() : null;

    /// <summary>
    /// Token substitution with optional pipe functions:
    /// "{First} {Last}", "{Code|upper}", "{Name|trim}", "{Desc|substr:0:20}", "{X|pad:5:0}".
    /// </summary>
    private static string EvalExpression(string? expr, Dictionary<string, object?> row)
    {
        if (string.IsNullOrEmpty(expr)) return "";
        return TokenRx.Replace(expr, m =>
        {
            var parts = m.Groups[1].Value.Split('|');
            var value = Get(row, parts[0].Trim()) ?? "";
            foreach (var fn in parts.Skip(1))
            {
                var bits = fn.Split(':');
                value = bits[0].Trim().ToLowerInvariant() switch
                {
                    "upper" => value.ToUpperInvariant(),
                    "lower" => value.ToLowerInvariant(),
                    "trim"  => value.Trim(),
                    "substr" => Substr(value, bits),
                    "pad"   => bits.Length > 1 && int.TryParse(bits[1], out var w)
                                ? value.PadLeft(w, bits.Length > 2 && bits[2].Length > 0 ? bits[2][0] : '0')
                                : value,
                    _ => value,
                };
            }
            return value;
        });
    }

    private static string Substr(string value, string[] bits)
    {
        var start = bits.Length > 1 && int.TryParse(bits[1], out var s) ? s : 0;
        if (start >= value.Length) return "";
        var len = bits.Length > 2 && int.TryParse(bits[2], out var l) ? l : value.Length - start;
        return value.Substring(start, Math.Min(len, value.Length - start));
    }

    private static string? StaticLookup(FieldSpec f, string? key)
    {
        if (key is null || f.StaticLookup is null) return key;
        return f.StaticLookup.TryGetValue(key, out var mapped) ? mapped : key;
    }

    private static string? FormatDate(FieldSpec f, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        DateTime dt;
        var ok = !string.IsNullOrEmpty(f.DateFormat)
            ? DateTime.TryParseExact(raw, f.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)
            : DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt);
        return ok ? dt.ToString("yyyy-MM-dd") : raw;
    }

    /// <summary>Convert the string into the JSON type the B1 property expects.</summary>
    private static object? Coerce(string? value, TargetProperty? prop)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (prop is null) return value;

        switch (prop.Type)
        {
            case "Edm.Int32" or "Edm.Int16" or "Edm.Int64":
                return long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var l) ? l : value;
            case "Edm.Double" or "Edm.Decimal" or "Edm.Single":
                return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : value;
            case "Edm.Boolean":
                return value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "y" or "tyes";
            default:
                return value;   // strings, dates (ISO), enum member names
        }
    }
}
