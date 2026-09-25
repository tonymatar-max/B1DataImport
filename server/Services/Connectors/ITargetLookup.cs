using B1DataImporter.Api.Domain;

namespace B1DataImporter.Api.Services.Connectors;

/// <summary>
/// Resolves a source value (e.g. a business-partner name) to a target code by querying the
/// live target system. Implemented per connector; the mapping engine depends only on this
/// interface so a B1Lookup transform works against any system that can answer a filtered query.
/// </summary>
public interface ITargetLookup
{
    Task<(bool ok, string? value, string? error)> ResolveAsync(
        B1LookupSpec spec, string? sourceValue, CancellationToken ct = default);
}
