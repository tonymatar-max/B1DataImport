using B1DataImporter.Api.Models;
using B1DataImporter.Api.Services.B1;

namespace B1DataImporter.Api.Services.Connectors;

/// <summary>
/// One write operation the executor hands to a target session: the built payload plus how it
/// should be applied (POST for create, PATCH for update — with a key predicate for PATCH).
/// <paramref name="Tag"/> lets the executor correlate a result back to its own record without
/// the session knowing anything about RunItems.
/// </summary>
public record TargetWriteOp(object Tag, string Json, string Method, string? KeyPredicate);

/// <summary>
/// A live, authenticated connection to a target system. Everything the executor needs to push
/// mapped records into a system sits behind this interface, so the executor is target-agnostic.
/// The metadata/validation model is currently expressed with the <see cref="TargetEntity"/> shape
/// (a generic Name/Properties/Collections descriptor); it is not B1-specific in structure.
/// </summary>
public interface ITargetSession : IDisposable
{
    /// <summary>Discover the writable objects (entities) and their fields from live metadata.</summary>
    Task<IReadOnlyList<TargetEntity>> GetEntitiesAsync(CancellationToken ct = default);

    /// <summary>Does a record already exist under this key predicate? Used to route upsert to PATCH.</summary>
    Task<bool> ExistsAsync(string entitySet, string keyPredicate, CancellationToken ct = default);

    /// <summary>Pre-flight validation of a payload against the target's metadata.</summary>
    IReadOnlyList<string> Validate(
        Dictionary<string, object?> payload, TargetEntity entity, HashSet<string> explicitlyRequired);

    /// <summary>Apply a batch of writes, returning one result per op in the same order.</summary>
    Task<IReadOnlyList<WriteResult>> WriteAsync(
        string entitySet, string keyProperty, IReadOnlyList<TargetWriteOp> ops,
        bool useBatch, CancellationToken ct = default);

    /// <summary>Format a value as this system's key predicate (quoting rules are system-specific).</summary>
    string FormatKeyPredicate(object value, TargetProperty? prop);

    /// <summary>Lookup resolver bound to this session, for name→code transforms during mapping.</summary>
    ITargetLookup Lookups { get; }
}
