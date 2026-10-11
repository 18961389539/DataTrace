using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataTrace.Infrastructure.Persistence.Migrations.Runtime;

[DbContext(typeof(RuntimeDbContext))]
[Migration("20261011172000_AddPendingConfirmationTarget")]
public sealed class AddPendingConfirmationTarget : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "PlcConnectionId", table: "PendingWriteBacks", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<string>(name: "TriggerAddress", table: "PendingWriteBacks", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<short>(name: "TriggerValue", table: "PendingWriteBacks", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<string>(name: "ConnectionFingerprint", table: "PendingWriteBacks", type: "TEXT", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PlcConnectionId", table: "PendingWriteBacks");
        migrationBuilder.DropColumn(name: "TriggerAddress", table: "PendingWriteBacks");
        migrationBuilder.DropColumn(name: "TriggerValue", table: "PendingWriteBacks");
        migrationBuilder.DropColumn(name: "ConnectionFingerprint", table: "PendingWriteBacks");
    }
}
