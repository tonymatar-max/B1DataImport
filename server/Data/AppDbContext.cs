using B1DataImporter.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace B1DataImporter.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ConnectionDef> Connections => Set<ConnectionDef>();
    public DbSet<Scenario> Scenarios => Set<Scenario>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<RunItem> RunItems => Set<RunItem>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Run>()
            .HasMany(r => r.Items)
            .WithOne(i => i.Run!)
            .HasForeignKey(i => i.RunId)
            .OnDelete(DeleteBehavior.Cascade);

        // Run history is queried by scenario + recency; items by run + status (for retry).
        b.Entity<Run>().HasIndex(r => new { r.ScenarioId, r.QueuedUtc });
        b.Entity<RunItem>().HasIndex(i => new { i.RunId, i.Status });
        b.Entity<Scenario>().HasIndex(s => s.Enabled);

        // SQLite loses DateTimeKind, so timestamps would serialise without a "Z" and be
        // read as local time by the browser. Re-stamp them as UTC on the way out.
        var utc = new ValueConverter<DateTime, DateTime>(
            v => v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(
            v => v.HasValue ? v.Value.ToUniversalTime() : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var prop in entity.GetProperties())
            {
                if (prop.ClrType == typeof(DateTime)) prop.SetValueConverter(utc);
                else if (prop.ClrType == typeof(DateTime?)) prop.SetValueConverter(utcNullable);
            }
    }
}
