using System.Text.Json;
using B1DataImporter.Api.Data;
using B1DataImporter.Api.Domain;
using B1DataImporter.Api.Models;
using B1DataImporter.Api.Services;
using B1DataImporter.Api.Services.B1;
using B1DataImporter.Api.Services.Connectors;
using B1DataImporter.Api.Services.Mapping;
using Microsoft.EntityFrameworkCore;

namespace B1DataImporter.Api.Services.Jobs;

/// <summary>
/// Executes one Run: pulls source rows, builds + validates payloads, resolves lookups,
/// writes to the target (batched) via <see cref="ITargetSession"/>, and records a per-row
/// result so failures can be re-run. Progress is flushed to the DB after every batch so the
/// UI can follow along. The executor is target-agnostic: it obtains a session from the
/// <see cref="TargetConnectorRegistry"/> and never references a specific system.
/// </summary>
public class ScenarioExecutor
{
    private readonly IServiceScopeFactory _scopes;
    private readonly SourceReaderFactory _readers;
    private readonly TargetConnectorRegistry _connectors;
    private readonly ISecretProtector _secrets;
    private readonly RecordBuilder _builder = new();
    private readonly ILogger<ScenarioExecutor> _log;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // MappingJson is stored as the browser wrote it (camelCase); MappingSpec's C# properties are
    // PascalCase, so deserializing it must be case-insensitive or every field silently comes back
    // at its default (Header/Lines empty) and the built payload is always {}.
    private static readonly JsonSerializerOptions MappingReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public ScenarioExecutor(IServiceScopeFactory scopes, SourceReaderFactory readers,
        TargetConnectorRegistry connectors, ISecretProtector secrets, ILogger<ScenarioExecutor> log)
    {
        _scopes = scopes; _readers = readers; _connectors = connectors; _secrets = secrets; _log = log;
    }

