using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

/// <summary>
/// 月份库的结构版本。打开某个 <c>data_yyyyMM.db</c> 时只跑还没跑过的补丁。
/// </summary>
/// <remarks>
/// EF 的 <c>EnsureCreated</c> 不会改已有文件。新列如果只写在模型里，上个月的库一读就
/// <c>no such column</c>。每个补丁必须可以重复执行：SQLite 的 DDL 不能和后面的版本号
/// 写在同一个可回滚事务里，进程若在补丁中途退出，下次仍从旧版本重跑这一档。
/// </remarks>
public static class RuntimeSchema
{
    public const int CurrentVersion = 1;

    public static void Upgrade(RuntimeDbContext ctx)
    {
        EnsureVersionTable(ctx);
        var version = ReadVersion(ctx);
        if (version < 1)
        {
            ApplyVersion1(ctx);
            WriteVersion(ctx, 1);
        }
    }

    private static void EnsureVersionTable(RuntimeDbContext ctx)
    {
        ctx.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "RuntimeSchemaVersion" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_RuntimeSchemaVersion" PRIMARY KEY,
                "Version" INTEGER NOT NULL
            );
            """);
    }

    private static int ReadVersion(RuntimeDbContext ctx)
    {
        var connection = ctx.Database.GetDbConnection();
        ctx.Database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """SELECT "Version" FROM "RuntimeSchemaVersion" WHERE "Id" = 1""";
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    private static void WriteVersion(RuntimeDbContext ctx, int version)
    {
        var connection = ctx.Database.GetDbConnection();
        ctx.Database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "RuntimeSchemaVersion" ("Id", "Version") VALUES (1, $v)
            ON CONFLICT("Id") DO UPDATE SET "Version" = $v
            """;
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "$v";
        parameter.Value = version;
        cmd.Parameters.Add(parameter);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 把历史上散落的补列收成第一档：曲线特征表、偏离列、点位限值、归档引用，以及去掉点位编码列。
    /// </summary>
    private static void ApplyVersion1(RuntimeDbContext ctx)
    {
        EnsureCurveFeatureTable(ctx);
        EnsureCurveFeatureColumns(ctx);
        EnsureTagValueColumns(ctx);
    }

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

    private static void EnsureTagValueColumns(RuntimeDbContext ctx)
    {
        SqliteSchema.AddColumnIfMissing(
            ctx,
            "TagValues",
            "IsWarning",
            """ALTER TABLE "TagValues" ADD COLUMN "IsWarning" INTEGER NOT NULL DEFAULT 0""");

        SqliteSchema.AddColumnIfMissing(
            ctx,
            "CollectRecords",
            "RecipeCode",
            """ALTER TABLE "CollectRecords" ADD COLUMN "RecipeCode" TEXT NOT NULL DEFAULT ''""");

        ctx.Database.ExecuteSqlRaw(
            """CREATE INDEX IF NOT EXISTS "IX_CollectRecords_RecipeCode" ON "CollectRecords" ("RecipeCode");""");

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

        SqliteSchema.AddColumnIfMissing(
            ctx,
            "CollectRecords",
            "ArchivePath",
            """ALTER TABLE "CollectRecords" ADD COLUMN "ArchivePath" TEXT NOT NULL DEFAULT ''""");

        SqliteSchema.AddColumnIfMissing(
            ctx,
            "CollectRecords",
            "ArchiveFileSize",
            """ALTER TABLE "CollectRecords" ADD COLUMN "ArchiveFileSize" INTEGER NOT NULL DEFAULT 0""");

        SqliteSchema.AddColumnIfMissing(
            ctx,
            "CollectRecords",
            "ArchiveCrc32",
            """ALTER TABLE "CollectRecords" ADD COLUMN "ArchiveCrc32" INTEGER NOT NULL DEFAULT 0""");

        if (SqliteSchema.ColumnExists(ctx, "TagValues", "TagCode"))
        {
            ctx.Database.ExecuteSqlRaw(
                """UPDATE "TagValues" SET "TagName" = "TagCode" WHERE "TagName" IS NULL OR trim("TagName") = ''""");
            SqliteSchema.DropColumnIfPresent(ctx, "TagValues", "TagCode");
        }
    }
}
