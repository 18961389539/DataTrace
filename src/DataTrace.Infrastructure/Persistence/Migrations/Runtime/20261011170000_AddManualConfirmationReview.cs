using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataTrace.Infrastructure.Persistence.Migrations.Runtime;

[DbContext(typeof(RuntimeDbContext))]
[Migration("20261011170000_AddManualConfirmationReview")]
public sealed class AddManualConfirmationReview : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(name: "ManualReviewRequired", table: "PendingWriteBacks", type: "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<DateTime>(name: "ManualReviewRequiredAt", table: "PendingWriteBacks", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "ManualReviewReason", table: "PendingWriteBacks", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<DateTime>(name: "ManualReviewResolvedAt", table: "PendingWriteBacks", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "ManualReviewResolvedBy", table: "PendingWriteBacks", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "ManualReviewResolution", table: "PendingWriteBacks", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "ManualReviewDeviceStateEvidence", table: "PendingWriteBacks", type: "TEXT", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ManualReviewRequired", table: "PendingWriteBacks");
        migrationBuilder.DropColumn(name: "ManualReviewRequiredAt", table: "PendingWriteBacks");
        migrationBuilder.DropColumn(name: "ManualReviewReason", table: "PendingWriteBacks");
        migrationBuilder.DropColumn(name: "ManualReviewResolvedAt", table: "PendingWriteBacks");
        migrationBuilder.DropColumn(name: "ManualReviewResolvedBy", table: "PendingWriteBacks");
        migrationBuilder.DropColumn(name: "ManualReviewResolution", table: "PendingWriteBacks");
        migrationBuilder.DropColumn(name: "ManualReviewDeviceStateEvidence", table: "PendingWriteBacks");
    }
}
