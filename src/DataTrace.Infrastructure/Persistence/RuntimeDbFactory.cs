using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;

namespace DataTrace.Infrastructure.Persistence;

public sealed class RuntimeDbFactory
{
    private sealed record QueryTotal(int Total, DateTime CachedAt, long Generation);
    private readonly ConcurrentDictionary<string, QueryTotal> _queryTotals = new(StringComparer.Ordinal);
    private readonly object _queryCacheGate = new();
    private long _queryGeneration;
    private readonly string _queryScopeId = Guid.NewGuid().ToString("N");
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _ensured = new(StringComparer.OrdinalIgnoreCase);

    internal long QueryGeneration => Interlocked.Read(ref _queryGeneration);
    internal string QueryScopeId => _queryScopeId;

    internal bool TryGetQueryTotal(string key, long generation, TimeSpan ttl, out int total)
    {
        lock (_queryCacheGate)
        {
            if (generation == QueryGeneration
                && _queryTotals.TryGetValue(key, out var cached)
                && cached.Generation == generation
                && DateTime.UtcNow - cached.CachedAt <= ttl)
            {
                total = cached.Total;
                return true;
            }

            _queryTotals.TryRemove(key, out _);
            total = 0;
            return false;
        }
    }

    internal void CacheQueryTotal(string key, int total, long generation, TimeSpan ttl, int maximumEntries)
    {
        lock (_queryCacheGate)
        {
            if (generation != QueryGeneration) return;
            var now = DateTime.UtcNow;
            _queryTotals[key] = new QueryTotal(total, now, generation);
            if (_queryTotals.Count <= maximumEntries) return;

            foreach (var stale in _queryTotals.Where(pair => pair.Value.Generation != generation
                                                              || now - pair.Value.CachedAt > ttl)
                         .Select(pair => pair.Key).ToArray())
                _queryTotals.TryRemove(stale, out _);
            while (_queryTotals.Count > maximumEntries)
            {
                var oldest = _queryTotals.OrderBy(pair => pair.Value.CachedAt).FirstOrDefault();
                if (oldest.Key is null) break;
                _queryTotals.TryRemove(oldest.Key, out _);
            }
        }
    }

    internal void InvalidateQueryTotals()
    {
        lock (_queryCacheGate)
        {
            Interlocked.Increment(ref _queryGeneration);
            _queryTotals.Clear();
        }
    }

    public RuntimeDbFactory(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public static string MonthKey(DateTime time) => time.ToString("yyyyMM");

    /// <summary>
    /// 月库键只有 yyyyMM 一种合法形状。它会被直接拼进文件名，而明细页的月库键来自路由参数，
    /// 形状不对时一律当"库不存在"：不能让 "..\..\data_202609" 这类键拐去打开别的文件。
    /// </summary>
    public static bool IsValidMonthKey(string? monthKey)
        => monthKey is { Length: 6 } && monthKey.All(char.IsAsciiDigit);

    public static IEnumerable<string> MonthsInRange(DateTime from, DateTime to)
    {
        var cursor = new DateTime(from.Year, from.Month, 1);
        var end = new DateTime(to.Year, to.Month, 1);
        while (cursor <= end)
        {
            yield return cursor.ToString("yyyyMM");
            cursor = cursor.AddMonths(1);
        }
    }

    public string GetPath(string monthKey) => Path.Combine(_root, $"data_{monthKey}.db");

    public bool Exists(string monthKey) => IsValidMonthKey(monthKey) && File.Exists(GetPath(monthKey));

    public RuntimeDbContext Open(string monthKey)
    {
        Directory.CreateDirectory(_root);
        var path = GetPath(monthKey);
        var options = new DbContextOptionsBuilder<RuntimeDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        var ctx = new RuntimeDbContext(options);
        EnsureCreated(monthKey, ctx);
        return ctx;
    }

    public async Task ApplyPragmasAsync(RuntimeDbContext ctx, CancellationToken cancellationToken = default)
    {
        await ctx.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", cancellationToken).ConfigureAwait(false);
    }

    private void EnsureCreated(string monthKey, RuntimeDbContext ctx)
    {
        lock (_ensured)
        {
            if (_ensured.Contains(monthKey))
            {
                return;
            }
        }

        _gate.Wait();
        try
        {
            lock (_ensured)
            {
                if (_ensured.Contains(monthKey))
                {
                    return;
                }
            }

            if (MigrationBootstrap.NeedsLegacyBootstrap(ctx))
            {
                ctx.Database.EnsureCreated();
                RuntimeSchema.Upgrade(ctx);
                MigrationBootstrap.MarkReviewedRuntimeMigrationsApplied(ctx);
            }

            ctx.Database.Migrate();
            RuntimeSchema.Upgrade(ctx);
            ctx.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            ctx.Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
            RuntimeSchema.Upgrade(ctx);
            lock (_ensured)
            {
                _ensured.Add(monthKey);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<string> ListMonthKeys()
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        return Directory.GetFiles(_root, "data_*.db")
            .Select(f => Path.GetFileNameWithoutExtension(f)["data_".Length..])
            .OrderBy(x => x)
            .ToList();
    }

    public void DeleteMonth(string monthKey)
    {
        var path = GetPath(monthKey);
        // SQLite pools keep native handles after DbContext disposal. Clear only this month's
        // pool so the database and its WAL sidecars can be deleted on Windows without
        // disrupting connections to other retained months.
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            SqliteConnection.ClearPool(connection);
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = suffix == "" ? path : path + suffix;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        lock (_ensured)
        {
            _ensured.Remove(monthKey);
        }
    }
}
