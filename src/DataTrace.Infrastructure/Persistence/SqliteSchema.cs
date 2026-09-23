using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

internal static class SqliteSchema
{
    public static async Task AddColumnIfMissingAsync(
        DbContext db,
        string table,
        string column,
        string alterSql,
        CancellationToken cancellationToken = default)
    {
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = db.Database.GetDbConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await db.Database.ExecuteSqlRawAsync(alterSql, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 同步版补列。月份库的建表在 <see cref="RuntimeDbFactory"/> 的锁内同步执行，无法 await。
    /// </summary>
    public static void AddColumnIfMissing(DbContext db, string table, string column, string alterSql)
    {
        db.Database.OpenConnection();
        var connection = db.Database.GetDbConnection();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        db.Database.ExecuteSqlRaw(alterSql);
    }
}
