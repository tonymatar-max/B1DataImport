using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using B1DataImporter.Api.Domain;
using B1DataImporter.Api.Models;

namespace B1DataImporter.Api.Services.Ai;

public record AiMappingProposal(MappingSpec Spec, List<FieldNote> Notes, string? Summary);
public record FieldNote(string TargetField, string Confidence, string Reason);

/// <summary>
/// Asks Claude to propose a full mapping given the source schema (columns + sample rows)
/// and the target B1 object's fields. Returns a MappingSpec the user then reviews — the
/// model proposes, the human approves.
/// </summary>
public class AiMappingService
{
    private readonly ILogger<AiMappingService> _log;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopes;
    private readonly HttpClient _http;

    public AiMappingService(IConfiguration config, IServiceScopeFactory scopes, ILogger<AiMappingService> log)
    {
        _log = log;
        _config = config;
        _scopes = scopes;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    private const string DefaultModel = "anthropic/claude-3.5-sonnet";

    /// <summary>Effective AI config, resolved fresh each call: DB settings win, then appsettings, then env.</summary>
    private sealed record Resolved(string Provider, string? OpenRouterKey, IReadOnlyList<string> Models, string? AnthropicKey)
    {
        public bool Configured => Provider == "openrouter"
            ? !string.IsNullOrWhiteSpace(OpenRouterKey)
            : !string.IsNullOrWhiteSpace(AnthropicKey);
        public string Info => Provider == "openrouter"
            ? $"openrouter:{Models[0]}" + (Models.Count > 1 ? $" (+{Models.Count - 1} fallback)" : "")
            : "anthropic:claude-opus-5";
    }

    /// <summary>Parse a models setting (newline- or comma-separated) into up to 3 model ids.</summary>
    private static List<string> ParseModels(string? raw)
    {
        var models = (raw ?? "")
            .Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct()
            .Take(3)   // OpenRouter fallback list limit
            .ToList();
        return models.Count > 0 ? models : new List<string> { DefaultModel };
    }

    private async Task<Resolved> ResolveAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<SettingsStore>();

        var orKey = await store.GetSecretAsync(SettingsStore.OpenRouterApiKey, ct)
                    ?? _config["OpenRouter:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        var anthKey = await store.GetSecretAsync(SettingsStore.AnthropicApiKey, ct)
                      ?? _config["Anthropic:ApiKey"] ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var models = ParseModels(
            await store.GetAsync(SettingsStore.OpenRouterModel, ct)
            ?? _config["OpenRouter:Model"] ?? Environment.GetEnvironmentVariable("OPENROUTER_MODEL"));
        var provider = (await store.GetAsync(SettingsStore.AiProvider, ct)
                        ?? _config["Ai:Provider"] ?? Environment.GetEnvironmentVariable("AI_PROVIDER"))
                       ?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(provider) || provider == "auto")
            provider = !string.IsNullOrWhiteSpace(orKey) ? "openrouter" : "anthropic";

        return new Resolved(provider, orKey, models, anthKey);
    }

    /// <summary>Whether AI mapping can run, and the active provider:model — for /api/health and the UI.</summary>
    public async Task<(bool configured, string provider)> GetStatusAsync(CancellationToken ct = default)
    {
        var r = await ResolveAsync(ct);
        return (r.Configured, r.Info);
    }

    public async Task<AiMappingProposal> ProposeAsync(
        SourceSchema source, TargetEntity entity, CancellationToken ct = default)
    {
        var r = await ResolveAsync(ct);
        if (!r.Configured)
            throw new InvalidOperationException(
                "No AI API key configured. Set it in Settings, or via OPENROUTER_API_KEY / ANTHROPIC_API_KEY.");

        var json = r.Provider == "openrouter"
            ? await CallOpenRouterAsync(r, BuildUserPrompt(source, entity), ct)
            : await CallAnthropicAsync(r, BuildUserPrompt(source, entity), ct);
        return Parse(json);
    }

    /// <summary>OpenRouter (OpenAI-compatible chat completions), JSON-object response.</summary>
    private async Task<string> CallOpenRouterAsync(Resolved r, string userPrompt, CancellationToken ct)
    {
        var messages = new object[]
        {
            new { role = "system", content = SystemPrompt },
            new { role = "user", content = userPrompt + "\n\n" + JsonShapeHint },
        };
        // One model → "model"; several → OpenRouter's "models" fallback array (primary first, then
        // the rest if it's down, rate-limited, or refuses).
        // NOTE: we deliberately do NOT send response_format=json_object — many models (esp. free
        // ones) don't support JSON mode and 400 on it, which would defeat the fallback list. The
        // prompt already demands a bare JSON object, and the parser strips code fences.
        object body = r.Models.Count > 1
            ? new { models = r.Models, temperature = 0, max_tokens = 16000, messages }
            : new { model = r.Models[0], temperature = 0, max_tokens = 16000, messages };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {r.OpenRouterKey}");
        // Optional attribution headers OpenRouter recommends.
        req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/tonymatar-max/B1DataImport");
        req.Headers.TryAddWithoutValidation("X-Title", "B1 Data Importer");

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenRouter error ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                      ?? throw new InvalidOperationException("OpenRouter returned an empty message.");
        return StripFences(content);
    }

