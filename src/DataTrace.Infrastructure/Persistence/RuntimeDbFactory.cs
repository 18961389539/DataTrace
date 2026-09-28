using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

public sealed class RuntimeDbFactory
{
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _ensured = new(StringComparer.OrdinalIgnoreCase);

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

            ctx.Database.EnsureCreated();
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
