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
    private readonly string? _apiKey;

    public AiMappingService(IConfiguration config, ILogger<AiMappingService> log)
    {
        _log = log;
        _apiKey = config["Anthropic:ApiKey"]
                  ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<AiMappingProposal> ProposeAsync(
        SourceSchema source, TargetEntity entity, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "No Anthropic API key configured. Set ANTHROPIC_API_KEY or Anthropic:ApiKey.");

        var client = new AnthropicClient { ApiKey = _apiKey };

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
            Messages = [new() { Role = Role.User, Content = BuildUserPrompt(source, entity) }],
        }, cancellationToken: ct);

        var json = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return Parse(json);
    }

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
