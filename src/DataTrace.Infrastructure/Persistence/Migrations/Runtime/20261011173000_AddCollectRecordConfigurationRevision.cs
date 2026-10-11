using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataTrace.Infrastructure.Persistence.Migrations.Runtime;

[DbContext(typeof(RuntimeDbContext))]
[Migration("20261011173000_AddCollectRecordConfigurationRevision")]
public sealed class AddCollectRecordConfigurationRevision : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(name: "ConfigurationRevisionId", table: "CollectRecords", type: "INTEGER", nullable: true);
        migrationBuilder.CreateIndex(name: "IX_CollectRecords_ConfigurationRevisionId", table: "CollectRecords", column: "ConfigurationRevisionId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_CollectRecords_ConfigurationRevisionId", table: "CollectRecords");
        migrationBuilder.DropColumn(name: "ConfigurationRevisionId", table: "CollectRecords");
    }
}
