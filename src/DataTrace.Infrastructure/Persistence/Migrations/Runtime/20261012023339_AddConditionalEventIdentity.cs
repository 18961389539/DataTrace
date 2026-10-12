using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataTrace.Infrastructure.Persistence.Migrations.Runtime
{
    /// <inheritdoc />
    public partial class AddConditionalEventIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConfirmationAckAddress",
                table: "PendingWriteBacks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfirmationRequestAddress",
                table: "PendingWriteBacks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DeviceEventId",
                table: "PendingWriteBacks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EventIdAddress",
                table: "PendingWriteBacks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfirmationAckAddress",
                table: "PendingWriteBacks");

            migrationBuilder.DropColumn(
                name: "ConfirmationRequestAddress",
                table: "PendingWriteBacks");

            migrationBuilder.DropColumn(
                name: "DeviceEventId",
                table: "PendingWriteBacks");

            migrationBuilder.DropColumn(
                name: "EventIdAddress",
                table: "PendingWriteBacks");
        }
    }
}
