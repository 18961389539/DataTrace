using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Reflection;

namespace DataTrace.Infrastructure.Persistence;

internal static class MigrationBootstrap
{
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

    private static string GetBaselineMigration(DbContext db)
        => db.Database.GetMigrations().FirstOrDefault()
            ?? throw new InvalidOperationException($"{db.GetType().Name} 没有配置 EF Core 迁移");
}