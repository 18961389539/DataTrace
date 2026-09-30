using System.Data;
using System.Globalization;
using DataTrace.Application.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

/// <summary>
/// 每日流水号，形如 <c>20260919-000001</c>。
/// </summary>
/// <remarks>
/// 自增必须由数据库<b>原子</b>完成，不能用"读一行 → 加一 → 写回"：
/// 采集端每个工站各跑各的流水线（<c>CollectionHostedService</c> 用 <c>Task.Run</c> 并行），
/// 而每次落库都会新开一个 scope —— 旧实现把锁挂在实例字段上，新 scope 里就是另一把锁，
/// 互斥范围为零。当天首件之后的两路并发会把同一行读成 5、各自写回 6，丢更新产重号。
/// <para>
/// 现在用单条 <c>INSERT ... ON CONFLICT ... DO UPDATE ... RETURNING</c>：SQLite 保证语句级原子，
/// 没有读-改-写窗口；<c>DayKey</c> 上的唯一索引既是 UPSERT 的定位目标，也兜住任何意外插入。
/// 写锁竞争由 SQLite 的 busy_timeout（跟随连接串的 Default Timeout）等待，再叠一层有限重试。
/// </para>
/// <para>
/// 无状态，注册为单例；每次自增用工厂新开一个 context，避免与调用方共用一个长生命周期连接。
/// </para>
/// </remarks>
public sealed class SerialNumberGenerator : ISerialNumberGenerator
{
    /// <summary>写忙碌（SQLITE_BUSY / SQLITE_LOCKED）时的重试次数。SQLite 是单写者模型，等待通常极短。</summary>
    private const int MaxAttempts = 5;

    private readonly IDbContextFactory<ConfigDbContext> _factory;

    public SerialNumberGenerator(IDbContextFactory<ConfigDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<string> NextAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        var dayKey = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var value = await IncrementAsync(dayKey, cancellationToken).ConfigureAwait(false);
                return $"{dayKey}-{value:000000}";
            }
            catch (SqliteException ex) when (attempt < MaxAttempts && IsTransient(ex))
            {
                // 退避一下再试：并发的写入者在毫秒级就会让出写锁。
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<long> IncrementAsync(string dayKey, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var cmd = connection.CreateCommand();
        // 一条语句里完成"不存在则从 1 起、存在则加一"并把结果带回来。
        cmd.CommandText = """
            INSERT INTO "SerialCounters" ("DayKey", "LastValue") VALUES ($day, 1)
            ON CONFLICT("DayKey") DO UPDATE SET "LastValue" = "LastValue" + 1
            RETURNING "LastValue";
            """;
        var day = cmd.CreateParameter();
        day.ParameterName = "$day";
        day.Value = dayKey;
        cmd.Parameters.Add(day);

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            throw new InvalidOperationException($"流水号自增没有返回结果（DayKey={dayKey}）。");
        }

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <summary>SQLITE_BUSY(5) / SQLITE_LOCKED(6)：写锁暂时被别的连接占着，重试有意义。</summary>
    private static bool IsTransient(SqliteException ex) => ex.SqliteErrorCode is 5 or 6;
}
