using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Reflection;

namespace DataTrace.Infrastructure.Persistence;

internal static class MigrationBootstrap
{
    // RuntimeSchema is the compatibility authority for history-less customer month databases.
    // Keep this list explicit: adding an EF migration without reviewing its schema effect must
    // fail startup instead of silently marking that migration as already applied.
    private static readonly string[] ReviewedRuntimeMigrations =
    [
        "20261008141905_InitialRuntimeSchema",
        "20261010042850_AddMesOutboxIntent",
        "20261011153000_AddSessionIndexIntentsAndPendingWriteBacks",
        "20261011170000_AddManualConfirmationReview",
        "20261011171000_AddSessionRecipeSnapshot",
        "20261011172000_AddPendingConfirmationTarget",
        "20261011173000_AddCollectRecordConfigurationRevision",
        "20261011174000_AddPendingWriteBackRecordIdentity",
        "20261012023339_AddConditionalEventIdentity",
        "20261012113312_AddTagValueUnitSnapshot",
        "20261012120919_AddTagPurposeSnapshot"
    ];

    public static bool NeedsLegacyBootstrap(DbContext db)
    {
        var baseline = GetBaselineMigration(db);
        db.Database.OpenConnection();

        var history = db.GetService<IHistoryRepository>();
        if (history.Exists() && history.GetAppliedMigrations().Any(row => row.MigrationId == baseline))
        {
            return false;
        }

        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM sqlite_master
                WHERE type = 'table'
                  AND name NOT LIKE 'sqlite_%'
                  AND name <> '__EFMigrationsHistory'
            )
            """;
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    public static void MarkBaseline(DbContext db)
    {
        var baseline = GetBaselineMigration(db);
        var history = db.GetService<IHistoryRepository>();
        if (!history.Exists())
        {
            db.Database.ExecuteSqlRaw(history.GetCreateScript());
        }

        if (history.GetAppliedMigrations().Any(row => row.MigrationId == baseline))
        {
            return;
        }

        var productVersion = typeof(DbContext).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?
            .Split('+', 2)[0]
            ?? throw new InvalidOperationException("无法读取 EF Core 产品版本");
        db.Database.ExecuteSqlRaw(history.GetInsertScript(new HistoryRow(baseline, productVersion)));
    }

    public static void MarkMigrationApplied(DbContext db, string migrationId)
    {
        if (!db.Database.GetMigrations().Contains(migrationId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"未知迁移：{migrationId}");
        }

        var history = db.GetService<IHistoryRepository>();
        if (!history.Exists())
        {
            db.Database.ExecuteSqlRaw(history.GetCreateScript());
        }

        if (history.GetAppliedMigrations().Any(row => row.MigrationId == migrationId))
        {
            return;
        }

        var productVersion = typeof(DbContext).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?
            .Split('+', 2)[0]
            ?? throw new InvalidOperationException("无法读取 EF Core 产品版本");
        db.Database.ExecuteSqlRaw(history.GetInsertScript(new HistoryRow(migrationId, productVersion)));
    }

    public static void MarkReviewedRuntimeMigrationsApplied(DbContext db)
    {
        var migrations = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var unknown = migrations.Except(ReviewedRuntimeMigrations, StringComparer.Ordinal).ToArray();
        var missing = ReviewedRuntimeMigrations.Except(migrations, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0 || missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"RuntimeSchema legacy bootstrap migration allowlist is stale. Unknown: [{string.Join(", ", unknown)}]; missing: [{string.Join(", ", missing)}]. Review schema coverage before updating the allowlist.");
        }

        var history = db.GetService<IHistoryRepository>();
        if (!history.Exists()) db.Database.ExecuteSqlRaw(history.GetCreateScript());
        var applied = history.GetAppliedMigrations().Select(row => row.MigrationId).ToHashSet(StringComparer.Ordinal);
        var productVersion = typeof(DbContext).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?
            .Split('+', 2)[0]
            ?? throw new InvalidOperationException("无法读取 EF Core 产品版本");
        foreach (var migration in ReviewedRuntimeMigrations)
        {
            if (!applied.Contains(migration))
                db.Database.ExecuteSqlRaw(history.GetInsertScript(new HistoryRow(migration, productVersion)));
        }
    }

    private static string GetBaselineMigration(DbContext db)
        => db.Database.GetMigrations().FirstOrDefault()
            ?? throw new InvalidOperationException($"{db.GetType().Name} 没有配置 EF Core 迁移");
}
