using System.Data.Common;
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

    public static bool ColumnExists(DbContext db, string table, string column)
    {
        db.Database.OpenConnection();
        var connection = db.Database.GetDbConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 删掉已有库里的一列。先卸掉引用该列的普通索引：SQLite 拒绝删除仍被索引占用的列。
    /// </summary>
    public static void DropColumnIfPresent(DbContext db, string table, string column)
    {
        if (!ColumnExists(db, table, column))
        {
            return;
        }

        var connection = db.Database.GetDbConnection();
        var indexes = new List<string>();
        using (var list = connection.CreateCommand())
        {
            list.CommandText = $"PRAGMA index_list(\"{table}\")";
            using var reader = list.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                if (!name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
                {
                    indexes.Add(name);
                }
            }
        }

        // 先把要删的索引收齐再执行：读取还没结束就 DROP，SQLite 会报 table is locked。
        var toDrop = new List<string>();
        foreach (var index in indexes)
        {
            using var info = connection.CreateCommand();
            info.CommandText = $"PRAGMA index_info(\"{index}\")";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(2)
                    && string.Equals(reader.GetString(2), column, StringComparison.OrdinalIgnoreCase))
                {
                    toDrop.Add(index);
                    break;
                }
            }
        }

        foreach (var index in toDrop)
        {
            Execute(connection, $"""DROP INDEX IF EXISTS "{index}" """);
        }

        Execute(connection, $"""ALTER TABLE "{table}" DROP COLUMN "{column}" """);
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
