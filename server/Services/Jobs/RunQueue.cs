using System.Collections.Concurrent;
using System.Threading.Channels;

namespace B1DataImporter.Api.Services.Jobs;

/// <summary>
/// Queue of runs waiting to execute, plus a cancellation handle per in-flight run
/// so the UI can stop a long import.
/// </summary>
public class RunQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public void Enqueue(string runId) => _channel.Writer.TryWrite(runId);

    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    public CancellationTokenSource Track(string runId, CancellationToken outer)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _running[runId] = cts;
        return cts;
    }

    public void Untrack(string runId)
    {
        if (_running.TryRemove(runId, out var cts)) cts.Dispose();
    }

    /// <summary>Cancel an in-flight run. Returns false if it isn't running.</summary>
    public bool Cancel(string runId)
    {
        if (!_running.TryGetValue(runId, out var cts)) return false;
        cts.Cancel();
        return true;
    }

    public bool IsRunning(string runId) => _running.ContainsKey(runId);
    public int ActiveCount => _running.Count;
}
