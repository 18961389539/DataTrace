using DataTrace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DataTrace.Tests;

public sealed class RuntimeLegacyBootstrapTests
{
    [Fact]
    public async Task Historyless_legacy_month_is_upgraded_and_reopened_without_duplicate_columns()
    {
        using var workspace = new TempWorkspace();
        var root = Path.Combine(workspace.Root, "runtime");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "data_202609.db");
        var options = new DbContextOptionsBuilder<RuntimeDbContext>().UseSqlite($"Data Source={path}").Options;

        // Build an actual EF initial-migration database, then remove its history row to represent
        // a customer month that predates the recorded migration chain. Keep a real row to prove
        // the schema upgrade preserves customer data.
        await using (var legacy = new RuntimeDbContext(options))
        {
            var initialMigration = legacy.Database.GetMigrations().First();
            await legacy.GetService<IMigrator>().MigrateAsync(initialMigration);
            await legacy.Database.ExecuteSqlRawAsync(
                "INSERT INTO PalletSessions (SerialNo, PalletCode, StartTime, Status, Judgement) VALUES ('SN-OLD', 'P-OLD', '2026-09-01 08:00:00', 0, 0)");
            await legacy.Database.ExecuteSqlRawAsync(
                "INSERT INTO CollectRecords (PalletSessionId, SerialNo, PalletCode, StationId, StationCode, TriggerTime, CompleteTime, DurationMs, ResultCode, Judgement, RecipeCode, ArchivePath, ArchiveFileSize, ArchiveCrc32) VALUES (1, 'SN-OLD', 'P-OLD', 1, 'ST01', '2026-09-01 08:00:00', '2026-09-01 08:00:01', 1, 1, 0, '', '', 0, 0)");
            await legacy.Database.ExecuteSqlRawAsync("DELETE FROM __EFMigrationsHistory");
        }

        var factory = new RuntimeDbFactory(root);
        await using (var upgraded = factory.Open("202609"))
        {
            Assert.Equal("SN-OLD", (await upgraded.CollectRecords.AsNoTracking().SingleAsync()).SerialNo);
            Assert.Equal(0, await upgraded.MesOutboxIntents.CountAsync());
            Assert.True(await upgraded.Database.SqlQueryRaw<long>(
                "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name='MesOutboxIntents'").SingleAsync() == 1);
            Assert.False(upgraded.Database.HasPendingModelChanges());
        }

        // Reopen through another factory so the result is checked against persisted migration
        // history, not RuntimeDbFactory's per-instance ensured cache.
        await using var reopened = new RuntimeDbFactory(root).Open("202609");
        Assert.Equal("SN-OLD", (await reopened.CollectRecords.AsNoTracking().SingleAsync()).SerialNo);
        Assert.Equal(RuntimeSchema.CurrentVersion, await reopened.Database.SqlQueryRaw<int>(
            "SELECT Version AS Value FROM RuntimeSchemaVersion WHERE Id = 1").SingleAsync());
        Assert.Equal(0, await reopened.MesOutboxIntents.CountAsync());
    }
}
