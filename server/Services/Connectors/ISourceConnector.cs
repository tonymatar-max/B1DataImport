using B1DataImporter.Api.Domain;
using B1DataImporter.Api.Models;

namespace B1DataImporter.Api.Services.Connectors;

/// <summary>
/// A system the platform can pull records FROM — the read/source counterpart of
/// <see cref="ITargetConnector"/>. File/SQL sources keep using the existing ISourceReader path;
/// this seam is for connection-backed sources (e.g. a REST/OData API) that need auth and a
/// connection definition. Methods are synchronous to match the executor's streaming read loop;
/// implementations may buffer internally.
/// </summary>
public interface ISourceConnector
{
    ConnectionKind Kind { get; }
    string DisplayName { get; }

    /// <summary>Discover columns + preview rows for an object (entity set) on this connection.</summary>
    SourceSchema Inspect(ConnectionDef conn, string objectName, int previewRows = 20);

    /// <summary>Read all rows of an object; <paramref name="query"/> is an optional filter expression.</summary>
    IEnumerable<Dictionary<string, object?>> ReadAll(ConnectionDef conn, string objectName, string? query);
}

/// <summary>Resolves an <see cref="ISourceConnector"/> by <see cref="ConnectionKind"/>.</summary>
public class SourceConnectorRegistry
{
    private readonly Dictionary<ConnectionKind, ISourceConnector> _byKind;

    public SourceConnectorRegistry(IEnumerable<ISourceConnector> connectors)
        => _byKind = connectors.ToDictionary(c => c.Kind);

    public bool Has(ConnectionKind kind) => _byKind.ContainsKey(kind);

    public ISourceConnector Get(ConnectionKind kind)
        => _byKind.TryGetValue(kind, out var c)
            ? c
            : throw new InvalidOperationException($"No source connector is registered for kind '{kind}'.");
}
