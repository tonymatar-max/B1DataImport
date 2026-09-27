using B1DataImporter.Api.Data;
using B1DataImporter.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace B1DataImporter.Api.Services;

/// <summary>
/// Reads/writes persisted app settings (the <c>Settings</c> key/value table). Used for
/// UI-configurable options such as the AI provider, model and API keys. Secret values are
/// encrypted with <see cref="ISecretProtector"/> before being stored.
/// </summary>
public class SettingsStore
{
    // Setting keys.
    public const string AiProvider = "Ai:Provider";
    public const string OpenRouterModel = "OpenRouter:Model";
    public const string OpenRouterApiKey = "OpenRouter:ApiKey";   // stored encrypted
    public const string AnthropicApiKey = "Anthropic:ApiKey";     // stored encrypted

    private readonly AppDbContext _db;
    private readonly ISecretProtector _secrets;

    public SettingsStore(AppDbContext db, ISecretProtector secrets)
    {
        _db = db;
        _secrets = secrets;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
        => (await _db.Settings.FindAsync(new object?[] { key }, ct))?.Value;

    /// <summary>Get and decrypt a secret setting.</summary>
    public async Task<string?> GetSecretAsync(string key, CancellationToken ct = default)
        => _secrets.Unprotect(await GetAsync(key, ct));

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        var row = await _db.Settings.FindAsync(new object?[] { key }, ct);
        if (row is null) _db.Settings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Encrypt then store a secret setting; a null/blank value is ignored (keeps existing).</summary>
    public async Task SetSecretAsync(string key, string? plain, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plain)) return;
        await SetAsync(key, _secrets.Protect(plain), ct);
    }

    public async Task<bool> HasAsync(string key, CancellationToken ct = default)
        => !string.IsNullOrWhiteSpace(await GetAsync(key, ct));
}
