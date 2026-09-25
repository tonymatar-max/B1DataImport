using B1DataImporter.Api.Data;
using B1DataImporter.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace B1DataImporter.Api.Services.Jobs;

/// <summary>Drains the run queue one run at a time, so imports never block an HTTP request.</summary>
public class RunWorker : BackgroundService
{
    private readonly RunQueue _queue;
    private readonly ScenarioExecutor _executor;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RunWorker> _log;

    public RunWorker(RunQueue queue, ScenarioExecutor executor,
        IServiceScopeFactory scopes, ILogger<RunWorker> log)
    {
        _queue = queue; _executor = executor; _scopes = scopes; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueOrphansAsync(stoppingToken);

        await foreach (var runId in _queue.ReadAllAsync(stoppingToken))
        {
            var cts = _queue.Track(runId, stoppingToken);
            try { await _executor.ExecuteAsync(runId, cts.Token); }
            catch (Exception ex) { _log.LogError(ex, "Unhandled failure in run {RunId}", runId); }
            finally { _queue.Untrack(runId); }
        }
    }

    /// <summary>A run left Running/Queued by a crash or restart is marked failed on startup.</summary>
    private async Task RequeueOrphansAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orphans = await db.Runs
            .Where(r => r.Status == RunStatus.Running || r.Status == RunStatus.Queued)
            .ToListAsync(ct);

        foreach (var r in orphans)
        {
            r.Status = RunStatus.Failed;
            r.Error = "Interrupted by a service restart.";
            r.FinishedUtc = DateTime.UtcNow;
        }
        if (orphans.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            _log.LogWarning("Marked {Count} interrupted run(s) as failed on startup.", orphans.Count);
        }
    }
}
