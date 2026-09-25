using B1DataImporter.Api.Domain;

namespace B1DataImporter.Api.Services.Connectors;

/// <summary>
/// A registered target system the platform can write to. One implementation per system
/// (SAP B1 today; a manifest-driven REST connector later). Connectors are stateless and
/// resolved from the <see cref="TargetConnectorRegistry"/> by <see cref="ConnectionKind"/>;
/// each call to <see cref="OpenAsync"/> yields a fresh authenticated <see cref="ITargetSession"/>.
/// </summary>
public interface ITargetConnector
{
    /// <summary>The connection kind this connector handles (the registry key).</summary>
    ConnectionKind Kind { get; }

    /// <summary>Human-readable name for UI/logs, e.g. "SAP Business One (Service Layer)".</summary>
    string DisplayName { get; }

    /// <summary>Open and authenticate a session against the given connection definition.</summary>
    Task<ITargetSession> OpenAsync(ConnectionDef connection, CancellationToken ct = default);
}
