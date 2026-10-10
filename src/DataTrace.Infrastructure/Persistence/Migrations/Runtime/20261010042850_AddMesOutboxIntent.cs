using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataTrace.Infrastructure.Persistence.Migrations.Runtime
{
    /// <inheritdoc />
    public partial class AddMesOutboxIntent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MesOutboxIntents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    MonthKey = table.Column<string>(type: "TEXT", nullable: false),
                    PalletSessionId = table.Column<long>(type: "INTEGER", nullable: false),
                    SerialNo = table.Column<string>(type: "TEXT", nullable: false),
                    PalletCode = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MesOutboxIntents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MesOutboxIntents_EventId",
                table: "MesOutboxIntents",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MesOutboxIntents_PublishedAt_CreatedAt",
                table: "MesOutboxIntents",
                columns: new[] { "PublishedAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MesOutboxIntents");
        }
    }
}
