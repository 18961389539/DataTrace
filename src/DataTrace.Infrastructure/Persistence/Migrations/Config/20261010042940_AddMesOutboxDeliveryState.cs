using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataTrace.Infrastructure.Persistence.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddMesOutboxDeliveryState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_MesOutbox_Status_CreatedAt\"");

            migrationBuilder.AddColumn<string>(
                name: "EventId",
                table: "MesOutbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                table: "MesOutbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MesOutbox_EventId",
                table: "MesOutbox",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MesOutbox_Status_NextAttemptAt_CreatedAt",
                table: "MesOutbox",
                columns: new[] { "Status", "NextAttemptAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_MesOutbox_EventId\"");

            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_MesOutbox_Status_NextAttemptAt_CreatedAt\"");

            migrationBuilder.DropColumn(
                name: "EventId",
                table: "MesOutbox");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "MesOutbox");

            migrationBuilder.CreateIndex(
                name: "IX_MesOutbox_Status_CreatedAt",
                table: "MesOutbox",
                columns: new[] { "Status", "CreatedAt" });
        }
    }
}
