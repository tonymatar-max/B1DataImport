using B1DataImporter.Api.Domain;
using B1DataImporter.Api.Services.Connectors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace B1DataImporter.Tests;

/// <summary>Minimal IWebHostEnvironment so ManifestStore can be constructed with a temp content root.</summary>
internal sealed class FakeEnv : IWebHostEnvironment
{
    public FakeEnv(string contentRoot) => ContentRootPath = contentRoot;
    public string ContentRootPath { get; set; }
    public string ApplicationName { get; set; } = "Tests";
    public string EnvironmentName { get; set; } = "Test";
    public string WebRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>A deterministic ITargetLookup for testing mapping without a live system.</summary>
internal sealed class FakeLookup : ITargetLookup
{
    private readonly Dictionary<string, string> _map;
    public FakeLookup(Dictionary<string, string> map) => _map = map;

    public Task<(bool ok, string? value, string? error)> ResolveAsync(
        B1LookupSpec spec, string? sourceValue, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceValue))
            return Task.FromResult<(bool, string?, string?)>((true, null, null));
        if (_map.TryGetValue(sourceValue, out var v))
            return Task.FromResult<(bool, string?, string?)>((true, v, null));
        return Task.FromResult<(bool, string?, string?)>(
            spec.FailIfMissing ? (false, null, $"no match for {sourceValue}") : (true, sourceValue, null));
    }
}
