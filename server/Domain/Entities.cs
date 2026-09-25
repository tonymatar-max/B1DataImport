using System.ComponentModel.DataAnnotations;

namespace B1DataImporter.Api.Domain;

// ============================================================ Connections

public enum ConnectionKind { SapB1, SqlServer, File }

/// <summary>A reusable, named connection to either SAP B1 or a source system.</summary>
public class ConnectionDef
{
    [Key] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public ConnectionKind Kind { get; set; }

    // SAP B1
    public string? BaseUrl { get; set; }
    public string? CompanyDB { get; set; }
    public string? UserName { get; set; }
    /// <summary>Protected with ASP.NET DataProtection — never stored or returned in clear.</summary>
    public string? SecretProtected { get; set; }
    public bool IgnoreSslErrors { get; set; } = true;

    // SQL / file
    public string? ConnectionStringProtected { get; set; }
    public string? RootPath { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastTestedUtc { get; set; }
    public string? LastTestResult { get; set; }
}

// ============================================================ Scenario (the B1if analogue)

public enum TriggerKind { Manual, Schedule }
public enum WriteMode { Create, Upsert, UpdateOnly }

/// <summary>
/// A named, persisted integration definition: pull from a source, map it, write to a B1 object.
/// This is the unit that gets scheduled, queued, run, and monitored.
/// </summary>
public class Scenario
{
    [Key] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;

    // --- Source ---
    public string SourceConnectionId { get; set; } = "";
    public string SourceKind { get; set; } = "sql";       // sql | excel | csv
    public string? SourceQuery { get; set; }              // SELECT ... (sql)
    public string? SourceObject { get; set; }             // table / sheet / file path

    // --- Target ---
    public string B1ConnectionId { get; set; } = "";
    public string TargetEntity { get; set; } = "";        // e.g. BusinessPartners, Orders
    public WriteMode WriteMode { get; set; } = WriteMode.Create;
    /// <summary>Field(s) used to find an existing record for upsert, e.g. "CardCode".</summary>
    public string? KeyFields { get; set; }

    // --- Mapping (JSON: MappingSpec) ---
    public string MappingJson { get; set; } = "{}";

    // --- Behaviour ---
    public TriggerKind Trigger { get; set; } = TriggerKind.Manual;
    public string? CronExpression { get; set; }           // e.g. "0 */15 * * * *"
    public bool ContinueOnError { get; set; } = true;
    public int BatchSize { get; set; } = 20;
    public bool UseBatch { get; set; } = true;
    public int MaxRetries { get; set; } = 2;

    /// <summary>Incremental sync: only pull rows where this column &gt; the stored watermark.</summary>
    public string? WatermarkColumn { get; set; }
    public string? WatermarkValue { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? NextRunUtc { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public string? LastRunStatus { get; set; }
}

// ============================================================ Runs (monitoring)

public enum RunStatus { Queued, Running, Succeeded, PartiallyFailed, Failed, Cancelled }

/// <summary>One execution of a scenario. The monitoring record.</summary>
public class Run
{
    [Key] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ScenarioId { get; set; } = "";
    public string ScenarioName { get; set; } = "";
    public RunStatus Status { get; set; } = RunStatus.Queued;
    public bool DryRun { get; set; }
    public string TriggeredBy { get; set; } = "manual";   // manual | schedule | retry

    public DateTime QueuedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }

    public int Total { get; set; }
    public int Processed { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }

    public string? Error { get; set; }                    // fatal error that aborted the run
    /// <summary>Run this to retry only the failed items of a previous run.</summary>
    public string? RetryOfRunId { get; set; }
    /// <summary>
    /// Comma-separated row numbers to retry from RetryOfRunId. Null means "every failed row"
    /// (the original retry-all behavior); set means "just these rows" (single- or multi-row retry).
    /// </summary>
    public string? RetryRowNumbers { get; set; }

    public List<RunItem> Items { get; set; } = new();
}

public enum ItemStatus { Pending, Ok, Failed, Skipped }

/// <summary>Per-record result — this is what makes failures re-runnable.</summary>
public class RunItem
{
    [Key] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RunId { get; set; } = "";
    public Run? Run { get; set; }

    public int RowNumber { get; set; }
    public ItemStatus Status { get; set; } = ItemStatus.Pending;
    /// <summary>Business key from the source, so a human can find the row again.</summary>
    public string? SourceKey { get; set; }
    /// <summary>Key returned by B1 (DocEntry / CardCode / ItemCode).</summary>
    public string? TargetKey { get; set; }
    public string? Error { get; set; }
    public string? ErrorStage { get; set; }               // validation | lookup | post
    public string? PayloadJson { get; set; }
    public int Attempts { get; set; }
}
