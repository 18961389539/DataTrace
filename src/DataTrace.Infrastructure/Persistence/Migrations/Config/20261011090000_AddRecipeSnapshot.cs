using DataTrace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataTrace.Infrastructure.Persistence.Migrations.Config
{
    // 迁移类必须带 [Migration] + [DbContext] 标注，否则 EF 的 MigrationsAssembly 发现不了它：
    // 通常这两条写在 Designer.cs 里；本迁移为手写，直接标注在类上，效果相同。
    [DbContext(typeof(ConfigDbContext))]
    [Migration("20261011090000_AddRecipeSnapshot")]
    public partial class AddRecipeSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecipeSnapshot",
                table: "ActiveSessions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecipeSnapshot",
                table: "ActiveSessions");
        }
    }
}