    /// <summary>Anthropic (native SDK, structured JSON output).</summary>
    private async Task<string> CallAnthropicAsync(Resolved r, string userPrompt, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = r.AnthropicKey };
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = "claude-opus-5",
            MaxTokens = 16000,
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = BuildSchema() } },
            System = new List<TextBlockParam>
            {
                new() { Text = SystemPrompt, CacheControl = new CacheControlEphemeral() },
            },
            Messages = [new() { Role = Role.User, Content = userPrompt }],
        }, cancellationToken: ct);

        return string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
    }

    /// <summary>Strip ```json fences some models wrap their output in, so JsonDocument can parse it.</summary>
    private static string StripFences(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("```")) return s;
        var firstNl = s.IndexOf('\n');
        if (firstNl < 0) return s;
        s = s[(firstNl + 1)..];
        var lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
        return (lastFence >= 0 ? s[..lastFence] : s).Trim();
    }

    private const string JsonShapeHint = """
        Respond with ONLY a JSON object (no prose, no code fences) of exactly this shape:
        {
          "summary": "string",
          "groupBy": "string or null",
          "sourceKeyColumn": "string or null",
          "header": [ {
            "targetField": "string", "sourceColumn": "string or null",
            "transform": "Direct|Constant|Expression|DateFormat|StaticLookup|B1Lookup",
            "constantValue": "string or null", "expression": "string or null",
            "dateFormat": "string or null", "staticLookup": { "from": "to" } or null,
            "b1Lookup": { "entity": "string", "matchField": "string", "returnField": "string" } or null,
            "confidence": "high|medium|low", "reason": "string"
          } ],
          "lines": [ { "targetCollection": "string", "fields": [ /* same field shape as header */ ] } ]
        }
        """;

    private const string SystemPrompt = """
        You map source data (from Excel, CSV or SQL) onto SAP Business One Service Layer objects.

        You are given the source columns with sample values, and the target B1 object's fields
        with their types, whether they are mandatory, their max length, and allowed enum values.

        Produce a mapping. Rules:
        - Match on meaning, not just name. "Customer Name" -> CardName, "Tel" -> Phone1,
          "Delivery Date" -> DocDueDate.
        - Every mandatory field must be mapped, or given a sensible constant. If you cannot
          satisfy one from the source, still emit it with transform "Constant" and an empty
          constantValue, and flag it in notes with confidence "low" so the user fixes it.
        - Use transform "DateFormat" when sample values look like dates, and infer the format
          from the samples (e.g. "31/12/2026" -> "dd/MM/yyyy").
        - Use transform "B1Lookup" when the source holds a human-readable name but B1 expects a
          code. e.g. warehouse name -> Warehouses/WarehouseName/WarehouseCode; item description
          -> Items/ItemName/ItemCode; BP name -> BusinessPartners/CardName/CardCode.
        - Use transform "StaticLookup" for small closed value sets that map to B1 enum members
          (e.g. "Customer"/"Supplier" -> "cCustomer"/"cSupplier"). Enum values must be exact
          members from the allowed list given.
        - Use transform "Constant" for values that are fixed for the whole load (e.g. Series,
          a default warehouse) and "Expression" to combine columns, e.g. "{First} {Last}".
        - If the object has line collections and the source looks like one row per line
          (a repeating document number), set groupBy to that column and map line fields into
          the matching collection. Otherwise leave groupBy null.
        - Do NOT map read-only or system fields (DocEntry, DocNum, CreateDate) unless the
          source clearly supplies them.
        - Only reference source columns that actually exist, and target fields that actually exist.

        Give each mapped field a confidence of "high", "medium" or "low", and a one-line reason.
        Prefer leaving a field unmapped over guessing badly.
        """;

    private static string BuildUserPrompt(SourceSchema source, TargetEntity entity)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Source: {source.ObjectName} ({source.SourceType})");
        sb.AppendLine("Columns with sample values:");
        foreach (var c in source.Columns)
        {
            var samples = source.PreviewRows.Take(3)
                .Select(r => r.TryGetValue(c.Name, out var v) ? v?.ToString() : null)
                .Where(v => !string.IsNullOrWhiteSpace(v)).Take(3);
            sb.AppendLine($"- {c.Name} ({c.DataType}) e.g. {string.Join(" | ", samples)}");
        }

        sb.AppendLine();
        sb.AppendLine($"# Target B1 object: {entity.Name}");
        sb.AppendLine("Fields (name | type | mandatory | maxLen | enum values):");
        foreach (var p in entity.Properties)
        {
            var mandatory = !p.Nullable && !p.IsKey ? "MANDATORY" : "";
            var en = p.EnumMembers is { Count: > 0 } ? string.Join(",", p.EnumMembers.Take(12)) : "";
            sb.AppendLine($"- {p.Name} | {p.Type.Replace("Edm.", "")} | {mandatory} | {p.MaxLength} | {en}");
        }

        foreach (var coll in entity.Collections.Take(3))
        {
            sb.AppendLine();
            sb.AppendLine($"## Line collection: {coll.Name}");
            foreach (var p in coll.Properties.Take(80))
            {
                var mandatory = !p.Nullable ? "MANDATORY" : "";
                sb.AppendLine($"- {p.Name} | {p.Type.Replace("Edm.", "")} | {mandatory} | {p.MaxLength}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Propose the mapping.");
        return sb.ToString();
    }

    private static Dictionary<string, JsonElement> BuildSchema()
    {
        // Reused for header fields and line fields.
        var field = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["targetField"] = new { type = "string" },
                ["sourceColumn"] = new { type = new[] { "string", "null" } },
                ["transform"] = new { type = "string", @enum = new[]
                    { "Direct", "Constant", "Expression", "DateFormat", "StaticLookup", "B1Lookup" } },
                ["constantValue"] = new { type = new[] { "string", "null" } },
                ["expression"] = new { type = new[] { "string", "null" } },
                ["dateFormat"] = new { type = new[] { "string", "null" } },
                ["staticLookup"] = new { type = new[] { "object", "null" }, additionalProperties = new { type = "string" } },
                ["b1Lookup"] = new
                {
                    type = new[] { "object", "null" },
                    properties = new Dictionary<string, object>
                    {
                        ["entity"] = new { type = "string" },
                        ["matchField"] = new { type = "string" },
                        ["returnField"] = new { type = "string" },
                    },
                    required = new[] { "entity", "matchField", "returnField" },
                    additionalProperties = false,
                },
                ["confidence"] = new { type = "string", @enum = new[] { "high", "medium", "low" } },
                ["reason"] = new { type = "string" },
            },
            required = new[] { "targetField", "transform", "confidence", "reason" },
            additionalProperties = false,
        };

        var schema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["groupBy"] = new { type = new[] { "string", "null" } },
                ["sourceKeyColumn"] = new { type = new[] { "string", "null" } },
                ["summary"] = new { type = "string" },
                ["header"] = new { type = "array", items = field },
                ["lines"] = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["targetCollection"] = new { type = "string" },
                            ["fields"] = new { type = "array", items = field },
                        },
                        required = new[] { "targetCollection", "fields" },
                        additionalProperties = false,
                    },
                },
            },
            required = new[] { "header", "lines", "summary" },
            additionalProperties = false,
        };

        return new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["properties"] = JsonSerializer.SerializeToElement(schema.properties),
            ["required"] = JsonSerializer.SerializeToElement(schema.required),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        };
    }

    private AiMappingProposal Parse(string json)
    {
        var notes = new List<FieldNote>();
        var spec = new MappingSpec();
        string? summary = null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("summary", out var s)) summary = s.GetString();
            if (root.TryGetProperty("groupBy", out var g) && g.ValueKind == JsonValueKind.String)
                spec.GroupBy = g.GetString();
            if (root.TryGetProperty("sourceKeyColumn", out var sk) && sk.ValueKind == JsonValueKind.String)
                spec.SourceKeyColumn = sk.GetString();

            if (root.TryGetProperty("header", out var header))
                foreach (var f in header.EnumerateArray())
                    spec.Header.Add(ReadField(f, notes));

            if (root.TryGetProperty("lines", out var lines))
                foreach (var l in lines.EnumerateArray())
                {
                    var ls = new LineSpec
                    {
                        TargetCollection = l.GetProperty("targetCollection").GetString() ?? "",
                    };
                    if (l.TryGetProperty("fields", out var lf))
                        foreach (var f in lf.EnumerateArray()) ls.Fields.Add(ReadField(f, notes));
                    spec.Lines.Add(ls);
                }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not parse the AI mapping proposal");
            throw new InvalidOperationException("The AI returned a mapping that could not be parsed.");
        }

        return new AiMappingProposal(spec, notes, summary);
    }

    private static FieldSpec ReadField(JsonElement f, List<FieldNote> notes)
    {
        string? Str(string name) =>
            f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var spec = new FieldSpec
        {
            TargetField = Str("targetField") ?? "",
            SourceColumn = Str("sourceColumn"),
            ConstantValue = Str("constantValue"),
            Expression = Str("expression"),
            DateFormat = Str("dateFormat"),
            Transform = Enum.TryParse<TransformKind>(Str("transform"), true, out var t) ? t : TransformKind.Direct,
        };

        if (f.TryGetProperty("staticLookup", out var sl) && sl.ValueKind == JsonValueKind.Object)
            spec.StaticLookup = sl.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");

        if (f.TryGetProperty("b1Lookup", out var bl) && bl.ValueKind == JsonValueKind.Object)
            spec.B1Lookup = new B1LookupSpec
            {
                Entity = bl.GetProperty("entity").GetString() ?? "",
                MatchField = bl.GetProperty("matchField").GetString() ?? "",
                ReturnField = bl.GetProperty("returnField").GetString() ?? "",
            };

        notes.Add(new FieldNote(spec.TargetField, Str("confidence") ?? "medium", Str("reason") ?? ""));
        return spec;
    }
}
