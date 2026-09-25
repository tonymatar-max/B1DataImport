using System.Text.Json;

namespace B1DataImporter.Api.Services.Connectors.Rest;

/// <summary>
/// Loads connector manifests from the <c>connectors/</c> folder next to the app (one *.json per
/// system) and caches them by id. Reloadable so manifests can be added without a rebuild.
/// </summary>
public class ManifestStore
{
    private readonly string _dir;
    private readonly ILogger<ManifestStore> _log;
    private readonly object _gate = new();
    private Dictionary<string, ConnectorManifest> _byId = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public ManifestStore(IWebHostEnvironment env, ILogger<ManifestStore> log)
    {
        _dir = Path.Combine(env.ContentRootPath, "connectors");
        _log = log;
        Reload();
    }

    public void Reload()
    {
        var map = new Dictionary<string, ConnectorManifest>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(_dir))
        {
            foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
            {
                try
                {
                    var m = JsonSerializer.Deserialize<ConnectorManifest>(File.ReadAllText(file), JsonOpts);
                    if (m is null || string.IsNullOrWhiteSpace(m.Id))
                    {
                        _log.LogWarning("Connector manifest {File} has no id; skipped.", file);
                        continue;
                    }
                    map[m.Id] = m;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to load connector manifest {File}", file);
                }
            }
        }
        lock (_gate) _byId = map;
        _log.LogInformation("Loaded {Count} connector manifest(s) from {Dir}", map.Count, _dir);
    }

    public ConnectorManifest Get(string id)
    {
        lock (_gate)
            return _byId.TryGetValue(id, out var m)
                ? m
                : throw new InvalidOperationException($"Connector manifest '{id}' not found in {_dir}.");
    }

    public IReadOnlyCollection<ConnectorManifest> All
    {
        get { lock (_gate) return _byId.Values.ToList(); }
    }
}
