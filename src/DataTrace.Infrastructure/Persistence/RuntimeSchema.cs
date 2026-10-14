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
    public const int CurrentVersion = 12;

    public static void Upgrade(RuntimeDbContext ctx)
    {
        EnsureVersionTable(ctx);
        var version = ReadVersion(ctx);
        if (version < 1)
        {
            ApplyVersion1(ctx);
            WriteVersion(ctx, 1);
        }

        if (version < 2)
        {
            ApplyVersion2(ctx);
            WriteVersion(ctx, 2);
        }

        if (version < 3)
        {
            ApplyVersion3(ctx);
            WriteVersion(ctx, 3);
        }

        if (version < 4)
        {
            ApplyVersion4(ctx);
            WriteVersion(ctx, 4);
        }

        if (version < 5)
        {
            SqliteSchema.AddColumnIfMissing(ctx, "PalletSessions", "RecipeSnapshot", """ALTER TABLE "PalletSessions" ADD COLUMN "RecipeSnapshot" TEXT NULL""");
            WriteVersion(ctx, 5);
        }

        if (version < 6)
        {
            SqliteSchema.AddColumnIfMissing(ctx, "PendingWriteBacks", "PlcConnectionId", """ALTER TABLE "PendingWriteBacks" ADD COLUMN "PlcConnectionId" INTEGER NULL""");
            SqliteSchema.AddColumnIfMissing(ctx, "PendingWriteBacks", "TriggerAddress", """ALTER TABLE "PendingWriteBacks" ADD COLUMN "TriggerAddress" TEXT NULL""");
            SqliteSchema.AddColumnIfMissing(ctx, "PendingWriteBacks", "TriggerValue", """ALTER TABLE "PendingWriteBacks" ADD COLUMN "TriggerValue" INTEGER NULL""");
            SqliteSchema.AddColumnIfMissing(ctx, "PendingWriteBacks", "ConnectionFingerprint", """ALTER TABLE "PendingWriteBacks" ADD COLUMN "ConnectionFingerprint" TEXT NULL""");
            WriteVersion(ctx, 6);
        }

        if (version < 7)
        {
            SqliteSchema.AddColumnIfMissing(ctx, "CollectRecords", "ConfigurationRevisionId", """ALTER TABLE "CollectRecords" ADD COLUMN "ConfigurationRevisionId" INTEGER NULL""");
            ctx.Database.ExecuteSqlRaw("""CREATE INDEX IF NOT EXISTS "IX_CollectRecords_ConfigurationRevisionId" ON "CollectRecords" ("ConfigurationRevisionId")""");
            WriteVersion(ctx, 7);
        }

        if (version < 8)
        {
            SqliteSchema.AddColumnIfMissing(ctx, "PendingWriteBacks", "CollectRecordId", """ALTER TABLE "PendingWriteBacks" ADD COLUMN "CollectRecordId" INTEGER NULL""");
            ctx.Database.ExecuteSqlRaw("DROP INDEX IF EXISTS \"IX_PendingWriteBacks_SerialNo_StationId\"");
            ctx.Database.ExecuteSqlRaw("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_PendingWriteBacks_CollectRecordId" ON "PendingWriteBacks" ("CollectRecordId")""");
            WriteVersion(ctx, 8);
        }
        if (version < 9)
        {
            SqliteSchema.AddColumnIfMissing(ctx, "PendingWriteBacks", "DeviceEventId", "ALTER TABLE PendingWriteBacks ADD COLUMN DeviceEventId INTEGER NULL");
            foreach (var column in new[] { "EventIdAddress", "ConfirmationRequestAddress", "ConfirmationAckAddress" })
                SqliteSchema.AddColumnIfMissing(ctx, "PendingWriteBacks", column, $"ALTER TABLE PendingWriteBacks ADD COLUMN {column} TEXT NULL");
            WriteVersion(ctx, 9);
        }
        if (version < 10)
        {
            ctx.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "MesOutboxIntents" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_MesOutboxIntents" PRIMARY KEY AUTOINCREMENT,
                    "EventId" TEXT NOT NULL,
                    "MonthKey" TEXT NOT NULL,
                    "PalletSessionId" INTEGER NOT NULL,
                    "SerialNo" TEXT NOT NULL,
                    "PalletCode" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "PublishedAt" TEXT NULL,
                    "PayloadJson" TEXT NOT NULL
                );
                """);
            ctx.Database.ExecuteSqlRaw("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_MesOutboxIntents_EventId" ON "MesOutboxIntents" ("EventId");""");
            ctx.Database.ExecuteSqlRaw("""CREATE INDEX IF NOT EXISTS "IX_MesOutboxIntents_PublishedAt_CreatedAt" ON "MesOutboxIntents" ("PublishedAt", "CreatedAt");""");
            WriteVersion(ctx, 10);
        }

        if (version < 11)
        {
            SqliteSchema.AddColumnIfMissing(
                ctx,
                "TagValues",
                "UnitSnapshot",
                """ALTER TABLE "TagValues" ADD COLUMN "UnitSnapshot" TEXT NULL""");
            WriteVersion(ctx, 11);
        }

        if (version < 12)
        {
            SqliteSchema.AddColumnIfMissing(
                ctx,
                "TagValues",
                "PurposeSnapshot",
                """ALTER TABLE "TagValues" ADD COLUMN "PurposeSnapshot" INTEGER NULL""");
            WriteVersion(ctx, 12);
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

    /// <summary>
    /// 会话首因：判定为 NG 的会话要能说清"哪一站先出问题"。
    /// 新库由 EnsureCreated 直接从模型建出这几列，老库靠这一档补齐。
    /// </summary>
    private static void ApplyVersion2(RuntimeDbContext ctx)
    {
        SqliteSchema.AddColumnIfMissing(
            ctx,
            "PalletSessions",
            "FirstNgStationCode",
            """ALTER TABLE "PalletSessions" ADD COLUMN "FirstNgStationCode" TEXT NULL""");

        SqliteSchema.AddColumnIfMissing(
            ctx,
            "PalletSessions",
            "FirstNgAt",
            """ALTER TABLE "PalletSessions" ADD COLUMN "FirstNgAt" TEXT NULL""");

        SqliteSchema.AddColumnIfMissing(
            ctx,
            "PalletSessions",
            "FirstNgResultCode",
            """ALTER TABLE "PalletSessions" ADD COLUMN "FirstNgResultCode" INTEGER NULL""");

        SqliteSchema.AddColumnIfMissing(
            ctx,
            "PalletSessions",
            "FirstNgReason",
            """ALTER TABLE "PalletSessions" ADD COLUMN "FirstNgReason" TEXT NULL""");
    }

    /// <summary>
    /// 在制索引意图表：月库提交与 config.db ActiveSessions 更新之间的硬退出窗口，
    /// 由同事务写入的意图在重启后对账重放（仿 MES Outbox）。
    /// 待确认回写表：回写失败的事件留证，触发命中时只重试确认、不重新采集（R01 采集幂等）。
    /// </summary>
    private static void ApplyVersion3(RuntimeDbContext ctx)
    {
        ctx.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "SessionIndexIntents" (
                "Id"          INTEGER NOT NULL CONSTRAINT "PK_SessionIndexIntents" PRIMARY KEY AUTOINCREMENT,
                "PalletCode"  TEXT    NOT NULL,
                "SerialNo"    TEXT    NOT NULL,
                "MonthKey"    TEXT    NOT NULL,
                "Operation"   INTEGER NOT NULL,
                "PayloadJson" TEXT    NOT NULL DEFAULT '',
                "CreatedAt"   TEXT    NOT NULL,
                "AppliedAt"   TEXT    NULL
            );
            """);
        ctx.Database.ExecuteSqlRaw(
            """CREATE INDEX IF NOT EXISTS "IX_SessionIndexIntents_AppliedAt_CreatedAt" ON "SessionIndexIntents" ("AppliedAt", "CreatedAt");""");

        ctx.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "PendingWriteBacks" (
                "Id"              INTEGER NOT NULL CONSTRAINT "PK_PendingWriteBacks" PRIMARY KEY AUTOINCREMENT,
                "PalletCode"      TEXT    NOT NULL,
                "SerialNo"        TEXT    NOT NULL,
                "StationId"       INTEGER NOT NULL,
                "StationCode"     TEXT    NOT NULL DEFAULT '',
                "MonthKey"        TEXT    NOT NULL,
                "PalletSessionId" INTEGER NOT NULL DEFAULT 0,
                "ResultCode"      INTEGER NOT NULL,
                "CreatedAt"       TEXT    NOT NULL,
                "ConfirmedAt"     TEXT    NULL,
                "Attempts"        INTEGER NOT NULL DEFAULT 0,
                "NextAttemptAt"   TEXT    NOT NULL
            );
            """);
        ctx.Database.ExecuteSqlRaw(
            """CREATE INDEX IF NOT EXISTS "IX_PendingWriteBacks_StationId_ConfirmedAt" ON "PendingWriteBacks" ("StationId", "ConfirmedAt");""");
        ctx.Database.ExecuteSqlRaw(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PendingWriteBacks_SerialNo_StationId" ON "PendingWriteBacks" ("SerialNo", "StationId");""");
    }

    private static void ApplyVersion4(RuntimeDbContext ctx)
    {
        SqliteSchema.AddColumnIfMissing(ctx, "PendingWriteBacks", "ManualReviewRequired", """ALTER TABLE "PendingWriteBacks" ADD COLUMN "ManualReviewRequired" INTEGER NOT NULL DEFAULT 0""");
        SqliteSchema.AddColumnIfMissing(ctx, "PendingWriteBacks", "ManualReviewRequiredAt", """ALTER TABLE "PendingWriteBacks" ADD COLUMN "ManualReviewRequiredAt" TEXT NULL""");
        SqliteSchema.AddColumnIfMissing(ctx, "PendingWriteBacks", "ManualReviewReason", """ALTER TABLE "PendingWriteBacks" ADD COLUMN "ManualReviewReason" TEXT NULL""");
        SqliteSchema.AddColumnIfMissing(ctx, "PendingWriteBacks", "ManualReviewResolvedAt", """ALTER TABLE "PendingWriteBacks" ADD COLUMN "ManualReviewResolvedAt" TEXT NULL""");
        SqliteSchema.AddColumnIfMissing(ctx, "PendingWriteBacks", "ManualReviewResolvedBy", """ALTER TABLE "PendingWriteBacks" ADD COLUMN "ManualReviewResolvedBy" TEXT NULL""");
        SqliteSchema.AddColumnIfMissing(ctx, "PendingWriteBacks", "ManualReviewResolution", """ALTER TABLE "PendingWriteBacks" ADD COLUMN "ManualReviewResolution" TEXT NULL""");
        SqliteSchema.AddColumnIfMissing(ctx, "PendingWriteBacks", "ManualReviewDeviceStateEvidence", """ALTER TABLE "PendingWriteBacks" ADD COLUMN "ManualReviewDeviceStateEvidence" TEXT NULL""");
    }
}
