using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataTrace.Infrastructure.Persistence.Migrations.Runtime;

[DbContext(typeof(RuntimeDbContext))]
[Migration("20261011171000_AddSessionRecipeSnapshot")]
public sealed class AddSessionRecipeSnapshot : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(name: "RecipeSnapshot", table: "PalletSessions", type: "TEXT", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "RecipeSnapshot", table: "PalletSessions");
}
