using B1DataImporter.Api.Api;
using B1DataImporter.Api.Data;
using B1DataImporter.Api.Services;
using B1DataImporter.Api.Services.Ai;
using B1DataImporter.Api.Services.Jobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(Path.Combine(dataDir, "uploads"));

// Enums travel as their names ("SapB1", "Succeeded"), not ordinals, in both directions.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite($"Data Source={Path.Combine(dataDir, "importer.db")}"));

builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));
builder.Services.AddSingleton<ISecretProtector, SecretProtector>();

builder.Services.AddSingleton<MetadataService>();
builder.Services.AddSingleton<SourceReaderFactory>();
builder.Services.AddSingleton<AiMappingService>();

// Target connectors: one ITargetConnector per system, resolved by ConnectionKind. Register a
// new connector here (e.g. a manifest-driven REST connector) and the executor picks it up.
builder.Services.AddSingleton<B1DataImporter.Api.Services.Connectors.Rest.ManifestStore>();
builder.Services.AddSingleton<B1DataImporter.Api.Services.Connectors.ITargetConnector,
    B1DataImporter.Api.Services.Connectors.B1.B1TargetConnector>();
builder.Services.AddSingleton<B1DataImporter.Api.Services.Connectors.ITargetConnector,
    B1DataImporter.Api.Services.Connectors.Rest.RestManifestConnector>();
builder.Services.AddSingleton<B1DataImporter.Api.Services.Connectors.TargetConnectorRegistry>();

// Source connectors: pull FROM a system. File/SQL keep the ISourceReader path; REST/OData reads here.
builder.Services.AddSingleton<B1DataImporter.Api.Services.Connectors.ISourceConnector,
    B1DataImporter.Api.Services.Connectors.Rest.RestSourceConnector>();
builder.Services.AddSingleton<B1DataImporter.Api.Services.Connectors.SourceConnectorRegistry>();

builder.Services.AddSingleton<RunQueue>();
builder.Services.AddSingleton<ScenarioExecutor>();
builder.Services.AddHostedService<RunWorker>();
builder.Services.AddHostedService<SchedulerService>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173", "http://localhost:5174")
     .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbc = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbc.Database.EnsureCreated();
    // No migrations (EnsureCreated only builds the schema when the DB doesn't exist yet) — patch
    // an already-existing dev DB forward for columns added after it was first created.
    if (!dbc.Database.SqlQueryRaw<int>(
            "select count(*) as [Value] from pragma_table_info('Runs') where name = 'RetryRowNumbers'")
            .AsEnumerable().Single().Equals(1))
        dbc.Database.ExecuteSqlRaw("alter table Runs add column RetryRowNumbers TEXT NULL");
    if (!dbc.Database.SqlQueryRaw<int>(
            "select count(*) as [Value] from pragma_table_info('Connections') where name = 'ConnectorManifestId'")
            .AsEnumerable().Single().Equals(1))
        dbc.Database.ExecuteSqlRaw("alter table Connections add column ConnectorManifestId TEXT NULL");
}

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapConnections();
app.MapScenarios();
app.MapRuns();

// Upload a spreadsheet to use as a scenario source; returns the stored path + schema.
app.MapPost("/api/upload", async (HttpRequest request, SourceReaderFactory readers) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { message = "Expected multipart form." });
    var form = await request.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file is null) return Results.BadRequest(new { message = "No file uploaded." });

    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    var kind = ext == ".csv" ? "csv" : "excel";
    var path = Path.Combine(dataDir, "uploads", Guid.NewGuid().ToString("N") + ext);
    await using (var fs = File.Create(path)) await file.CopyToAsync(fs);

    var handle = new B1DataImporter.Api.Models.SourceHandle
    {
        SourceType = kind, FilePath = path, SheetOrTable = form["sheet"].FirstOrDefault(),
    };
    var schema = readers.Get(kind).Inspect(handle);
    var sheets = kind == "excel" ? ExcelSourceReader.ListSheets(path) : new List<string>();
    return Results.Ok(new { path, kind, fileName = file.FileName, schema, sheets });
});

app.MapGet("/api/health", (AiMappingService ai) =>
    Results.Ok(new { ok = true, aiConfigured = ai.IsConfigured }));

app.MapFallbackToFile("index.html");

app.Run();
