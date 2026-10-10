using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DataTrace.Infrastructure.Persistence;

public sealed class ConfigDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ConfigDbContext>
{
    public ConfigDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ConfigDbContext>()
            .UseSqlite("Data Source=datatrace-config-design.db")
            .Options;
        return new ConfigDbContext(options);
    }
}

public sealed class RuntimeDbContextDesignTimeFactory : IDesignTimeDbContextFactory<RuntimeDbContext>
{
    public RuntimeDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RuntimeDbContext>()
            .UseSqlite("Data Source=datatrace-runtime-design.db")
            .Options;
        return new RuntimeDbContext(options);
    }
}