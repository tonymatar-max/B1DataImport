using System.Collections.Concurrent;
using System.Text.Json;
using B1DataImporter.Api.Domain;

namespace B1DataImporter.Api.Services.B1;

/// <summary>
/// Resolves a human-readable source value into the B1 code it refers to, by querying B1.
/// e.g. "Main Warehouse" -> Warehouses?$filter=WarehouseName eq 'Main Warehouse' -> "01".
/// Results are cached for the lifetime of a run, so a 50k-row file with 6 warehouses
/// costs 6 queries, not 50k.
/// </summary>
public class LookupResolver : Connectors.ITargetLookup
{
    private readonly ServiceLayerClient _client;
    private readonly ConcurrentDictionary<string, string?> _cache = new();

    public int Hits { get; private set; }
    public int Misses { get; private set; }
    public int Queries { get; private set; }

    public LookupResolver(ServiceLayerClient client) => _client = client;

    public async Task<(bool ok, string? value, string? error)> ResolveAsync(
        B1LookupSpec spec, string? sourceValue, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceValue))
            return (true, null, null);

        var cacheKey = $"{spec.Entity}|{spec.MatchField}|{spec.ReturnField}|{spec.ExtraFilter}|{sourceValue}";
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            Hits++;
            if (cached is null && spec.FailIfMissing)
                return (false, null, Missing(spec, sourceValue));
            return (true, cached ?? sourceValue, null);
        }

        Misses++;
        var filter = $"{spec.MatchField} eq '{Escape(sourceValue)}'";
        if (!string.IsNullOrWhiteSpace(spec.ExtraFilter))
            filter = $"({filter}) and ({spec.ExtraFilter})";
        var url = $"{spec.Entity}?$filter={Uri.EscapeDataString(filter)}&$select={spec.ReturnField}&$top=1";

        try
        {
            Queries++;
            var rows = await _client.QueryAsync(url, ct);
            string? resolved = null;
            if (rows.Count > 0 && rows[0].ValueKind == JsonValueKind.Object &&
                rows[0].TryGetProperty(spec.ReturnField, out var v))
                resolved = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();

            _cache[cacheKey] = resolved;
            if (resolved is null)
                return spec.FailIfMissing
                    ? (false, null, Missing(spec, sourceValue))
                    : (true, sourceValue, null);
            return (true, resolved, null);
        }
        catch (Exception ex)
        {
            return (false, null, $"Lookup on {spec.Entity} failed: {ex.Message}");
        }
    }

    private static string Missing(B1LookupSpec spec, string value) =>
        $"No {spec.Entity} found where {spec.MatchField} = '{value}'.";

    /// <summary>OData string literals escape a single quote by doubling it.</summary>
    private static string Escape(string s) => s.Replace("'", "''");
}
