using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataTrace.Infrastructure.Persistence.Migrations.Runtime
{
    // 迁移类必须带 [Migration] + [DbContext] 标注，否则 EF 的 MigrationsAssembly 发现不了它
    // （通常这两条写在 Designer.cs 里；本迁移为手写，直接标注在类上，效果相同）。
    [DbContext(typeof(RuntimeDbContext))]
    [Migration("20261011153000_AddSessionIndexIntentsAndPendingWriteBacks")]
    public partial class AddSessionIndexIntentsAndPendingWriteBacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 与 RuntimeSchema v3 的补表保持幂等：走 legacy bootstrap 的老库在迁移运行前
            // 就被补丁建过表，这里用 IF NOT EXISTS 保证两种来源都不会撞 duplicate table。
            // 列形状与 RuntimeSchema.ApplyVersion3 逐列一致。
            migrationBuilder.Sql("""
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

            migrationBuilder.Sql(
                """CREATE INDEX IF NOT EXISTS "IX_SessionIndexIntents_AppliedAt_CreatedAt" ON "SessionIndexIntents" ("AppliedAt", "CreatedAt");""");

            migrationBuilder.Sql("""
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

            migrationBuilder.Sql(
                """CREATE INDEX IF NOT EXISTS "IX_PendingWriteBacks_StationId_ConfirmedAt" ON "PendingWriteBacks" ("StationId", "ConfirmedAt");""");

            migrationBuilder.Sql(
                """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PendingWriteBacks_SerialNo_StationId" ON "PendingWriteBacks" ("SerialNo", "StationId");""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TABLE IF EXISTS "PendingWriteBacks";""");
            migrationBuilder.Sql("""DROP TABLE IF EXISTS "SessionIndexIntents";""");
        }
    }
}
