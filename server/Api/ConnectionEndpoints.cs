using B1DataImporter.Api.Data;
using B1DataImporter.Api.Domain;
using B1DataImporter.Api.Services;
using B1DataImporter.Api.Services.B1;
using B1DataImporter.Api.Services.Jobs;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace B1DataImporter.Api.Api;

public record ConnectionInput(
    string Name, ConnectionKind Kind, string? BaseUrl, string? CompanyDB, string? UserName,
    string? Password, bool IgnoreSslErrors);

public static class ConnectionEndpoints
{
    // For SQL Server connections, BaseUrl holds the server name and CompanyDB the database name —
    // same fields SAP B1 uses for its URL/company, so no separate schema is needed per kind.
    private static string BuildSqlConnectionString(string? server, string? database, string? userName, string? password) =>
        $"Server={server};Database={database};User Id={userName};Password={password};TrustServerCertificate=True";

    public static void MapConnections(this WebApplication app)
    {
        var g = app.MapGroup("/api/connections");

        // Secrets are never returned to the browser — only whether one is set.
        g.MapGet("", async (AppDbContext db) =>
            Results.Ok((await db.Connections.OrderBy(c => c.Name).ToListAsync()).Select(Shape)));

        g.MapPost("", async (ConnectionInput input, AppDbContext db, ISecretProtector secrets) =>
        {
            var c = new ConnectionDef
            {
                Name = input.Name,
                Kind = input.Kind,
                BaseUrl = input.BaseUrl,
                CompanyDB = input.CompanyDB,
                UserName = input.UserName,
                IgnoreSslErrors = input.IgnoreSslErrors,
                SecretProtected = secrets.Protect(input.Password),
            };
            if (input.Kind == ConnectionKind.SqlServer)
                c.ConnectionStringProtected = secrets.Protect(
                    BuildSqlConnectionString(input.BaseUrl, input.CompanyDB, input.UserName, input.Password));
            db.Connections.Add(c);
            await db.SaveChangesAsync();
            return Results.Ok(Shape(c));
        });

        g.MapPut("/{id}", async (string id, ConnectionInput input, AppDbContext db, ISecretProtector secrets) =>
        {
            var c = await db.Connections.FindAsync(id);
            if (c is null) return Results.NotFound();
            c.Name = input.Name;
            c.BaseUrl = input.BaseUrl;
            c.CompanyDB = input.CompanyDB;
            c.UserName = input.UserName;
            c.IgnoreSslErrors = input.IgnoreSslErrors;
            // Only overwrite a secret when a new one was actually supplied.
            var password = input.Password;
            if (!string.IsNullOrEmpty(password)) c.SecretProtected = secrets.Protect(password);
            else password = secrets.Unprotect(c.SecretProtected);
            if (c.Kind == ConnectionKind.SqlServer)
                c.ConnectionStringProtected = secrets.Protect(
                    BuildSqlConnectionString(input.BaseUrl, input.CompanyDB, input.UserName, password));
            await db.SaveChangesAsync();
            return Results.Ok(Shape(c));
        });

        g.MapDelete("/{id}", async (string id, AppDbContext db) =>
        {
            var c = await db.Connections.FindAsync(id);
            if (c is null) return Results.NotFound();
            db.Connections.Remove(c);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapPost("/{id}/test", async (string id, AppDbContext db, ISecretProtector secrets) =>
        {
            var c = await db.Connections.FindAsync(id);
            if (c is null) return Results.NotFound();
            try
            {
                if (c.Kind == ConnectionKind.SapB1)
                {
                    using var client = new ServiceLayerClient(ScenarioExecutor.ToInfo(c, secrets));
                    await client.LoginAsync();
                }
                else
                {
                    await using var sql = new SqlConnection(secrets.Unprotect(c.ConnectionStringProtected));
                    await sql.OpenAsync();
                }
                c.LastTestedUtc = DateTime.UtcNow;
                c.LastTestResult = "ok";
                await db.SaveChangesAsync();
                return Results.Ok(new { ok = true, message = "Connected." });
            }
            catch (Exception ex)
            {
                c.LastTestedUtc = DateTime.UtcNow;
                c.LastTestResult = ex.Message;
                await db.SaveChangesAsync();
                return Results.BadRequest(new { ok = false, message = ex.Message });
            }
        });

        // Discover objects on a saved B1 connection.
        g.MapGet("/{id}/entities", async (string id, AppDbContext db, ISecretProtector secrets, MetadataService meta) =>
        {
            var c = await db.Connections.FindAsync(id);
            if (c is null) return Results.NotFound();
            try
            {
                using var client = new ServiceLayerClient(ScenarioExecutor.ToInfo(c, secrets));
                await client.LoginAsync();
                var entities = meta.Parse(await client.GetMetadataAsync(), c.BaseUrl + "|" + c.CompanyDB);
                return Results.Ok(entities.Select(e => new
                {
                    e.Name, e.HasUdfs,
                    fieldCount = e.Properties.Count,
                    mandatoryCount = e.Properties.Count(p => !p.Nullable && !p.IsKey),
                    collections = e.Collections.Select(x => x.Name),
                }));
            }
            catch (Exception ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        g.MapGet("/{id}/entities/{name}", async (string id, string name, AppDbContext db,
            ISecretProtector secrets, MetadataService meta) =>
        {
            var c = await db.Connections.FindAsync(id);
            if (c is null) return Results.NotFound();
            try
            {
                using var client = new ServiceLayerClient(ScenarioExecutor.ToInfo(c, secrets));
                await client.LoginAsync();
                var entities = meta.Parse(await client.GetMetadataAsync(), c.BaseUrl + "|" + c.CompanyDB);
                var e = entities.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                return e is null ? Results.NotFound() : Results.Ok(e);
            }
            catch (Exception ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        // List tables/views on a saved SQL connection.
        g.MapGet("/{id}/tables", async (string id, AppDbContext db, ISecretProtector secrets) =>
        {
            var c = await db.Connections.FindAsync(id);
            if (c is null) return Results.NotFound();
            try { return Results.Ok(SqlSourceReader.ListTables(secrets.Unprotect(c.ConnectionStringProtected)!)); }
            catch (Exception ex) { return Results.BadRequest(new { message = ex.Message }); }
        });
    }

    private static object Shape(ConnectionDef c) => new
    {
        c.Id, c.Name, c.Kind, c.BaseUrl, c.CompanyDB, c.UserName, c.IgnoreSslErrors,
        hasSecret = !string.IsNullOrEmpty(c.SecretProtected) || !string.IsNullOrEmpty(c.ConnectionStringProtected),
        c.LastTestedUtc, c.LastTestResult,
    };
}