    public async Task ExecuteAsync(string runId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return;

        var scenario = await db.Scenarios.FirstOrDefaultAsync(s => s.Id == run.ScenarioId, ct);
        if (scenario is null) { await FailAsync(db, run, "Scenario no longer exists.", ct); return; }

        run.Status = RunStatus.Running;
        run.StartedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        ITargetSession? target = null;
        try
        {
            var targetConn = await db.Connections.FirstOrDefaultAsync(c => c.Id == scenario.B1ConnectionId, ct)
                ?? throw new InvalidOperationException("Target connection not found.");
            var srcConn = await db.Connections.FirstOrDefaultAsync(c => c.Id == scenario.SourceConnectionId, ct);

            target = await _connectors.Get(targetConn.Kind).OpenAsync(targetConn, ct);

            var entities = await target.GetEntitiesAsync(ct);
            var entity = entities.FirstOrDefault(e =>
                e.Name.Equals(scenario.TargetEntity, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Target object '{scenario.TargetEntity}' not found in this company.");

            var spec = JsonSerializer.Deserialize<MappingSpec>(scenario.MappingJson, MappingReadOpts) ?? new MappingSpec();
            var lookups = target.Lookups;
            var required = spec.Header.Where(f => f.Required).Select(f => f.TargetField)
                .Concat(spec.Lines.SelectMany(l => l.Fields).Where(f => f.Required).Select(f => f.TargetField))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Only re-process the rows that failed last time, when this is a retry — or, if
            // RetryRowNumbers was given, just those specific rows (single- or multi-row retry).
            HashSet<int>? onlyRows = null;
            if (!string.IsNullOrEmpty(run.RetryOfRunId))
            {
                if (!string.IsNullOrEmpty(run.RetryRowNumbers))
                    onlyRows = run.RetryRowNumbers.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(int.Parse).ToHashSet();
                else
                    onlyRows = (await db.RunItems
                        .Where(i => i.RunId == run.RetryOfRunId && i.Status == ItemStatus.Failed)
                        .Select(i => i.RowNumber).ToListAsync(ct)).ToHashSet();
            }

            var handle = BuildHandle(scenario, srcConn, _secrets);
            var groups = GroupRows(_readers.Get(scenario.SourceKind).ReadAll(handle), spec.GroupBy);

            var keyProp = entity.Properties.FirstOrDefault(p => p.IsKey)?.Name ?? entity.Properties[0].Name;
            var pending = new List<(RunItem item, string json, string method, string? keyPredicate)>();
            string? maxWatermark = scenario.WatermarkValue;
            int rowNumber = 0;

            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();
                rowNumber++;
                if (onlyRows != null && !onlyRows.Contains(rowNumber)) continue;

                var item = new RunItem
                {
                    RunId = run.Id,
                    RowNumber = rowNumber,
                    SourceKey = spec.SourceKeyColumn != null && group[0].TryGetValue(spec.SourceKeyColumn, out var sk)
                        ? sk?.ToString() : null,
                };

                // Track the highest watermark value seen, to advance incremental sync.
                if (scenario.WatermarkColumn != null &&
                    group[0].TryGetValue(scenario.WatermarkColumn, out var wv) && wv != null)
                {
                    var s = wv.ToString();
                    if (s != null && (maxWatermark == null ||
                        string.Compare(s, maxWatermark, StringComparison.Ordinal) > 0))
                        maxWatermark = s;
                }

                try
                {
                    var built = await _builder.BuildAsync(spec, group, entity, lookups, ct);
                    if (built.Skipped)
                    {
                        item.Status = ItemStatus.Skipped;
                        run.Skipped++;
                        db.RunItems.Add(item);
                        continue;
                    }
                    if (!built.Ok)
                    {
                        item.Status = ItemStatus.Failed;
                        item.ErrorStage = "lookup";
                        item.Error = string.Join("; ", built.Errors);
                        run.Failed++;
                        db.RunItems.Add(item);
                        if (!scenario.ContinueOnError) break;
                        continue;
                    }

                    var validationErrors = target.Validate(built.Payload!, entity, required);
                    if (validationErrors.Count > 0)
                    {
                        item.Status = ItemStatus.Failed;
                        item.ErrorStage = "validation";
                        item.Error = string.Join("; ", validationErrors);
                        item.PayloadJson = JsonSerializer.Serialize(built.Payload, JsonOpts);
                        run.Failed++;
                        db.RunItems.Add(item);
                        if (!scenario.ContinueOnError) break;
                        continue;
                    }

                    // Upsert/UpdateOnly: check whether a record already exists under the match
                    // field(s) and route this row to PATCH instead of POST accordingly.
                    var method = "POST";
                    string? keyPredicate = null;
                    if (scenario.WriteMode != WriteMode.Create)
                    {
                        var matchField = string.IsNullOrWhiteSpace(scenario.KeyFields)
                            ? keyProp : scenario.KeyFields.Split(',')[0].Trim();
                        if (built.Payload!.TryGetValue(matchField, out var kv) && kv != null)
                        {
                            var matchProp = entity.Properties.FirstOrDefault(p =>
                                p.Name.Equals(matchField, StringComparison.OrdinalIgnoreCase));
                            keyPredicate = target.FormatKeyPredicate(kv, matchProp);
                            var exists = await target.ExistsAsync(entity.Name, keyPredicate, ct);
                            if (exists)
                            {
                                method = "PATCH";
                                // The entity's own primary key can't also appear in a PATCH body
                                // (B1 rejects it with a vague "Internal error (-2038)") — it's
                                // already in the URL predicate, so drop it from the payload.
                                if (matchProp?.IsKey == true)
                                    built.Payload!.Remove(matchField);
                            }
                            else if (scenario.WriteMode == WriteMode.UpdateOnly)
                            {
                                item.Status = ItemStatus.Skipped;
                                item.ErrorStage = "upsert-check";
                                item.Error = $"No existing {entity.Name} for {matchField}={kv}; UpdateOnly does not create.";
                                run.Skipped++;
                                db.RunItems.Add(item);
                                continue;
                            }
                            else keyPredicate = null;   // Upsert + not found -> plain create (POST)
                        }
                    }

                    // Serialized last, so a PATCH's key field (removed above) never appears in it.
                    var json = JsonSerializer.Serialize(built.Payload, JsonOpts);
                    item.PayloadJson = json;

                    if (run.DryRun)
                    {
                        item.Status = ItemStatus.Ok;
                        run.Succeeded++;
                        db.RunItems.Add(item);
                    }
                    else
                    {
                        db.RunItems.Add(item);
                        pending.Add((item, json, method, keyPredicate));
                        if (pending.Count >= Math.Max(1, scenario.BatchSize))
                        {
                            await FlushAsync(target, scenario, entity, keyProp, pending, run, db, ct);
                            pending.Clear();
                            await SaveProgressAsync(db, run, rowNumber, ct);
                            if (!scenario.ContinueOnError && run.Failed > 0) break;
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    item.Status = ItemStatus.Failed;
                    item.ErrorStage = "build";
                    item.Error = ex.Message;
                    run.Failed++;
                    db.RunItems.Add(item);
                    if (!scenario.ContinueOnError) break;
                }
            }

            if (pending.Count > 0 && !run.DryRun)
                await FlushAsync(target, scenario, entity, keyProp, pending, run, db, ct);

            run.Total = rowNumber;
            run.Processed = run.Succeeded + run.Failed + run.Skipped;
            run.Status = run.Failed == 0 ? RunStatus.Succeeded
                : run.Succeeded > 0 ? RunStatus.PartiallyFailed : RunStatus.Failed;
            run.FinishedUtc = DateTime.UtcNow;

            // Advance the watermark only on a clean, non-dry run.
            if (!run.DryRun && run.Failed == 0 && scenario.WatermarkColumn != null && maxWatermark != null)
                scenario.WatermarkValue = maxWatermark;

            scenario.LastRunUtc = run.FinishedUtc;
            scenario.LastRunStatus = run.Status.ToString();
            await db.SaveChangesAsync(ct);
            _log.LogInformation("Run {RunId} finished: {Status} ({Ok} ok, {Failed} failed)",
                run.Id, run.Status, run.Succeeded, run.Failed);
        }
        catch (OperationCanceledException)
        {
            run.Status = RunStatus.Cancelled;
            run.FinishedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            await FailAsync(db, run, ex.Message, CancellationToken.None);
            _log.LogError(ex, "Run {RunId} failed", run.Id);
        }
        finally { target?.Dispose(); }
    }

    /// <summary>Post a batch of built records and record each result.</summary>
    private static async Task FlushAsync(
        ITargetSession target, Scenario scenario, TargetEntity entity, string keyProp,
        List<(RunItem item, string json, string method, string? keyPredicate)> pending,
        Run run, AppDbContext db, CancellationToken ct)
    {
        var ops = pending
            .Select(p => new TargetWriteOp(p.item, p.json, p.method, p.keyPredicate))
            .ToList();
        var results = await target.WriteAsync(entity.Name, keyProp, ops, scenario.UseBatch, ct);

        for (int i = 0; i < pending.Count; i++)
        {
            var (item, _, method, keyPredicate) = pending[i];
            var r = results[i];
            item.Attempts++;
            if (r.Ok)
            {
                item.Status = ItemStatus.Ok;
                // PATCH succeeds with 204 No Content, so there's no body to read a key from.
                item.TargetKey = r.Key ?? (method == "PATCH" ? keyPredicate?.Trim('\'') : null);
                // Payload is kept for successes too, so "what was sent" is inspectable either way.
                run.Succeeded++;
            }
            else
            {
                item.Status = ItemStatus.Failed;
                item.ErrorStage = "post";
                item.Error = r.Error;
                run.Failed++;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task SaveProgressAsync(AppDbContext db, Run run, int processed, CancellationToken ct)
    {
        run.Processed = processed;
        await db.SaveChangesAsync(ct);
    }

    private static async Task FailAsync(AppDbContext db, Run run, string error, CancellationToken ct)
    {
        run.Status = RunStatus.Failed;
        run.Error = error;
        run.FinishedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static SourceHandle BuildHandle(Scenario s, ConnectionDef? srcConn, ISecretProtector secrets)
    {
        // SourceObject is overloaded: a table name for sql, but the uploaded file's path for
        // excel/csv — only pass it as SheetOrTable for sql, or ExcelSourceReader tries to open a
        // worksheet literally named the file path and always fails.
        var handle = new SourceHandle
        {
            SourceType = s.SourceKind,
            SheetOrTable = s.SourceKind == "sql" ? s.SourceObject : null,
            Query = s.SourceQuery,
            ConnectionString = secrets.Unprotect(srcConn?.ConnectionStringProtected),
            FilePath = s.SourceKind is "excel" or "csv" ? s.SourceObject : null,
        };

        // Incremental pull: only rows newer than the stored watermark.
        if (s.SourceKind == "sql" && !string.IsNullOrEmpty(s.WatermarkColumn) &&
            !string.IsNullOrEmpty(s.WatermarkValue))
        {
            var inner = s.SourceQuery ?? $"SELECT * FROM [{s.SourceObject}]";
            handle.Query = $"SELECT * FROM ({inner}) AS _src " +
                           $"WHERE [{s.WatermarkColumn}] > '{s.WatermarkValue.Replace("'", "''")}' " +
                           $"ORDER BY [{s.WatermarkColumn}]";
        }
        return handle;
    }

    private static IEnumerable<List<Dictionary<string, object?>>> GroupRows(
        IEnumerable<Dictionary<string, object?>> rows, string? groupBy)
    {
        if (string.IsNullOrEmpty(groupBy))
        {
            foreach (var r in rows) yield return new List<Dictionary<string, object?>> { r };
            yield break;
        }
        List<Dictionary<string, object?>>? current = null;
        string? currentKey = null;
        foreach (var r in rows)
        {
            var key = r.TryGetValue(groupBy, out var v) ? v?.ToString() : null;
            if (current is null || key != currentKey)
            {
                if (current is not null) yield return current;
                current = new List<Dictionary<string, object?>>();
                currentKey = key;
            }
            current.Add(r);
        }
        if (current is not null) yield return current;
    }
}
