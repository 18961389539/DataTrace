using DataTrace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataTrace.Infrastructure.Persistence.Migrations.Config;

[DbContext(typeof(ConfigDbContext))]
[Migration("20261011180000_AddConfigurationRevisions")]
public sealed class AddConfigurationRevisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ConfigurationRevisions",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                ConfigVersion = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UserName = table.Column<string>(type: "TEXT", nullable: false),
                Reason = table.Column<string>(type: "TEXT", nullable: false),
                Action = table.Column<string>(type: "TEXT", nullable: false),
                EntityType = table.Column<string>(type: "TEXT", nullable: false),
                EntityKey = table.Column<string>(type: "TEXT", nullable: true),
                ValidationResult = table.Column<string>(type: "TEXT", nullable: false),
                ParentRevisionId = table.Column<long>(type: "INTEGER", nullable: true),
                SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                DiffJson = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ConfigurationRevisions", x => x.Id));

        migrationBuilder.CreateIndex("IX_ConfigurationRevisions_CreatedAt", "ConfigurationRevisions", "CreatedAt");
        migrationBuilder.CreateIndex("IX_ConfigurationRevisions_ParentRevisionId", "ConfigurationRevisions", "ParentRevisionId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable("ConfigurationRevisions");
}
