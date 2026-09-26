using B1DataImporter.Api.Domain;
using B1DataImporter.Api.Models;
using B1DataImporter.Api.Services.B1;

namespace B1DataImporter.Api.Services.Connectors.B1;

/// <summary>
/// A live SAP B1 Service Layer session behind <see cref="ITargetSession"/>. It owns the
/// <see cref="ServiceLayerClient"/>, parses metadata, validates, resolves lookups, and applies
/// writes (single or $batch). The batch/single write logic and OData key formatting moved here
/// verbatim from ScenarioExecutor, so behavior is unchanged — only its location is now the
/// connector, where B1-specific knowledge belongs.
/// </summary>
public class B1TargetSession : ITargetSession
{
    private readonly ServiceLayerClient _client;
    private readonly MetadataService _metadata;
    private readonly ConnectionDef _conn;
    private readonly LookupResolver _lookups;
    private readonly PayloadValidator _validator = new();

    public B1TargetSession(ServiceLayerClient client, MetadataService metadata, ConnectionDef conn)
    {
        _client = client;
        _metadata = metadata;
        _conn = conn;
        _lookups = new LookupResolver(_client);
    }

    public ITargetLookup Lookups => _lookups;

    public async Task<IReadOnlyList<TargetEntity>> GetEntitiesAsync(CancellationToken ct = default)
        => _metadata.Parse(await _client.GetMetadataAsync(ct), _conn.BaseUrl + "|" + _conn.CompanyDB);

    public Task<bool> ExistsAsync(string entitySet, string keyPredicate, CancellationToken ct = default)
        => _client.ExistsAsync(entitySet, keyPredicate, ct);

    public IReadOnlyList<string> Validate(
        Dictionary<string, object?> payload, TargetEntity entity, HashSet<string> explicitlyRequired)
        => _validator.Validate(payload, entity, explicitlyRequired);

    public async Task<IReadOnlyList<WriteResult>> WriteAsync(
        string entitySet, string keyProperty, IReadOnlyList<TargetWriteOp> ops,
        bool useBatch, CancellationToken ct = default)
    {
        if (useBatch && ops.Count > 1)
        {
            var batch = ops.Select((p, i) => new BatchOp(
                i + 1, p.Method,
                p.Method == "PATCH" ? $"{entitySet}({p.KeyPredicate})" : entitySet,
                p.Json)).ToList();
            return await _client.BatchAsync(batch, keyProperty, ct);
        }

        var results = new List<WriteResult>(ops.Count);
        foreach (var op in ops)
            results.Add(op.Method == "PATCH"
                ? await _client.PatchAsync(entitySet, op.KeyPredicate!, op.Json, ct)
                : await _client.PostAsync(entitySet, op.Json, keyProperty, ct));
        return results;
    }

    /// <summary>OData key predicate: quoted for strings, raw for numeric key types.</summary>
    public string FormatKeyPredicate(object value, TargetProperty? prop)
    {
        var s = value is System.Text.Json.JsonElement je ? je.ToString() : value.ToString() ?? "";
        var isNumeric = prop?.Type is "Edm.Int16" or "Edm.Int32" or "Edm.Int64"
            or "Edm.Double" or "Edm.Decimal" or "Edm.Single";
        return isNumeric ? s : $"'{s.Replace("'", "''")}'";
    }

    public void Dispose() => _client.Dispose();
}
