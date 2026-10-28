using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataTrace.Infrastructure.Persistence.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddWorkOrderCurrentSince : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CurrentSince",
                table: "WorkOrders",
                type: "TEXT",
                nullable: true);

            // 已有“当前工单”没有起点：从升级时刻开始估算，避免历史产量被整段算进来。
            migrationBuilder.Sql(
                """UPDATE "WorkOrders" SET "CurrentSince" = datetime('now', 'localtime') WHERE "IsCurrent" = 1""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrentSince",
                table: "WorkOrders");
        }
    }
}
