using System.Security.Cryptography;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Infrastructure.Identity;
using DataTrace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DataTrace.Infrastructure.Seeding;

partial class DatabaseSeeder
{
    private async Task EnsureAuditLogSchemaAsync(CancellationToken cancellationToken)
    {
        // EnsureCreated does not create tables in a non-empty legacy database.
        await _db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "AuditLogs" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AuditLogs" PRIMARY KEY AUTOINCREMENT,
                "Time" TEXT NOT NULL,
                "UserName" TEXT NOT NULL,
                "Action" TEXT NOT NULL,
                "EntityType" TEXT NOT NULL,
                "EntityKey" TEXT NULL,
                "OldValue" TEXT NULL,
                "NewValue" TEXT NULL,
                "Outcome" TEXT NOT NULL DEFAULT 'Success',
                "Source" TEXT NOT NULL DEFAULT 'Legacy / unknown',
                "SourceIp" TEXT NULL,
                "CorrelationId" TEXT NULL
            )
            """,
            cancellationToken).ConfigureAwait(false);

        var outcomeColumnMissing = !SqliteSchema.ColumnExists(_db, "AuditLogs", "Outcome");
        await SqliteSchema.AddColumnIfMissingAsync(_db, "AuditLogs", "Outcome",
            """ALTER TABLE "AuditLogs" ADD COLUMN "Outcome" TEXT NOT NULL DEFAULT 'Success'""",
            cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "AuditLogs", "Source",
            """ALTER TABLE "AuditLogs" ADD COLUMN "Source" TEXT NOT NULL DEFAULT 'Legacy / unknown'""",
            cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "AuditLogs", "SourceIp",
            """ALTER TABLE "AuditLogs" ADD COLUMN "SourceIp" TEXT NULL""",
            cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "AuditLogs", "CorrelationId",
            """ALTER TABLE "AuditLogs" ADD COLUMN "CorrelationId" TEXT NULL""",
            cancellationToken).ConfigureAwait(false);
        if (outcomeColumnMissing)
        {
            await _db.Database.ExecuteSqlRawAsync(
                """UPDATE "AuditLogs" SET "Outcome" = 'Failure' WHERE "Action" = 'LoginFailed' AND "Outcome" = 'Success'""",
                cancellationToken).ConfigureAwait(false);
        }
        await _db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_AuditLogs_Time" ON "AuditLogs" ("Time")""",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureAlarmSchemaAsync(CancellationToken cancellationToken)
    {
        // EnsureCreated 不会给已经有表的配置库补新表。报警要能跨重启留下来，这张表必须自己建。
        await _db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "AlarmIncidents" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AlarmIncidents" PRIMARY KEY AUTOINCREMENT,
                "Key" TEXT NOT NULL,
                "Kind" INTEGER NOT NULL,
                "Message" TEXT NOT NULL,
                "RaisedAt" TEXT NOT NULL,
                "AcknowledgedAt" TEXT NULL,
                "AcknowledgedBy" TEXT NULL,
                "ClearedAt" TEXT NULL
            )
            """,
            cancellationToken).ConfigureAwait(false);
        await _db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_AlarmIncidents_RaisedAt" ON "AlarmIncidents" ("RaisedAt")""",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureQualityIncidentSchemaAsync(CancellationToken cancellationToken)
    {
        await _db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "QualityIncidents" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_QualityIncidents" PRIMARY KEY AUTOINCREMENT,
                "RequestId" TEXT NOT NULL,
                "IncidentNumber" TEXT NOT NULL,
                "Title" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "Severity" INTEGER NOT NULL,
                "Status" INTEGER NOT NULL,
                "StationId" INTEGER NULL,
                "StationCode" TEXT NOT NULL,
                "RecipeCode" TEXT NOT NULL,
                "SourceAlarmId" INTEGER NULL,
                "ScopeFrom" TEXT NULL,
                "ScopeTo" TEXT NULL,
                "CreatedBy" TEXT NOT NULL,
                "Owner" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "Version" INTEGER NOT NULL
            )
            """,
            cancellationToken).ConfigureAwait(false);
        await _db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "QualityIncidentEvidence" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_QualityIncidentEvidence" PRIMARY KEY AUTOINCREMENT,
                "IncidentId" INTEGER NOT NULL,
                "MonthKey" TEXT NOT NULL,
                "RecordId" INTEGER NOT NULL,
                "Purpose" INTEGER NOT NULL,
                "SerialNo" TEXT NOT NULL,
                "PalletCode" TEXT NOT NULL,
                "StationCode" TEXT NOT NULL,
                "RecipeCode" TEXT NOT NULL,
                "TriggerTime" TEXT NOT NULL,
                "Judgement" INTEGER NOT NULL,
                "ResultCode" INTEGER NOT NULL,
                "TagSnapshotJson" TEXT NOT NULL,
                CONSTRAINT "FK_QualityIncidentEvidence_QualityIncidents_IncidentId"
                    FOREIGN KEY ("IncidentId") REFERENCES "QualityIncidents" ("Id") ON DELETE CASCADE
            )
            """,
            cancellationToken).ConfigureAwait(false);
        await _db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "QualityIncidentActivities" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_QualityIncidentActivities" PRIMARY KEY AUTOINCREMENT,
                "IncidentId" INTEGER NOT NULL,
                "Kind" INTEGER NOT NULL,
                "FromStatus" INTEGER NULL,
                "ToStatus" INTEGER NULL,
                "CauseCategory" TEXT NULL,
                "ActionTaken" TEXT NULL,
                "VerificationResult" TEXT NULL,
                "Comment" TEXT NULL,
                "Actor" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_QualityIncidentActivities_QualityIncidents_IncidentId"
                    FOREIGN KEY ("IncidentId") REFERENCES "QualityIncidents" ("Id") ON DELETE CASCADE
            )
            """,
            cancellationToken).ConfigureAwait(false);
        await _db.Database.ExecuteSqlRawAsync(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_QualityIncidents_IncidentNumber"
                ON "QualityIncidents" ("IncidentNumber");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_QualityIncidents_RequestId"
                ON "QualityIncidents" ("RequestId");
            CREATE INDEX IF NOT EXISTS "IX_QualityIncidents_Status_UpdatedAt"
                ON "QualityIncidents" ("Status", "UpdatedAt");
            CREATE INDEX IF NOT EXISTS "IX_QualityIncidents_Owner_Status"
                ON "QualityIncidents" ("Owner", "Status");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_QualityIncidentEvidence_IncidentId_MonthKey_RecordId"
                ON "QualityIncidentEvidence" ("IncidentId", "MonthKey", "RecordId");
            CREATE INDEX IF NOT EXISTS "IX_QualityIncidentEvidence_MonthKey_RecordId"
                ON "QualityIncidentEvidence" ("MonthKey", "RecordId");
            CREATE INDEX IF NOT EXISTS "IX_QualityIncidentActivities_IncidentId_CreatedAt"
                ON "QualityIncidentActivities" ("IncidentId", "CreatedAt");
            """,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 点位不再有编码。老库把空名称补成原来的编码，重名的加上后缀，然后删掉 Code 列。
    /// 新库由模型直接建出 (工站, 名称) 唯一索引，这里看到没有 Code 列就跳过。
    /// </summary>
    private async Task RemoveTagCodesAsync(CancellationToken cancellationToken)
    {
        if (!SqliteSchema.ColumnExists(_db, "Tags", "Code"))
        {
            return;
        }

        var rows = new List<(int Id, int StationId, string Name, string Code)>();
        var connection = _db.Database.GetDbConnection();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """SELECT "Id", "StationId", "Name", "Code" FROM "Tags" ORDER BY "Id" """;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3)));
            }
        }

        var seen = new HashSet<(int StationId, string Name)>();
        foreach (var row in rows)
        {
            var name = string.IsNullOrWhiteSpace(row.Name)
                ? (string.IsNullOrWhiteSpace(row.Code) ? $"点位{row.Id}" : row.Code.Trim())
                : row.Name.Trim();
            if (!seen.Add((row.StationId, name.ToLowerInvariant())))
            {
                var suffix = string.IsNullOrWhiteSpace(row.Code) ? row.Id.ToString() : row.Code.Trim();
                var n = 2;
                var candidate = $"{name} ({suffix})";
                while (!seen.Add((row.StationId, candidate.ToLowerInvariant())))
                {
                    candidate = $"{name} ({suffix}-{n++})";
                }

                name = candidate;
            }

            if (string.Equals(name, row.Name, StringComparison.Ordinal))
            {
                continue;
            }

            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "Tags" SET "Name" = {name} WHERE "Id" = {row.Id}""",
                cancellationToken).ConfigureAwait(false);
        }

        SqliteSchema.DropColumnIfPresent(_db, "Tags", "Code");
        await _db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Tags_StationId_Name" ON "Tags" ("StationId", "Name")""",
            cancellationToken).ConfigureAwait(false);
    }
}
