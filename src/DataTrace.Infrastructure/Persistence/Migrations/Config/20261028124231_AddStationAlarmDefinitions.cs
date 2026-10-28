using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataTrace.Infrastructure.Persistence.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddStationAlarmDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StationAlarms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StationId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TagDefinitionId = table.Column<int>(type: "INTEGER", nullable: true),
                    Compare = table.Column<int>(type: "INTEGER", nullable: false),
                    Limit = table.Column<double>(type: "REAL", nullable: true),
                    SampleIntervalMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StationAlarms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StationAlarms_Stations_StationId",
                        column: x => x.StationId,
                        principalTable: "Stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StationAlarms_Tags_TagDefinitionId",
                        column: x => x.TagDefinitionId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StationAlarms_StationId_Name",
                table: "StationAlarms",
                columns: new[] { "StationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StationAlarms_TagDefinitionId",
                table: "StationAlarms",
                column: "TagDefinitionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StationAlarms");
        }
    }
}
