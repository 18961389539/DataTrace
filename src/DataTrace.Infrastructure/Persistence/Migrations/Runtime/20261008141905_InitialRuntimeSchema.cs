using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataTrace.Infrastructure.Persistence.Migrations.Runtime
{
    /// <inheritdoc />
    public partial class InitialRuntimeSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PalletSessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SerialNo = table.Column<string>(type: "TEXT", nullable: false),
                    PalletCode = table.Column<string>(type: "TEXT", nullable: false),
                    StartTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Judgement = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstNgStationCode = table.Column<string>(type: "TEXT", nullable: true),
                    FirstNgAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FirstNgResultCode = table.Column<short>(type: "INTEGER", nullable: true),
                    FirstNgReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PalletSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CollectRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PalletSessionId = table.Column<long>(type: "INTEGER", nullable: false),
                    SerialNo = table.Column<string>(type: "TEXT", nullable: false),
                    PalletCode = table.Column<string>(type: "TEXT", nullable: false),
                    StationId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationCode = table.Column<string>(type: "TEXT", nullable: false),
                    TriggerTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompleteTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultCode = table.Column<short>(type: "INTEGER", nullable: false),
                    Judgement = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    RecipeCode = table.Column<string>(type: "TEXT", nullable: false),
                    ArchivePath = table.Column<string>(type: "TEXT", nullable: false),
                    ArchiveFileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    ArchiveCrc32 = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CollectRecords_PalletSessions_PalletSessionId",
                        column: x => x.PalletSessionId,
                        principalTable: "PalletSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CurveRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CollectRecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    CurveDefinitionId = table.Column<int>(type: "INTEGER", nullable: false),
                    CurveCode = table.Column<string>(type: "TEXT", nullable: false),
                    CurveName = table.Column<string>(type: "TEXT", nullable: false),
                    PositionIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    PointCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    Crc32 = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurveRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CurveRecords_CollectRecords_CollectRecordId",
                        column: x => x.CollectRecordId,
                        principalTable: "CollectRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CollectRecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    PositionIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Occupied = table.Column<bool>(type: "INTEGER", nullable: false),
                    Judgement = table.Column<int>(type: "INTEGER", nullable: false),
                    NgReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductRecords_CollectRecords_CollectRecordId",
                        column: x => x.CollectRecordId,
                        principalTable: "CollectRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TagValues",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CollectRecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false),
                    TagName = table.Column<string>(type: "TEXT", nullable: false),
                    PositionIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    DataType = table.Column<int>(type: "INTEGER", nullable: false),
                    NumericValue = table.Column<double>(type: "REAL", nullable: true),
                    TextValue = table.Column<string>(type: "TEXT", nullable: true),
                    IsOutOfLimit = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsWarning = table.Column<bool>(type: "INTEGER", nullable: false),
                    LowerLimit = table.Column<double>(type: "REAL", nullable: true),
                    UpperLimit = table.Column<double>(type: "REAL", nullable: true),
                    WarningLowerLimit = table.Column<double>(type: "REAL", nullable: true),
                    WarningUpperLimit = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TagValues_CollectRecords_CollectRecordId",
                        column: x => x.CollectRecordId,
                        principalTable: "CollectRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CurveFeatures",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CurveRecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    SeriesName = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    PointCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Min = table.Column<double>(type: "REAL", nullable: false),
                    MinIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Peak = table.Column<double>(type: "REAL", nullable: false),
                    PeakIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Mean = table.Column<double>(type: "REAL", nullable: false),
                    StdDev = table.Column<double>(type: "REAL", nullable: false),
                    Area = table.Column<double>(type: "REAL", nullable: false),
                    RiseSlope = table.Column<double>(type: "REAL", nullable: false),
                    HoldSlope = table.Column<double>(type: "REAL", nullable: false),
                    RiseIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    RiseSpan = table.Column<int>(type: "INTEGER", nullable: false),
                    FallRatio = table.Column<double>(type: "REAL", nullable: false),
                    MaxStep = table.Column<double>(type: "REAL", nullable: false),
                    Oscillations = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviationRmsZ = table.Column<double>(type: "REAL", nullable: true),
                    DeviationVerdict = table.Column<int>(type: "INTEGER", nullable: true),
                    DeviationWorstDimension = table.Column<int>(type: "INTEGER", nullable: true),
                    BaselineSampleCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurveFeatures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CurveFeatures_CurveRecords_CurveRecordId",
                        column: x => x.CurveRecordId,
                        principalTable: "CurveRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CollectRecords_PalletCode_TriggerTime",
                table: "CollectRecords",
                columns: new[] { "PalletCode", "TriggerTime" });

            migrationBuilder.CreateIndex(
                name: "IX_CollectRecords_PalletSessionId",
                table: "CollectRecords",
                column: "PalletSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectRecords_SerialNo",
                table: "CollectRecords",
                column: "SerialNo");

            migrationBuilder.CreateIndex(
                name: "IX_CollectRecords_StationId_TriggerTime",
                table: "CollectRecords",
                columns: new[] { "StationId", "TriggerTime" });

            migrationBuilder.CreateIndex(
                name: "IX_CollectRecords_TriggerTime",
                table: "CollectRecords",
                column: "TriggerTime");

            migrationBuilder.CreateIndex(
                name: "IX_CurveFeatures_CurveRecordId",
                table: "CurveFeatures",
                column: "CurveRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_CurveFeatures_Role_SeriesName",
                table: "CurveFeatures",
                columns: new[] { "Role", "SeriesName" });

            migrationBuilder.CreateIndex(
                name: "IX_CurveRecords_CollectRecordId",
                table: "CurveRecords",
                column: "CollectRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_PalletSessions_PalletCode_StartTime",
                table: "PalletSessions",
                columns: new[] { "PalletCode", "StartTime" });

            migrationBuilder.CreateIndex(
                name: "IX_PalletSessions_SerialNo",
                table: "PalletSessions",
                column: "SerialNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductRecords_CollectRecordId",
                table: "ProductRecords",
                column: "CollectRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_TagValues_CollectRecordId",
                table: "TagValues",
                column: "CollectRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_TagValues_TagId_NumericValue",
                table: "TagValues",
                columns: new[] { "TagId", "NumericValue" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CurveFeatures");

            migrationBuilder.DropTable(
                name: "ProductRecords");

            migrationBuilder.DropTable(
                name: "TagValues");

            migrationBuilder.DropTable(
                name: "CurveRecords");

            migrationBuilder.DropTable(
                name: "CollectRecords");

            migrationBuilder.DropTable(
                name: "PalletSessions");
        }
    }
}
