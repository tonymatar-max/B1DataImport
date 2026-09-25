using B1DataImporter.Api.Domain;

namespace B1DataImporter.Api.Services.Connectors;

/// <summary>
/// Resolves a <see cref="ITargetConnector"/> by <see cref="ConnectionKind"/>. All connectors are
/// registered in DI and collected here, so adding a new target system is a matter of registering
/// one more <see cref="ITargetConnector"/> — no changes to the executor.
/// </summary>
public class TargetConnectorRegistry
{
    private readonly Dictionary<ConnectionKind, ITargetConnector> _byKind;

    public TargetConnectorRegistry(IEnumerable<ITargetConnector> connectors)
        => _byKind = connectors.ToDictionary(c => c.Kind);

    /// <summary>Is there a target connector for this kind? (SQL/File are sources, not targets.)</summary>
    public bool Has(ConnectionKind kind) => _byKind.ContainsKey(kind);

    public ITargetConnector Get(ConnectionKind kind)
        => _byKind.TryGetValue(kind, out var c)
            ? c
            : throw new InvalidOperationException(
                $"No target connector is registered for connection kind '{kind}'.");

    public IReadOnlyCollection<ITargetConnector> All => _byKind.Values;
}
