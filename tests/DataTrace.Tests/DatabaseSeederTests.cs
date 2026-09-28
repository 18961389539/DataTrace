using DataTrace.Infrastructure.Persistence;
using DataTrace.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Tests;

public class DatabaseSeederTests
{
    [Fact]
    public async Task ExistingDemoSimulatorIsExpandedToSixStationsOnlyOnce()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        int versionBefore;

        using (var scope = context.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            var addedStations = await db.Stations
                .Where(station => station.Code == "ST040"
                                  || station.Code == "ST050"
                                  || station.Code == "ST060")
                .ToListAsync();
            db.Stations.RemoveRange(addedStations);
            (await db.Stations.SingleAsync(station => station.Code == "ST030")).IsLastStation = true;
            await db.SaveChangesAsync();
            versionBefore = await db.ConfigVersions.Select(version => version.Version).SingleAsync();
        }

        using (var scope = context.Provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
        }

        int versionAfterUpgrade;
        using (var scope = context.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            var stations = await db.Stations.OrderBy(station => station.Sequence).ToListAsync();

            Assert.Equal(
                new[] { "ST010", "ST020", "ST030", "ST040", "ST050", "ST060" },
                stations.Select(station => station.Code).ToArray());
            Assert.False(stations.Single(station => station.Code == "ST030").IsLastStation);
            Assert.True(stations.Single(station => station.Code == "ST060").IsLastStation);
            versionAfterUpgrade = await db.ConfigVersions.Select(version => version.Version).SingleAsync();
        }

        using (var scope = context.Provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
        }

        using (var scope = context.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            Assert.Equal(6, await db.Stations.CountAsync());
            Assert.Equal(versionAfterUpgrade, await db.ConfigVersions.Select(version => version.Version).SingleAsync());
        }

        Assert.Equal(versionBefore + 1, versionAfterUpgrade);
    }
}
