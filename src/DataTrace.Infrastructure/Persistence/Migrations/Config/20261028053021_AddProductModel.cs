using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataTrace.Infrastructure.Persistence.Migrations.Config
{
    /// <inheritdoc />
    public partial class AddProductModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "WorkOrders",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "Recipes",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            // 旧库里的 Recipes.Code 就是业务型号（A100 这类）；内部编码 SYS-* 不当作型号。
            migrationBuilder.Sql(
                """UPDATE "Recipes" SET "Model" = "Code" WHERE "Model" IS NULL AND "Code" NOT LIKE 'SYS-%'""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Model",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "Recipes");
        }
    }
}
