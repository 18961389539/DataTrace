using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure.Identity;
using DataTrace.Infrastructure.Persistence;
using DataTrace.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Tests;

public class DatabaseSeederTests
{
    [Fact]
    public async Task Fresh_config_database_is_created_by_ef_migrations()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        var db = context.Scope.ServiceProvider.GetRequiredService<ConfigDbContext>();

        Assert.Contains(
            await db.Database.GetAppliedMigrationsAsync(),
            migration => migration.EndsWith("_InitialConfigSchema", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Production_seed_clears_legacy_first_login_password_change_flags()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        using (var scope = context.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            var admin = await db.Users.SingleAsync(user => user.UserName == "admin");
            admin.MustChangePassword = true;
            await db.SaveChangesAsync();
        }

        using (var scope = context.Provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>()
                .SeedAsync(SeedUserOptions.Production);
        }

        using (var scope = context.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            var admin = await db.Users.SingleAsync(user => user.UserName == "admin");
            Assert.False(admin.MustChangePassword);
        }
    }

    [Fact]
    public async Task Op130_demo_station_uses_the_requested_int16_register_map()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        var db = context.Scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        var station = await db.Stations
            .Include(item => item.Tags)
            .Include(item => item.Curves)
            .SingleAsync(item => item.Code == "OP130");

        Assert.Equal("点胶外观检测", station.Name);
        Assert.True(station.IsFirstStation);
        Assert.Equal("MW3002", station.TriggerAddress);
        Assert.Equal("MW3001", station.PalletCodeAddress);
        Assert.Equal(PlcDataType.Int16, station.PalletCodeDataType);

        var expected = new Dictionary<string, string>
        {
            ["数据1_配方号"] = "MW3000",
            ["托盘号"] = "MW3001"
        };
        for (var result = 1; result <= 17; result++)
        {
            expected[$"3D结果{result}"] = $"MW{3003 + result}";
        }

        var actual = station.Tags.ToDictionary(tag => tag.Name, tag => tag.Address);
        Assert.Equal(
            expected.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
            actual.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());
        Assert.All(station.Tags, tag => Assert.Equal(PlcDataType.Int16, tag.DataType));
        Assert.Empty(station.Curves);
    }

    [Fact]
    public async Task Op140_demo_station_uses_the_requested_int16_register_map()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        var db = context.Scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        var station = await db.Stations
            .Include(item => item.Tags)
            .Include(item => item.Curves)
            .SingleAsync(item => item.Code == "OP140");

        Assert.Equal("开关功能检测", station.Name);
        Assert.Equal(20, station.Sequence);
        Assert.False(station.IsFirstStation);
        Assert.False(station.IsLastStation);
        Assert.Equal("MW3102", station.TriggerAddress);
        Assert.Equal("MW3101", station.PalletCodeAddress);
        Assert.Equal(PlcDataType.Int16, station.PalletCodeDataType);

        var expected = new Dictionary<string, string>
        {
            ["配方号"] = "MW3100",
            ["托盘号"] = "MW3101"
        };
        for (var product = 0; product <= 8; product++)
        {
            expected[$"产品{product}结果"] = $"MW{3103 + product}";
        }

