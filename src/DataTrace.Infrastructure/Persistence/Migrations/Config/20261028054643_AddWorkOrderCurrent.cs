using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataTrace.Infrastructure.Persistence.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddWorkOrderCurrent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsCurrent",
                table: "WorkOrders",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsCurrent",
                table: "WorkOrders");
        }
    }
}
