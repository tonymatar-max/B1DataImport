using B1DataImporter.Api.Domain;
using B1DataImporter.Api.Models;
using B1DataImporter.Api.Services.B1;   // WriteResult, PayloadValidator

namespace B1DataImporter.Api.Services.Connectors.Rest;

/// <summary>
/// A live session against a manifest-driven REST target. Metadata comes from the manifest (no
/// live $metadata call), validation reuses <see cref="PayloadValidator"/>, and writes go through
/// <see cref="RestApiClient"/>. REST has no cross-record transaction like B1's $batch, so writes
/// are applied one at a time regardless of the scenario's UseBatch flag.
/// </summary>
public class RestManifestSession : ITargetSession
{
    private readonly RestApiClient _client;
    private readonly ConnectorManifest _manifest;
    private readonly PayloadValidator _validator = new();
    private readonly RestLookup _lookups;

    public RestManifestSession(RestApiClient client, ConnectorManifest manifest)
    {
        _client = client;
        _manifest = manifest;
        _lookups = new RestLookup(client);
    }

    public ITargetLookup Lookups => _lookups;

    public Task<IReadOnlyList<TargetEntity>> GetEntitiesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TargetEntity>>(_manifest.ToEntities());

    public Task<bool> ExistsAsync(string entitySet, string keyPredicate, CancellationToken ct = default)
        => _client.ExistsAsync(entitySet, keyPredicate, ct);

    public IReadOnlyList<string> Validate(
        Dictionary<string, object?> payload, TargetEntity entity, HashSet<string> explicitlyRequired)
        => _validator.Validate(payload, entity, explicitlyRequired);

    public async Task<IReadOnlyList<WriteResult>> WriteAsync(
        string entitySet, string keyProperty, IReadOnlyList<TargetWriteOp> ops,
        bool useBatch, CancellationToken ct = default)
    {
        var results = new List<WriteResult>(ops.Count);
        foreach (var op in ops)
            results.Add(op.Method == "PATCH"
                ? await _client.PatchAsync(entitySet, op.KeyPredicate!, op.Json, ct)
                : await _client.PostAsync(entitySet, op.Json, keyProperty, ct));
        return results;
    }

    public string FormatKeyPredicate(object value, TargetProperty? prop)
    {
        var s = value is System.Text.Json.JsonElement je ? je.ToString() : value.ToString() ?? "";
        var isNumeric = prop?.Type is "Edm.Int16" or "Edm.Int32" or "Edm.Int64"
            or "Edm.Double" or "Edm.Decimal" or "Edm.Single";
        return isNumeric ? s : $"'{s.Replace("'", "''")}'";
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Name→value lookup via a filtered top-1 query against the REST API.</summary>
    private sealed class RestLookup : ITargetLookup
    {
        private readonly RestApiClient _client;
        public RestLookup(RestApiClient client) => _client = client;

        public async Task<(bool ok, string? value, string? error)> ResolveAsync(
            B1LookupSpec spec, string? sourceValue, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sourceValue))
                return (true, null, null);

            var filter = $"{spec.MatchField} eq '{sourceValue.Replace("'", "''")}'";
            if (!string.IsNullOrWhiteSpace(spec.ExtraFilter))
                filter = $"({filter}) and ({spec.ExtraFilter})";

            var value = await _client.QueryScalarAsync(spec.Entity, filter, spec.ReturnField, ct);
            if (value is null && spec.FailIfMissing)
                return (false, null,
                    $"No {spec.Entity} where {spec.MatchField} = '{sourceValue}'.");
            return (true, value ?? sourceValue, null);
        }
    }
}
