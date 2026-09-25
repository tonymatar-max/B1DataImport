using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using B1DataImporter.Api.Services.B1;   // WriteResult

namespace B1DataImporter.Api.Services.Connectors.Rest;

/// <summary>
/// A thin OData-v4-style HTTP client for a manifest-driven REST target. Applies the manifest's
/// auth scheme, and exposes the read/write primitives the session needs. URL conventions:
/// list <c>{base}/{Entity}?$filter=…</c>, item <c>{base}/{Entity}({key})</c>, create POST,
/// update PATCH. Errors are surfaced verbatim so the run log shows the real API message.
/// </summary>
public class RestApiClient : IDisposable
{
    private readonly HttpClient _http;

    public RestApiClient(string baseUrl, ManifestAuth auth, string? userName, string? secret, bool ignoreSsl)
    {
        var handler = new HttpClientHandler();
        if (ignoreSsl)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        switch ((auth.Type ?? "none").ToLowerInvariant())
        {
            case "basic":
                var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{secret}"));
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", raw);
                break;
            case "bearer":
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret ?? "");
                break;
            case "apikey":
                _http.DefaultRequestHeaders.Add(
                    string.IsNullOrWhiteSpace(auth.ApiKeyHeader) ? "X-API-Key" : auth.ApiKeyHeader,
                    secret ?? "");
                break;
        }
    }

    /// <summary>GET the first matching record's <paramref name="select"/> field, or null if none.</summary>
    public async Task<string?> QueryScalarAsync(string entity, string filter, string select, CancellationToken ct)
    {
        var url = $"{entity}?$filter={Uri.EscapeDataString(filter)}&$select={Uri.EscapeDataString(select)}&$top=1";
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        // OData wraps rows in "value"; some APIs return a bare array.
        var arr = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var v) ? v : root;
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return null;
        return arr[0].TryGetProperty(select, out var cell) ? cell.ToString() : null;
    }

    public async Task<bool> ExistsAsync(string entity, string keyPredicate, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"{entity}({keyPredicate})", ct);
        return resp.StatusCode != HttpStatusCode.NotFound && resp.IsSuccessStatusCode;
    }

    public async Task<WriteResult> PostAsync(string entity, string json, string keyField, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, entity)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return new WriteResult(false, null, Error(resp.StatusCode, body), (int)resp.StatusCode);

        string? key = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(keyField, out var k))
                key = k.ToString();
        }
        catch { /* empty/non-JSON body is fine */ }
        return new WriteResult(true, key, null, (int)resp.StatusCode);
    }

    public async Task<WriteResult> PatchAsync(string entity, string keyPredicate, string json, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"{entity}({keyPredicate})")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode)
            return new WriteResult(true, keyPredicate.Trim('\''), null, (int)resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return new WriteResult(false, null, Error(resp.StatusCode, body), (int)resp.StatusCode);
    }

    private static string Error(HttpStatusCode code, string body)
    {
        body = body.Trim();
        if (body.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                // OData error shape: { "error": { "message": "..." | { "value": "..." } } }
                if (doc.RootElement.TryGetProperty("error", out var err))
                {
                    if (err.TryGetProperty("message", out var msg))
                        return msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("value", out var mv)
                            ? mv.ToString() : msg.ToString();
                }
            }
            catch { /* fall through to raw body */ }
        }
        return string.IsNullOrWhiteSpace(body) ? $"HTTP {(int)code} {code}" : body;
    }

    public void Dispose() => _http.Dispose();
}
