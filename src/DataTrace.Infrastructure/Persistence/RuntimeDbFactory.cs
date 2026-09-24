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
            EnsureCurveFeatureTable(ctx);
            EnsureCurveFeatureColumns(ctx);
            EnsureTagValueColumns(ctx);
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

    /// <summary>
    /// 已存在的月份库不会被 EnsureCreated 补表，所以这里用 IF NOT EXISTS 显式建特征表。
    /// 列定义与 EF 约定一致：枚举落 INTEGER、double 落 REAL、string 落 TEXT。
    ///</summary>
    private static void EnsureCurveFeatureTable(RuntimeDbContext ctx)
    {
        ctx.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "CurveFeatures" (
                "Id"            INTEGER NOT NULL CONSTRAINT "PK_CurveFeatures" PRIMARY KEY AUTOINCREMENT,
                "CurveRecordId" INTEGER NOT NULL,
                "SeriesName"    TEXT    NOT NULL,
                "Role"          INTEGER NOT NULL,
                "PointCount"    INTEGER NOT NULL,
                "Min"           REAL    NOT NULL,
                "MinIndex"      INTEGER NOT NULL,
                "Peak"          REAL    NOT NULL,
                "PeakIndex"     INTEGER NOT NULL,
                "Mean"          REAL    NOT NULL,
                "StdDev"        REAL    NOT NULL,
                "Area"          REAL    NOT NULL,
                "RiseSlope"     REAL    NOT NULL,
                "HoldSlope"     REAL    NOT NULL,
                "RiseIndex"     INTEGER NOT NULL,
                "RiseSpan"      INTEGER NOT NULL,
                "FallRatio"     REAL    NOT NULL,
                "MaxStep"       REAL    NOT NULL,
                "Oscillations"  INTEGER NOT NULL,
                CONSTRAINT "FK_CurveFeatures_CurveRecords_CurveRecordId"
                    FOREIGN KEY ("CurveRecordId") REFERENCES "CurveRecords" ("Id") ON DELETE CASCADE
            );
            """);
        ctx.Database.ExecuteSqlRaw(
            """CREATE INDEX IF NOT EXISTS "IX_CurveFeatures_CurveRecordId" ON "CurveFeatures" ("CurveRecordId");""");
        ctx.Database.ExecuteSqlRaw(
            """CREATE INDEX IF NOT EXISTS "IX_CurveFeatures_Role_SeriesName" ON "CurveFeatures" ("Role", "SeriesName");""");
    }

    /// <summary>
    /// 给已存在的月份库补曲线特征的偏离列。
    /// 这几列是影子模式的产出（只记录、不判定），老库补上默认为 NULL / 0 即可，
    /// 不需要回填历史 —— 没有基线时的"未比对"本身就是正确状态。
    /// </summary>
    private static void EnsureCurveFeatureColumns(RuntimeDbContext ctx)
    {
        SqliteSchema.AddColumnIfMissing(
            ctx,
            "CurveFeatures",
            "DeviationRmsZ",
            """ALTER TABLE "CurveFeatures" ADD COLUMN "DeviationRmsZ" REAL NULL""");

        SqliteSchema.AddColumnIfMissing(
            ctx,
            "CurveFeatures",
            "DeviationVerdict",
            """ALTER TABLE "CurveFeatures" ADD COLUMN "DeviationVerdict" INTEGER NULL""");

        SqliteSchema.AddColumnIfMissing(
            ctx,
            "CurveFeatures",
            "DeviationWorstDimension",
            """ALTER TABLE "CurveFeatures" ADD COLUMN "DeviationWorstDimension" INTEGER NULL""");

        SqliteSchema.AddColumnIfMissing(
            ctx,
            "CurveFeatures",
            "BaselineSampleCount",
            """ALTER TABLE "CurveFeatures" ADD COLUMN "BaselineSampleCount" INTEGER NOT NULL DEFAULT 0""");
    }

    /// <summary>给已存在的月份库补新增列（老库缺列会直接报 no such column）。</summary>
    private static void EnsureTagValueColumns(RuntimeDbContext ctx)
    {
        SqliteSchema.AddColumnIfMissing(
            ctx,
            "TagValues",
            "IsWarning",
            """ALTER TABLE "TagValues" ADD COLUMN "IsWarning" INTEGER NOT NULL DEFAULT 0""");

        // 判定所用的型号编码：限值随型号切换而变，不记下来事后无法解释"当时为什么判废"。
        SqliteSchema.AddColumnIfMissing(
            ctx,
            "CollectRecords",
            "RecipeCode",
            """ALTER TABLE "CollectRecords" ADD COLUMN "RecipeCode" TEXT NOT NULL DEFAULT ''""");

        // 查询/报表按型号过滤与分组；IF NOT EXISTS 对已有月库幂等安全。
        ctx.Database.ExecuteSqlRaw(
            """CREATE INDEX IF NOT EXISTS "IX_CollectRecords_RecipeCode" ON "CollectRecords" ("RecipeCode");""");

        // 判定那一刻生效的四道限值。用 REAL NULL 且不回填：老记录要么确实没配限值，
        // 要么是那会儿还没记，两者都不能凭空补 0——补 0 会让历史明细显示"规格限 0"。
        SqliteSchema.AddColumnIfMissing(
            ctx,
            "TagValues",
            "LowerLimit",
            """ALTER TABLE "TagValues" ADD COLUMN "LowerLimit" REAL NULL""");

        SqliteSchema.AddColumnIfMissing(
            ctx,
            "TagValues",
            "UpperLimit",
            """ALTER TABLE "TagValues" ADD COLUMN "UpperLimit" REAL NULL""");

        SqliteSchema.AddColumnIfMissing(
            ctx,
            "TagValues",
            "WarningLowerLimit",
            """ALTER TABLE "TagValues" ADD COLUMN "WarningLowerLimit" REAL NULL""");

        SqliteSchema.AddColumnIfMissing(
            ctx,
            "TagValues",
            "WarningUpperLimit",
            """ALTER TABLE "TagValues" ADD COLUMN "WarningUpperLimit" REAL NULL""");
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
