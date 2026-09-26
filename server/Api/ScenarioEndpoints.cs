using System.Text.Json;
using B1DataImporter.Api.Data;
using B1DataImporter.Api.Domain;
using B1DataImporter.Api.Models;
using B1DataImporter.Api.Services;
using B1DataImporter.Api.Services.Ai;
using B1DataImporter.Api.Services.B1;
using B1DataImporter.Api.Services.Connectors;
using B1DataImporter.Api.Services.Connectors.B1;
using B1DataImporter.Api.Services.Jobs;
using Microsoft.EntityFrameworkCore;

namespace B1DataImporter.Api.Api;

public record AiProposeRequest(string ConnectionId, string TargetEntity, SourceSchema Source);
public record PreviewRequest(string SourceKind, string? ConnectionId, string? Query, string? Object);

public static class ScenarioEndpoints
{
    public static void MapScenarios(this WebApplication app)
    {
        var g = app.MapGroup("/api/scenarios");

        g.MapGet("", async (AppDbContext db) => Results.Ok(
            await db.Scenarios.OrderByDescending(s => s.UpdatedUtc).ToListAsync()));

        g.MapGet("/{id}", async (string id, AppDbContext db) =>
            await db.Scenarios.FindAsync(id) is { } s ? Results.Ok(s) : Results.NotFound());

        g.MapPost("", async (Scenario s, AppDbContext db) =>
        {
            s.UpdatedUtc = DateTime.UtcNow;
            if (s.Trigger == TriggerKind.Schedule && s.CronExpression != null)
                s.NextRunUtc = SchedulerService.ComputeNext(s.CronExpression, DateTime.UtcNow);
            var existing = await db.Scenarios.FindAsync(s.Id);
            if (existing is null) db.Scenarios.Add(s);
            else db.Entry(existing).CurrentValues.SetValues(s);
            await db.SaveChangesAsync();
            return Results.Ok(s);
        });

        g.MapDelete("/{id}", async (string id, AppDbContext db) =>
        {
            var s = await db.Scenarios.FindAsync(id);
            if (s is null) return Results.NotFound();
            db.Scenarios.Remove(s);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // Preview the source rows a scenario would read.
        g.MapPost("/preview", async (PreviewRequest req, AppDbContext db,
            ISecretProtector secrets, SourceReaderFactory readers,
            SourceConnectorRegistry sourceConnectors) =>
        {
            try
            {
                var conn = req.ConnectionId is null ? null : await db.Connections.FindAsync(req.ConnectionId);
                // Connection-backed sources (REST/OData) inspect through a source connector.
                if (req.SourceKind.Equals("rest", StringComparison.OrdinalIgnoreCase))
                {
                    if (conn is null) return Results.BadRequest(new { message = "A REST source needs a connection." });
                    return Results.Ok(sourceConnectors.Get(conn.Kind).Inspect(conn, req.Object ?? ""));
                }
                var handle = new SourceHandle
                {
                    SourceType = req.SourceKind,
                    // Object is overloaded: a table name for sql, but the uploaded file's path
                    // for excel/csv — only pass it as SheetOrTable for sql (see BuildHandle).
                    SheetOrTable = req.SourceKind == "sql" ? req.Object : null,
                    Query = req.Query,
                    ConnectionString = secrets.Unprotect(conn?.ConnectionStringProtected),
                    FilePath = req.SourceKind is "excel" or "csv" ? req.Object : null,
                };
                return Results.Ok(readers.Get(req.SourceKind).Inspect(handle));
            }
            catch (Exception ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        // Ask Claude to propose the mapping.
        g.MapPost("/ai-propose", async (AiProposeRequest req, AppDbContext db,
            TargetConnectorRegistry registry, AiMappingService ai) =>
        {
            if (!ai.IsConfigured)
                return Results.BadRequest(new { message = "No AI API key configured. Set OPENROUTER_API_KEY (recommended) or ANTHROPIC_API_KEY." });
            var c = await db.Connections.FindAsync(req.ConnectionId);
            if (c is null) return Results.NotFound();
            if (!registry.Has(c.Kind)) return Results.BadRequest(new { message = $"{c.Kind} is not a writable target." });
            try
            {
                using var session = await registry.Get(c.Kind).OpenAsync(c);
                var entities = await session.GetEntitiesAsync();
                var entity = entities.FirstOrDefault(e =>
                    e.Name.Equals(req.TargetEntity, StringComparison.OrdinalIgnoreCase));
                if (entity is null) return Results.NotFound();

                var proposal = await ai.ProposeAsync(req.Source, entity);
                return Results.Ok(new
                {
                    mapping = proposal.Spec,
                    notes = proposal.Notes,
                    summary = proposal.Summary,
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        // Queue a run (dry or live).
        g.MapPost("/{id}/run", async (string id, bool? dryRun, AppDbContext db, RunQueue queue) =>
        {
            var s = await db.Scenarios.FindAsync(id);
            if (s is null) return Results.NotFound();
            var run = new Run
            {
                ScenarioId = s.Id,
                ScenarioName = s.Name,
                DryRun = dryRun ?? false,
                TriggeredBy = "manual",
            };
            db.Runs.Add(run);
            await db.SaveChangesAsync();
            queue.Enqueue(run.Id);
            return Results.Ok(run);
        });
    }

    public static void MapRuns(this WebApplication app)
    {
        var g = app.MapGroup("/api/runs");

        g.MapGet("", async (string? scenarioId, int? limit, AppDbContext db) =>
        {
            var q = db.Runs.AsQueryable();
            if (scenarioId != null) q = q.Where(r => r.ScenarioId == scenarioId);
            return Results.Ok(await q.OrderByDescending(r => r.QueuedUtc)
                .Take(limit ?? 50).ToListAsync());
        });

        g.MapGet("/{id}", async (string id, AppDbContext db, RunQueue queue) =>
        {
            var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == id);
            if (run is null) return Results.NotFound();
            return Results.Ok(new { run, isRunning = queue.IsRunning(id) });
        });

        // Per-row results, filterable to just the failures and/or a text search over
        // source key / error / target key.
        g.MapGet("/{id}/items", async (string id, string? status, string? q, int? limit, AppDbContext db) =>
        {
            var query = db.RunItems.Where(i => i.RunId == id);
            if (Enum.TryParse<ItemStatus>(status, true, out var st)) query = query.Where(i => i.Status == st);
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(i =>
                    (i.SourceKey != null && i.SourceKey.Contains(q)) ||
                    (i.Error != null && i.Error.Contains(q)) ||
                    (i.TargetKey != null && i.TargetKey.Contains(q)));
            return Results.Ok(await query.OrderBy(i => i.RowNumber).Take(limit ?? 500).ToListAsync());
        });

        // Failures grouped by message, so a pattern (e.g. one bad lookup affecting 200 rows)
        // is obvious at a glance instead of scrolling 200 identical-looking rows.
        g.MapGet("/{id}/failure-summary", async (string id, AppDbContext db) =>
        {
            var groups = await db.RunItems
                .Where(i => i.RunId == id && i.Status == ItemStatus.Failed)
                .GroupBy(i => i.Error ?? "(no message)")
                .Select(g => new { error = g.Key, count = g.Count(), rows = g.OrderBy(i => i.RowNumber).Select(i => i.RowNumber).Take(20) })
                .OrderByDescending(g => g.count)
                .ToListAsync();
            return Results.Ok(groups);
        });

        // Download all rows (or just one status) as CSV — for handing failures to whoever owns the data.
        g.MapGet("/{id}/export", async (string id, string? status, AppDbContext db) =>
        {
            var query = db.RunItems.Where(i => i.RunId == id);
            if (Enum.TryParse<ItemStatus>(status, true, out var st)) query = query.Where(i => i.Status == st);
            var items = await query.OrderBy(i => i.RowNumber).ToListAsync();

            string Csv(string? s) => s is null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";
            var sb = new System.Text.StringBuilder("Row,Status,SourceKey,TargetKey,Stage,Error,Attempts\n");
            foreach (var i in items)
                sb.AppendLine(string.Join(',', i.RowNumber, Csv(i.Status.ToString()), Csv(i.SourceKey),
                    Csv(i.TargetKey), Csv(i.ErrorStage), Csv(i.Error), i.Attempts));

            return Results.File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()), "text/csv",
                $"run-{id}{(status != null ? "-" + status : "")}.csv");
        });

        g.MapPost("/{id}/cancel", (string id, RunQueue queue) =>
            queue.Cancel(id) ? Results.Ok(new { cancelled = true })
                             : Results.BadRequest(new { message = "That run is not currently executing." }));

        // Re-run every failed row, or (with `rows`) just specific ones — single-row retry included.
        g.MapPost("/{id}/retry-failed", async (string id, string? rows, AppDbContext db, RunQueue queue) =>
        {
            var prev = await db.Runs.FirstOrDefaultAsync(r => r.Id == id);
            if (prev is null) return Results.NotFound();

            IQueryable<RunItem> failedQuery = db.RunItems.Where(i => i.RunId == id && i.Status == ItemStatus.Failed);
            if (!string.IsNullOrEmpty(rows))
            {
                var wanted = rows.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
                failedQuery = failedQuery.Where(i => wanted.Contains(i.RowNumber));
            }
            var failedCount = await failedQuery.CountAsync();
            if (failedCount == 0) return Results.BadRequest(new { message = "No matching failed rows to retry." });

            var run = new Run
            {
                ScenarioId = prev.ScenarioId,
                ScenarioName = prev.ScenarioName,
                TriggeredBy = "retry",
                RetryOfRunId = prev.Id,
                RetryRowNumbers = rows,
            };
            db.Runs.Add(run);
            await db.SaveChangesAsync();
            queue.Enqueue(run.Id);
            return Results.Ok(run);
        });

        // Dashboard summary.
        app.MapGet("/api/dashboard", async (AppDbContext db, RunQueue queue) =>
        {
            var since = DateTime.UtcNow.AddDays(-7);
            var recent = await db.Runs.Where(r => r.QueuedUtc >= since).ToListAsync();
            return Results.Ok(new
            {
                scenarios = await db.Scenarios.CountAsync(),
                enabled = await db.Scenarios.CountAsync(s => s.Enabled),
                scheduled = await db.Scenarios.CountAsync(s => s.Trigger == TriggerKind.Schedule && s.Enabled),
                activeRuns = queue.ActiveCount,
                last7Days = new
                {
                    runs = recent.Count,
                    records = recent.Sum(r => r.Succeeded),
                    failures = recent.Sum(r => r.Failed),
                },
                latest = await db.Runs.OrderByDescending(r => r.QueuedUtc).Take(10).ToListAsync(),
            });
        });
    }
}
