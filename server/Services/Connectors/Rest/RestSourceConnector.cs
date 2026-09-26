using B1DataImporter.Api.Domain;
using B1DataImporter.Api.Models;
using B1DataImporter.Api.Services;   // ISecretProtector

namespace B1DataImporter.Api.Services.Connectors.Rest;

/// <summary>
/// Pulls records FROM a manifest-driven REST/OData API, so a scenario can read from an API and
/// write to B1 (or any other target) — the symmetric counterpart of <see cref="RestManifestConnector"/>.
/// Columns come from the connection's manifest entity; rows are fetched (with paging) via
/// <see cref="RestApiClient"/>. Reads are capped to keep a runaway pull bounded.
/// </summary>
public class RestSourceConnector : ISourceConnector
{
    private const int ReadCap = 100_000;
    private const int PreviewDefault = 20;

    private readonly ManifestStore _manifests;
    private readonly ISecretProtector _secrets;

    public RestSourceConnector(ManifestStore manifests, ISecretProtector secrets)
    {
        _manifests = manifests;
        _secrets = secrets;
    }

    public ConnectionKind Kind => ConnectionKind.Rest;
    public string DisplayName => "REST / OData (manifest-driven)";

    public SourceSchema Inspect(ConnectionDef conn, string objectName, int previewRows = PreviewDefault)
    {
        var manifest = _manifests.Get(Require(conn.ConnectorManifestId, "ConnectorManifestId"));
        var entity = manifest.FindEntity(objectName)
            ?? throw new InvalidOperationException($"Entity '{objectName}' is not in manifest '{manifest.Id}'.");

        using var client = NewClient(conn, manifest);
        var rows = client.QueryRowsAsync(objectName, null, previewRows, CancellationToken.None)
            .GetAwaiter().GetResult();

        // Columns come from the manifest so the schema is stable even if a preview row omits a field.
        var columns = entity.Fields
            .Select(f => new SourceColumn(f.Name, f.Type, f.Nullable))
            .ToList();

        return new SourceSchema
        {
            SourceType = "rest",
            ObjectName = objectName,
            Columns = columns,
            PreviewRows = rows,
        };
    }

    public IEnumerable<Dictionary<string, object?>> ReadAll(ConnectionDef conn, string objectName, string? query)
    {
        var manifest = _manifests.Get(Require(conn.ConnectorManifestId, "ConnectorManifestId"));
        using var client = NewClient(conn, manifest);
        // Buffered read (matches the synchronous ISourceConnector contract); paging handled inside.
        return client.QueryRowsAsync(objectName, query, ReadCap, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private RestApiClient NewClient(ConnectionDef conn, ConnectorManifest manifest) => new(
        Require(conn.BaseUrl, "BaseUrl"),
        manifest.Auth,
        conn.UserName,
        _secrets.Unprotect(conn.SecretProtected),
        conn.IgnoreSslErrors);

    private static string Require(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"This REST connection has no {name}.")
            : value;
}
