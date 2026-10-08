using DataTrace.Domain.Entities;
using DataTrace.Infrastructure.Persistence;
using DataTrace.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Tests;

public class DatabaseSeederTests
{
    [Fact]
    public async Task Quality_incident_tables_are_recreated_for_an_existing_config_database()
    {
        await using var context = await InfrastructureContext.CreateAsync();

        using (var scope = context.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"QualityIncidentActivities\"");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"QualityIncidentEvidence\"");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"QualityIncidents\"");
        }

        using (var scope = context.Provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
        }

        using (var scope = context.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            db.QualityIncidents.Add(new QualityIncident
            {
                RequestId = Guid.NewGuid(),
                IncidentNumber = "QI-20261008-0001",
                Title = "压力超限",
                Description = "重启后核查旧库补表",
                CreatedBy = "engineer",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            });
            await db.SaveChangesAsync();
        }

        using (var scope = context.Provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
        }

        using (var scope = context.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            Assert.Equal("QI-20261008-0001", (await db.QualityIncidents.SingleAsync()).IncidentNumber);
            Assert.Empty(await db.QualityIncidentEvidence.ToListAsync());
            Assert.Empty(await db.QualityIncidentActivities.ToListAsync());
        }
    }

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
