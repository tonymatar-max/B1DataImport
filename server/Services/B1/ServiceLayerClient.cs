using System.Net;
using System.Text;
using System.Text.Json;

namespace B1DataImporter.Api.Services.B1;

public class B1ConnectionInfo
{
    public string BaseUrl { get; set; } = "";
    public string CompanyDB { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public bool IgnoreSslErrors { get; set; } = true;
}

/// <summary>Result of a single write operation.</summary>
public record WriteResult(bool Ok, string? Key, string? Error, int StatusCode);

/// <summary>One operation to include in a $batch request.</summary>
public record BatchOp(int ContentId, string Method, string Path, string? Json);

/// <summary>
/// Service Layer client with session recovery, OData query, single + $batch writes.
/// Each batch op is placed in its OWN changeset so one failing row does not roll back
/// the others (a changeset is a single B1 transaction).
/// </summary>
public class ServiceLayerClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly B1ConnectionInfo _conn;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private string? _sessionId;
    private readonly string _basePath;   // e.g. "/b1s/v1"

    public ServiceLayerClient(B1ConnectionInfo conn)
    {
        _conn = conn;
        var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
        if (conn.IgnoreSslErrors)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        var baseUri = new Uri(conn.BaseUrl.TrimEnd('/') + "/");
        _basePath = baseUri.AbsolutePath.TrimEnd('/');
        _http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromMinutes(10) };
    }

    // ---------------------------------------------------------------- session

    public async Task LoginAsync(CancellationToken ct = default)
    {
        await _loginLock.WaitAsync(ct);
        try { await LoginCoreAsync(ct); }
        finally { _loginLock.Release(); }
    }

    private async Task LoginCoreAsync(CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { _conn.CompanyDB, _conn.UserName, _conn.Password });
        using var resp = await _http.PostAsync("Login", new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var content = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new ServiceLayerException($"Login failed ({(int)resp.StatusCode}): {ExtractError(content)}");

        using var doc = JsonDocument.Parse(content);
        _sessionId = doc.RootElement.TryGetProperty("SessionId", out var s) ? s.GetString() : null;
        if (string.IsNullOrEmpty(_sessionId))
            throw new ServiceLayerException("Login returned no SessionId.");
    }

    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_sessionId)) await LoginAsync(ct);
    }

    /// <summary>Send a request, transparently re-logging in once if the session expired.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        await EnsureSessionAsync(ct);
        var resp = await _http.SendAsync(build(), ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            resp.Dispose();
            await LoginAsync(ct);                 // session timed out — B1 default is 30 min
            resp = await _http.SendAsync(build(), ct);
        }
        return resp;
    }

    // ---------------------------------------------------------------- metadata & query

    public async Task<string> GetMetadataAsync(CancellationToken ct = default)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "$metadata"), ct);
        var content = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new ServiceLayerException($"$metadata failed ({(int)resp.StatusCode}): {ExtractError(content)}");
        return content;
    }

    /// <summary>Run an OData query and return the "value" array elements.</summary>
    public async Task<List<JsonElement>> QueryAsync(string relativeUrl, CancellationToken ct = default)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, relativeUrl), ct);
        var content = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new ServiceLayerException($"Query '{relativeUrl}' failed ({(int)resp.StatusCode}): {ExtractError(content)}");

        using var doc = JsonDocument.Parse(content);
        var list = new List<JsonElement>();
        if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var el in arr.EnumerateArray()) list.Add(el.Clone());
        else
            list.Add(doc.RootElement.Clone());
        return list;
    }

    /// <summary>True when a record with this key already exists (used for upsert).</summary>
    public async Task<bool> ExistsAsync(string entitySet, string keyPredicate, CancellationToken ct = default)
    {
        using var resp = await SendAsync(() =>
            new HttpRequestMessage(HttpMethod.Get, $"{entitySet}({keyPredicate})?$select=*"), ct);
        return resp.IsSuccessStatusCode;
    }

    // ---------------------------------------------------------------- writes

    public async Task<WriteResult> PostAsync(string entitySet, string json, string keyProperty, CancellationToken ct = default)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, entitySet)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") }, ct);

        var content = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return new WriteResult(false, null, ExtractError(content), (int)resp.StatusCode);
        return new WriteResult(true, ReadKey(content, keyProperty), null, (int)resp.StatusCode);
    }

    public async Task<WriteResult> PatchAsync(string entitySet, string keyPredicate, string json, CancellationToken ct = default)
    {
        using var resp = await SendAsync(() =>
            new HttpRequestMessage(HttpMethod.Patch, $"{entitySet}({keyPredicate})")
            { Content = new StringContent(json, Encoding.UTF8, "application/json") }, ct);

        var content = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return new WriteResult(false, null, ExtractError(content), (int)resp.StatusCode);
        return new WriteResult(true, keyPredicate.Trim('\''), null, (int)resp.StatusCode);
    }

    // ---------------------------------------------------------------- $batch

    /// <summary>
    /// Send several operations in ONE HTTP round trip. Each op gets its own changeset,
    /// so each is an independent B1 transaction and failures are isolated per row.
    /// Results come back in the same order as <paramref name="ops"/>.
    /// </summary>
    public async Task<List<WriteResult>> BatchAsync(
        IReadOnlyList<BatchOp> ops, string keyProperty, CancellationToken ct = default)
    {
        if (ops.Count == 0) return new List<WriteResult>();

        // Service Layer stops processing a $batch entirely at the first changeset that fails —
        // it does not return a part for the changesets after it, even though each is its own
        // independent transaction. Recover real per-row isolation by re-submitting whatever
        // wasn't accounted for as a fresh batch, until every op has a result.
        var results = await BatchOnceAsync(ops, keyProperty, ct);
        if (results.Count < ops.Count)
        {
            var remaining = ops.Skip(results.Count).ToList();
            results.AddRange(await BatchAsync(remaining, keyProperty, ct));
        }
        return results;
    }

    private async Task<List<WriteResult>> BatchOnceAsync(
        IReadOnlyList<BatchOp> ops, string keyProperty, CancellationToken ct)
    {
        var batchId = "batch_" + Guid.NewGuid().ToString("N");
        var sb = new StringBuilder();

        foreach (var op in ops)
        {
            var csId = "changeset_" + Guid.NewGuid().ToString("N");
            sb.Append("--").Append(batchId).Append("\r\n");
            sb.Append("Content-Type: multipart/mixed;boundary=").Append(csId).Append("\r\n\r\n");

            sb.Append("--").Append(csId).Append("\r\n");
            sb.Append("Content-Type: application/http\r\n");
            sb.Append("Content-Transfer-Encoding: binary\r\n");
            sb.Append("Content-ID: ").Append(op.ContentId).Append("\r\n\r\n");

            sb.Append(op.Method).Append(' ').Append(_basePath).Append('/').Append(op.Path).Append(" HTTP/1.1\r\n");
            if (op.Json != null)
            {
                sb.Append("Content-Type: application/json\r\n\r\n");
                sb.Append(op.Json).Append("\r\n");
            }
            else sb.Append("\r\n");

            sb.Append("--").Append(csId).Append("--\r\n");
        }
        sb.Append("--").Append(batchId).Append("--\r\n");

        var payload = sb.ToString();
        using var resp = await SendAsync(() =>
        {
            var content = new StringContent(payload, Encoding.UTF8);
            content.Headers.Remove("Content-Type");
            content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/mixed;boundary={batchId}");
            return new HttpRequestMessage(HttpMethod.Post, "$batch") { Content = content };
        }, ct);

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = ExtractError(body);
            return ops.Select(_ => new WriteResult(false, null, err, (int)resp.StatusCode)).ToList();
        }
        return ParseBatchResponse(body, ops.Count, keyProperty);
    }

    /// <summary>
    /// Pull the individual HTTP responses out of a multipart batch reply, in order.
    /// We scan for "HTTP/1.1 &lt;code&gt;" markers and take the JSON body that follows each.
    /// </summary>
    internal static List<WriteResult> ParseBatchResponse(string body, int expected, string keyProperty)
    {
        var results = new List<WriteResult>();
        var marker = "HTTP/1.1 ";
        int idx = 0;

        while (results.Count < expected)
        {
            var h = body.IndexOf(marker, idx, StringComparison.Ordinal);
            if (h < 0) break;

            var lineEnd = body.IndexOf('\r', h);
            if (lineEnd < 0) lineEnd = body.Length;
            var statusLine = body[(h + marker.Length)..lineEnd].Trim();
            int.TryParse(statusLine.Split(' ')[0], out var status);

            // Body of this part = from the blank line after the headers, to the next boundary.
            var bodyStart = body.IndexOf("\r\n\r\n", lineEnd, StringComparison.Ordinal);
            var nextBoundary = body.IndexOf("\r\n--", bodyStart < 0 ? lineEnd : bodyStart + 4, StringComparison.Ordinal);
            var partBody = bodyStart < 0 ? ""
                : body[(bodyStart + 4)..(nextBoundary < 0 ? body.Length : nextBoundary)].Trim();

            if (status is >= 200 and < 300)
                results.Add(new WriteResult(true, ReadKey(partBody, keyProperty), null, status));
            else
                results.Add(new WriteResult(false, null, ExtractError(partBody), status));

            idx = nextBoundary < 0 ? body.Length : nextBoundary + 2;
        }

        // Fewer parts than ops means Service Layer stopped processing the batch after an error;
        // return only what was actually parsed — the caller re-submits the rest as a new batch.
        return results;
    }

    // ---------------------------------------------------------------- helpers

    private static string? ReadKey(string json, string keyProperty)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(keyProperty, out var k))
                return k.ValueKind == JsonValueKind.String ? k.GetString() : k.GetRawText();
        }
        catch { /* not JSON */ }
        return null;
    }

    public static string ExtractError(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "(empty response)";
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg))
            {
                if (msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("value", out var v))
                    return v.GetString() ?? content;
                if (msg.ValueKind == JsonValueKind.String) return msg.GetString() ?? content;
            }
        }
        catch { /* fall through to raw */ }
        var trimmed = content.Trim();
        return trimmed.Length > 400 ? trimmed[..400] : trimmed;
    }

    public void Dispose() { _http.Dispose(); _loginLock.Dispose(); }
}

public class ServiceLayerException : Exception
{
    public ServiceLayerException(string message) : base(message) { }
}