        var actual = station.Tags.ToDictionary(tag => tag.Name, tag => tag.Address);
        Assert.Equal(
            expected.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
            actual.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());
        Assert.All(station.Tags, tag => Assert.Equal(PlcDataType.Int16, tag.DataType));
        Assert.Empty(station.Curves);
    }

    [Fact]
    public async Task Op150_demo_station_uses_the_requested_int16_register_map()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        var db = context.Scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        var station = await db.Stations
            .Include(item => item.Tags)
            .Include(item => item.Curves)
            .SingleAsync(item => item.Code == "OP150");

        Assert.Equal("位置度检测(左侧)", station.Name);
        Assert.Equal(30, station.Sequence);
        Assert.False(station.IsFirstStation);
        Assert.False(station.IsLastStation);
        Assert.Equal("MW3202", station.TriggerAddress);
        Assert.Equal("MW3201", station.PalletCodeAddress);
        Assert.Equal(PlcDataType.Int16, station.PalletCodeDataType);

        var expected = new Dictionary<string, string>
        {
            ["配方号"] = "MW3200",
            ["托盘号"] = "MW3201"
        };
        for (var product = 1; product <= 4; product++)
        {
            expected[$"产品{product}拍基准结果"] = $"MW{3203 + product}";
            expected[$"产品{product}拍PIN结果"] = $"MW{3207 + product}";
        }

        var actual = station.Tags.ToDictionary(tag => tag.Name, tag => tag.Address);
        Assert.Equal(
            expected.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
            actual.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());
        Assert.All(station.Tags, tag => Assert.Equal(PlcDataType.Int16, tag.DataType));
        Assert.Empty(station.Curves);
    }

    [Fact]
    public async Task Op20_demo_station_uses_the_requested_int16_register_map_and_real_curves()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        var db = context.Scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        var station = await db.Stations
            .Include(item => item.Tags)
            .Include(item => item.Curves)
                .ThenInclude(curve => curve.Series)
            .SingleAsync(item => item.Code == "OP20");

        Assert.Equal("开关上料压装", station.Name);
        Assert.Equal(50, station.Sequence);
        Assert.False(station.IsFirstStation);
        Assert.False(station.IsLastStation);
        Assert.Equal("MW2302", station.TriggerAddress);
        Assert.Equal("MW2301", station.PalletCodeAddress);
        Assert.Equal(PlcDataType.Int16, station.PalletCodeDataType);

        var expected = new Dictionary<string, string>
        {
            ["配方号"] = "MW2300",
            ["托盘号"] = "MW2301"
        };
        for (var press = 1; press <= 4; press++)
        {
            expected[$"压装{press}结果"] = $"MW{2303 + press}";
            expected[$"压装{press}压力值"] = $"MW{2307 + press}";
            expected[$"压装{press}位移值"] = $"MW{2311 + press}";
        }

        var actual = station.Tags.ToDictionary(tag => tag.Name, tag => tag.Address);
        Assert.Equal(
            expected.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
            actual.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());
        Assert.All(station.Tags, tag => Assert.Equal(PlcDataType.Int16, tag.DataType));

        var curve = Assert.Single(station.Curves);
        Assert.Equal("OP20_PD", curve.Code);
        Assert.Equal(30, curve.PointCount);
        var series = curve.Series.ToDictionary(item => item.Name);
        Assert.Equal(SeriesRole.X, series["曲线位移值"].Role);
        Assert.Equal("MD1700", series["曲线位移值"].StartAddress);
        Assert.Equal(PlcDataType.Float, series["曲线位移值"].DataType);
        Assert.Equal(2, series["曲线位移值"].StrideWords);
        Assert.Equal(SeriesRole.Y, series["曲线压力值"].Role);
        Assert.Equal("%MD1730", series["曲线压力值"].StartAddress);
        Assert.Equal(PlcDataType.Float, series["曲线压力值"].DataType);
        Assert.Equal(2, series["曲线压力值"].StrideWords);
    }

    [Fact]
    public async Task Op30_demo_station_uses_the_requested_int16_register_map_and_real_curves()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        var db = context.Scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        var station = await db.Stations
            .Include(item => item.Tags)
            .Include(item => item.Curves)
                .ThenInclude(curve => curve.Series)
            .SingleAsync(item => item.Code == "OP30");

        Assert.Equal("开关上料压装", station.Name);
        Assert.Equal(60, station.Sequence);
        Assert.False(station.IsFirstStation);
        Assert.False(station.IsLastStation);
        Assert.Equal("MW2402", station.TriggerAddress);
        Assert.Equal("MW2401", station.PalletCodeAddress);
        Assert.Equal(PlcDataType.Int16, station.PalletCodeDataType);

        var expected = new Dictionary<string, string>
        {
            ["配方号"] = "MW2400",
            ["托盘号"] = "MW2401"
        };
        for (var press = 1; press <= 4; press++)
        {
            expected[$"压装{press}结果"] = $"MW{2403 + press}";
            expected[$"压装{press}压力值"] = $"MW{2407 + press}";
            expected[$"压装{press}位移值"] = $"MW{2411 + press}";
        }

        var actual = station.Tags.ToDictionary(tag => tag.Name, tag => tag.Address);
        Assert.Equal(
            expected.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
            actual.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());
        Assert.All(station.Tags, tag => Assert.Equal(PlcDataType.Int16, tag.DataType));

        var curve = Assert.Single(station.Curves);
        Assert.Equal("OP30_PD", curve.Code);
        Assert.Equal(30, curve.PointCount);
        var series = curve.Series.ToDictionary(item => item.Name);
        Assert.Equal(SeriesRole.X, series["曲线位移值"].Role);
        Assert.Equal("MD1800", series["曲线位移值"].StartAddress);
        Assert.Equal(PlcDataType.Float, series["曲线位移值"].DataType);
        Assert.Equal(2, series["曲线位移值"].StrideWords);
        Assert.Equal(SeriesRole.Y, series["曲线压力值"].Role);
        Assert.Equal("MD1830", series["曲线压力值"].StartAddress);
        Assert.Equal(PlcDataType.Float, series["曲线压力值"].DataType);
        Assert.Equal(2, series["曲线压力值"].StrideWords);
    }

    [Fact]
    public async Task Op40_demo_station_uses_the_requested_int16_register_map_and_real_curves()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        var db = context.Scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        var station = await db.Stations
            .Include(item => item.Tags)
            .Include(item => item.Curves)
                .ThenInclude(curve => curve.Series)
            .SingleAsync(item => item.Code == "OP40");

        Assert.Equal("开关上料压装", station.Name);
        Assert.Equal(80, station.Sequence);
        Assert.False(station.IsFirstStation);
        Assert.False(station.IsLastStation);
        Assert.Equal("MW2502", station.TriggerAddress);
        Assert.Equal("MW2501", station.PalletCodeAddress);
        Assert.Equal(PlcDataType.Int16, station.PalletCodeDataType);

        var expected = new Dictionary<string, string>
        {
            ["配方号"] = "MW2500",
            ["托盘号"] = "MW2501"
        };
        for (var press = 1; press <= 4; press++)
        {
            expected[$"压装{press}结果"] = $"MW{2503 + press}";
            expected[$"压装{press}压力值"] = $"MW{2507 + press}";
            expected[$"压装{press}位移值"] = $"MW{2511 + press}";
        }

        var actual = station.Tags.ToDictionary(tag => tag.Name, tag => tag.Address);
        Assert.Equal(
            expected.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
            actual.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());
        Assert.All(station.Tags, tag => Assert.Equal(PlcDataType.Int16, tag.DataType));

        var curve = Assert.Single(station.Curves);
        Assert.Equal("OP40_PD", curve.Code);
        Assert.Equal(30, curve.PointCount);
        var series = curve.Series.ToDictionary(item => item.Name);
        Assert.Equal(SeriesRole.X, series["曲线位移值"].Role);
        Assert.Equal("MD1900", series["曲线位移值"].StartAddress);
        Assert.Equal(PlcDataType.Float, series["曲线位移值"].DataType);
        Assert.Equal(2, series["曲线位移值"].StrideWords);
        Assert.Equal(SeriesRole.Y, series["曲线压力值"].Role);
        Assert.Equal("MD1930", series["曲线压力值"].StartAddress);
        Assert.Equal(PlcDataType.Float, series["曲线压力值"].DataType);
        Assert.Equal(2, series["曲线压力值"].StrideWords);
    }

    [Fact]
    public async Task Op50_demo_station_uses_the_requested_int16_register_map_and_real_curves()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        var db = context.Scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        var station = await db.Stations
            .Include(item => item.Tags)
            .Include(item => item.Curves)
                .ThenInclude(curve => curve.Series)
            .SingleAsync(item => item.Code == "OP50");

        Assert.Equal("开关上料压装", station.Name);
        Assert.Equal(90, station.Sequence);
        Assert.False(station.IsFirstStation);
        Assert.False(station.IsLastStation);
        Assert.Equal("MW2602", station.TriggerAddress);
        Assert.Equal("MW2601", station.PalletCodeAddress);
        Assert.Equal(PlcDataType.Int16, station.PalletCodeDataType);

        var expected = new Dictionary<string, string>
        {
            ["配方号"] = "MW2600",
            ["托盘号"] = "MW2601"
        };
        for (var press = 1; press <= 4; press++)
        {
            expected[$"压装{press}结果"] = $"MW{2603 + press}";
            expected[$"压装{press}压力值"] = $"MW{2607 + press}";
            expected[$"压装{press}位移值"] = $"MW{2611 + press}";
        }

        var actual = station.Tags.ToDictionary(tag => tag.Name, tag => tag.Address);
        Assert.Equal(
            expected.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
            actual.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());
        Assert.All(station.Tags, tag => Assert.Equal(PlcDataType.Int16, tag.DataType));

        var curve = Assert.Single(station.Curves);
        Assert.Equal("OP50_PD", curve.Code);
        Assert.Equal(30, curve.PointCount);
        var series = curve.Series.ToDictionary(item => item.Name);
        Assert.Equal(SeriesRole.X, series["曲线位移值"].Role);
        Assert.Equal("MD2000", series["曲线位移值"].StartAddress);
        Assert.Equal(PlcDataType.Float, series["曲线位移值"].DataType);
        Assert.Equal(2, series["曲线位移值"].StrideWords);
        Assert.Equal(SeriesRole.Y, series["曲线压力值"].Role);
        Assert.Equal("MD2030", series["曲线压力值"].StartAddress);
        Assert.Equal(PlcDataType.Float, series["曲线压力值"].DataType);
        Assert.Equal(2, series["曲线压力值"].StrideWords);
    }

    [Fact]
    public async Task Op70_demo_station_uses_the_requested_int16_register_map()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        var db = context.Scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        var station = await db.Stations
            .Include(item => item.Tags)
            .Include(item => item.Curves)
            .SingleAsync(item => item.Code == "OP70");

        Assert.Equal("开关压装检测", station.Name);
        Assert.Equal(100, station.Sequence);
        Assert.False(station.IsFirstStation);
        Assert.True(station.IsLastStation);
        Assert.Equal("MW2702", station.TriggerAddress);
        Assert.Equal("MW2701", station.PalletCodeAddress);
        Assert.Equal(PlcDataType.Int16, station.PalletCodeDataType);

        var expected = new Dictionary<string, string>
        {
            ["配方号"] = "MW2700",
            ["托盘号"] = "MW2701"
        };
        for (var result = 1; result <= 16; result++)
        {
            expected[$"3D结果{result}"] = $"MW{2703 + result}";
        }

        var actual = station.Tags.ToDictionary(tag => tag.Name, tag => tag.Address);
        Assert.Equal(
            expected.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
            actual.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());
        Assert.All(station.Tags, tag => Assert.Equal(PlcDataType.Int16, tag.DataType));
        Assert.Empty(station.Curves);
    }

    [Fact]
    public async Task Mes_outbox_migration_accepts_legacy_database_without_previous_queue_index()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        using (var scope = context.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"__EFMigrationsHistory\"");
            await ConfigMigrationTestSupport.RewindMesOutboxDeliveryStateAsync(db);
            await db.Database.ExecuteSqlRawAsync("DROP INDEX \"IX_MesOutbox_Status_CreatedAt\"");
        }

        using (var scope = context.Provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
        }

        using (var scope = context.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            var columns = await db.Database
                .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('MesOutbox')")
                .ToListAsync();
            Assert.Contains("EventId", columns);
            Assert.Contains("NextAttemptAt", columns);
            Assert.Contains(
                await db.Database.GetAppliedMigrationsAsync(),
                migration => migration.EndsWith("_AddMesOutboxDeliveryState", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Quality_incident_tables_are_recreated_for_an_existing_config_database()
    {
        await using var context = await InfrastructureContext.CreateAsync();

        using (var scope = context.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"__EFMigrationsHistory\"");
            await ConfigMigrationTestSupport.RewindMesOutboxDeliveryStateAsync(db);
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
    public async Task ExistingDemoSimulatorAddsOp40Op50AndOp70OnlyOnce()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        int versionBefore;

        using (var scope = context.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            // ST040 故意留下：升级种子应把它永久删掉，而不是再种回来。
            var addedStations = await db.Stations
                .Where(station => station.Code == "OP40"
                                  || station.Code == "OP50"
                                  || station.Code == "OP70")
                .ToListAsync();
            db.Stations.RemoveRange(addedStations);

            var firstStation = await db.Stations
                .Include(station => station.Tags)
                .Include(station => station.Curves)
                .SingleAsync(station => station.Code == "OP130");
            firstStation.Code = "ST010";
            firstStation.Name = "上料工站";
            firstStation.TriggerAddress = "D1000";
            firstStation.PalletCodeAddress = "D1010";
            firstStation.PalletCodeLength = 16;
            firstStation.PalletCodeDataType = PlcDataType.String;
            db.Tags.RemoveRange(firstStation.Tags);
            firstStation.Tags.Clear();
            firstStation.Tags.Add(new TagDefinition
            {
                Name = "工站温度", Address = "D1110", DataType = PlcDataType.Float, PositionIndex = 1
            });
            firstStation.Tags.Add(new TagDefinition
            {
                Name = "压力", Address = "D1100", DataType = PlcDataType.Float, PositionIndex = 1
            });
            db.Curves.RemoveRange(firstStation.Curves);
            firstStation.Curves.Clear();
            firstStation.Curves.Add(new CurveDefinition
            {
                Code = "ST010_PD",
                Name = "位移压力曲线",
                PointCount = 50,
                PositionIndex = 1,
                Enabled = true,
                Series =
                [
                    new CurveSeries { Name = "压力", Role = SeriesRole.Y, StartAddress = "D2200", DataType = PlcDataType.Float, StrideWords = 2 },
                    new CurveSeries { Name = "位移", Role = SeriesRole.X, StartAddress = "D2400", DataType = PlcDataType.Float, StrideWords = 2 }
                ]
            });

            var secondStation = await db.Stations
                .Include(station => station.Tags)
                .Include(station => station.Curves)
                .SingleAsync(station => station.Code == "OP140");
            secondStation.Code = "ST020";
            secondStation.Name = "压装工站";
            secondStation.TriggerAddress = "D1200";
            secondStation.PalletCodeAddress = "D1210";
            secondStation.PalletCodeLength = 16;
            secondStation.PalletCodeDataType = PlcDataType.String;
            db.Tags.RemoveRange(secondStation.Tags);
            secondStation.Tags.Clear();
            secondStation.Tags.Add(new TagDefinition
            {
                Name = "工站温度", Address = "D1310", DataType = PlcDataType.Float, PositionIndex = 1
            });
            secondStation.Tags.Add(new TagDefinition
            {
                Name = "压力", Address = "D1300", DataType = PlcDataType.Float, PositionIndex = 1
            });
            db.Curves.RemoveRange(secondStation.Curves);
            secondStation.Curves.Clear();
            secondStation.Curves.Add(new CurveDefinition
            {
                Code = "ST020_PD",
                Name = "位移压力曲线",
                PointCount = 50,
                PositionIndex = 1,
                Enabled = true,
                Series =
                [
                    new CurveSeries { Name = "压力", Role = SeriesRole.Y, StartAddress = "D3600", DataType = PlcDataType.Float, StrideWords = 2 },
                    new CurveSeries { Name = "位移", Role = SeriesRole.X, StartAddress = "D4000", DataType = PlcDataType.Float, StrideWords = 2 }
                ]
            });

            var thirdStation = await db.Stations
                .Include(station => station.Tags)
                .Include(station => station.Curves)
                .SingleAsync(station => station.Code == "OP150");
            thirdStation.Code = "ST030";
            thirdStation.Name = "下线工站";
            thirdStation.TriggerAddress = "D1400";
            thirdStation.PalletCodeAddress = "D1410";
            thirdStation.PalletCodeLength = 16;
            thirdStation.PalletCodeDataType = PlcDataType.String;
            thirdStation.IsLastStation = true;
            db.Tags.RemoveRange(thirdStation.Tags);
            thirdStation.Tags.Clear();
            thirdStation.Tags.Add(new TagDefinition
            {
                Name = "工站温度", Address = "D1510", DataType = PlcDataType.Float, PositionIndex = 1
            });
            thirdStation.Tags.Add(new TagDefinition
            {
                Name = "压力", Address = "D1500", DataType = PlcDataType.Float, PositionIndex = 1
            });
            db.Curves.RemoveRange(thirdStation.Curves);
            thirdStation.Curves.Clear();
            thirdStation.Curves.Add(new CurveDefinition
            {
                Code = "ST030_PD",
                Name = "位移压力曲线",
                PointCount = 50,
                PositionIndex = 1,
                Enabled = true,
                Series =
                [
                    new CurveSeries { Name = "压力", Role = SeriesRole.Y, StartAddress = "D5200", DataType = PlcDataType.Float, StrideWords = 2 },
                    new CurveSeries { Name = "位移", Role = SeriesRole.X, StartAddress = "D5600", DataType = PlcDataType.Float, StrideWords = 2 }
                ]
            });

            var fifthStation = await db.Stations
                .Include(station => station.Tags)
                .Include(station => station.Curves)
                .SingleAsync(station => station.Code == "OP20");
            fifthStation.Code = "ST050";
            fifthStation.Name = "性能检测工站";
            fifthStation.TriggerAddress = "D1800";
            fifthStation.PalletCodeAddress = "D1810";
            fifthStation.PalletCodeLength = 16;
            fifthStation.PalletCodeDataType = PlcDataType.String;
            db.Tags.RemoveRange(fifthStation.Tags);
            fifthStation.Tags.Clear();
            fifthStation.Tags.Add(new TagDefinition
            {
                Name = "工站温度", Address = "D1910", DataType = PlcDataType.Float, PositionIndex = 1
            });
            fifthStation.Tags.Add(new TagDefinition
            {
                Name = "压力", Address = "D1900", DataType = PlcDataType.Float, PositionIndex = 1
            });
            db.Curves.RemoveRange(fifthStation.Curves);
            fifthStation.Curves.Clear();
            fifthStation.Curves.Add(new CurveDefinition
            {
                Code = "ST050_PD",
                Name = "位移压力曲线",
                PointCount = 50,
                PositionIndex = 1,
                Enabled = true,
                Series =
                [
                    new CurveSeries { Name = "压力", Role = SeriesRole.Y, StartAddress = "D8400", DataType = PlcDataType.Float, StrideWords = 2 },
                    new CurveSeries { Name = "位移", Role = SeriesRole.X, StartAddress = "D8800", DataType = PlcDataType.Float, StrideWords = 2 }
                ]
            });

            var sixthStation = await db.Stations
                .Include(station => station.Tags)
                .Include(station => station.Curves)
                .SingleAsync(station => station.Code == "OP30");
            sixthStation.Code = "ST060";
            sixthStation.Name = "终检工站";
            sixthStation.TriggerAddress = "D2000";
            sixthStation.PalletCodeAddress = "D2010";
            sixthStation.PalletCodeLength = 16;
            sixthStation.PalletCodeDataType = PlcDataType.String;
            db.Tags.RemoveRange(sixthStation.Tags);
            sixthStation.Tags.Clear();
            sixthStation.Tags.Add(new TagDefinition
            {
                Name = "工站温度", Address = "D2110", DataType = PlcDataType.Float, PositionIndex = 1
            });
            sixthStation.Tags.Add(new TagDefinition
            {
                Name = "压力", Address = "D2100", DataType = PlcDataType.Float, PositionIndex = 1
            });
            db.Curves.RemoveRange(sixthStation.Curves);
            sixthStation.Curves.Clear();
            sixthStation.Curves.Add(new CurveDefinition
            {
                Code = "ST060_PD",
                Name = "位移压力曲线",
                PointCount = 50,
                PositionIndex = 1,
                Enabled = true,
                Series =
                [
                    new CurveSeries { Name = "压力", Role = SeriesRole.Y, StartAddress = "D10000", DataType = PlcDataType.Float, StrideWords = 2 },
                    new CurveSeries { Name = "位移", Role = SeriesRole.X, StartAddress = "D10400", DataType = PlcDataType.Float, StrideWords = 2 }
                ]
            });

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
                new[] { "OP10", "OP130", "OP140", "OP150", "OP20", "OP30", "OP40", "OP50", "OP60", "OP70", "OP80", "OP90" },
                stations.Select(station => station.Code).ToArray());
            Assert.DoesNotContain(stations, station => station.Code == "ST040");
            Assert.Equal("D2100", stations.Single(station => station.Code == "OP10").TriggerAddress);
            Assert.Equal("MW3002", stations.Single(station => station.Code == "OP130").TriggerAddress);
            Assert.Equal("MW3102", stations.Single(station => station.Code == "OP140").TriggerAddress);
            Assert.Equal("MW3202", stations.Single(station => station.Code == "OP150").TriggerAddress);
            Assert.Equal("MW2302", stations.Single(station => station.Code == "OP20").TriggerAddress);
            Assert.Equal("MW2402", stations.Single(station => station.Code == "OP30").TriggerAddress);
            Assert.Equal("MW2502", stations.Single(station => station.Code == "OP40").TriggerAddress);
            Assert.Equal("MW2602", stations.Single(station => station.Code == "OP50").TriggerAddress);
            Assert.Equal("MW2702", stations.Single(station => station.Code == "OP70").TriggerAddress);
            Assert.False(stations.Single(station => station.Code == "OP150").IsLastStation);
            Assert.False(stations.Single(station => station.Code == "OP30").IsLastStation);
            Assert.False(stations.Single(station => station.Code == "OP40").IsLastStation);
            Assert.False(stations.Single(station => station.Code == "OP50").IsLastStation);
            Assert.False(stations.Single(station => station.Code == "OP70").IsLastStation);
            Assert.True(stations.Single(station => station.Code == "OP90").IsLastStation);
            versionAfterUpgrade = await db.ConfigVersions.Select(version => version.Version).SingleAsync();
        }

        using (var scope = context.Provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
        }

        using (var scope = context.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            Assert.Equal(12, await db.Stations.CountAsync());
            Assert.Equal(0, await db.Stations.CountAsync(station => station.Code == "ST040"));
            Assert.Equal(versionAfterUpgrade, await db.ConfigVersions.Select(version => version.Version).SingleAsync());
        }

        // EnsureDemoStations / DemoAlarms 等可能各 bump 一次，只要求升级有进展且第二次 Seed 幂等。
        Assert.True(versionAfterUpgrade > versionBefore);
    }
}
