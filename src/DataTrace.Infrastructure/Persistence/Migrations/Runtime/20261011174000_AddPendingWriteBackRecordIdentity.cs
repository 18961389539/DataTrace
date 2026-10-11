using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataTrace.Infrastructure.Persistence.Migrations.Runtime;

[DbContext(typeof(RuntimeDbContext))]
[Migration("20261011174000_AddPendingWriteBackRecordIdentity")]
public sealed class AddPendingWriteBackRecordIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(name: "CollectRecordId", table: "PendingWriteBacks", type: "INTEGER", nullable: true);
        migrationBuilder.DropIndex(name: "IX_PendingWriteBacks_SerialNo_StationId", table: "PendingWriteBacks");
        migrationBuilder.CreateIndex(name: "IX_PendingWriteBacks_CollectRecordId", table: "PendingWriteBacks", column: "CollectRecordId", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_PendingWriteBacks_CollectRecordId", table: "PendingWriteBacks");
        migrationBuilder.DropColumn(name: "CollectRecordId", table: "PendingWriteBacks");
        migrationBuilder.CreateIndex(name: "IX_PendingWriteBacks_SerialNo_StationId", table: "PendingWriteBacks", columns: new[] { "SerialNo", "StationId" }, unique: true);
    }
}
