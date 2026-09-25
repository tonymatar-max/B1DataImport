using B1DataImporter.Api.Data;
using B1DataImporter.Api.Domain;
using Cronos;
using Microsoft.EntityFrameworkCore;

namespace B1DataImporter.Api.Services.Jobs;

/// <summary>
/// Wakes every 30s, finds enabled scenarios whose cron is due, and queues a run.
/// This is what turns the tool from a one-shot importer into an integration service.
/// </summary>
public class SchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly RunQueue _queue;
    private readonly ILogger<SchedulerService> _log;

    public SchedulerService(IServiceScopeFactory scopes, RunQueue queue, ILogger<SchedulerService> log)
    {
        _scopes = scopes; _queue = queue; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await TickAsync(ct); }
            catch (Exception ex) { _log.LogError(ex, "Scheduler tick failed"); }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        var due = await db.Scenarios
            .Where(s => s.Enabled && s.Trigger == TriggerKind.Schedule && s.CronExpression != null)
            .ToListAsync(ct);

        foreach (var s in due)
        {
            var next = s.NextRunUtc ?? ComputeNext(s.CronExpression!, now);
            if (next is null) continue;

            if (next <= now)
            {
                var run = new Run
                {
                    ScenarioId = s.Id,
                    ScenarioName = s.Name,
                    Status = RunStatus.Queued,
                    TriggeredBy = "schedule",
                };
                db.Runs.Add(run);
                s.NextRunUtc = ComputeNext(s.CronExpression!, now.AddSeconds(1));
                await db.SaveChangesAsync(ct);
                _queue.Enqueue(run.Id);
                _log.LogInformation("Scheduled run queued for scenario '{Name}'", s.Name);
            }
            else if (s.NextRunUtc is null)
            {
                s.NextRunUtc = next;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    /// <summary>Supports 5-field (minute) and 6-field (second) cron expressions.</summary>
    public static DateTime? ComputeNext(string cron, DateTime fromUtc)
    {
        try
        {
            var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var expr = CronExpression.Parse(cron, fields >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
            return expr.GetNextOccurrence(fromUtc, TimeZoneInfo.Utc);
        }
        catch { return null; }
    }
}
