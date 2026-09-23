using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Tests;

/// <summary>按月分库工厂：月键、范围展开、建库与整月删除。</summary>
public class RuntimeDbFactoryTests
{
    [Fact]
    public void Month_key_formats_as_yyyyMM()
        => Assert.Equal("202609", RuntimeDbFactory.MonthKey(new DateTime(2026, 9, 19, 23, 59, 59)));

    [Fact]
    public void Months_in_range_expands_across_year_boundary()
    {
        var months = RuntimeDbFactory.MonthsInRange(new DateTime(2025, 11, 15), new DateTime(2026, 2, 3)).ToList();
        Assert.Equal(new[] { "202511", "202512", "202601", "202602" }, months);
    }

    [Fact]
    public void Months_in_range_collapses_single_month()
    {
        var months = RuntimeDbFactory.MonthsInRange(new DateTime(2026, 3, 1), new DateTime(2026, 3, 31)).ToList();
        Assert.Equal(new[] { "202603" }, months);
    }

    [Fact]
    public void Months_in_range_returns_nothing_when_from_after_to()
    {
        // 起点晚于终点时不抛异常也不臆造月份，直接返回空集（UI 显示空报表）。
        var months = RuntimeDbFactory.MonthsInRange(new DateTime(2026, 5, 1), new DateTime(2026, 4, 1)).ToList();
        Assert.Empty(months);
    }

    [Fact]
    public async Task Open_creates_month_database_and_is_idempotent()
    {
        using var workspace = new TempWorkspace();
        var factory = new RuntimeDbFactory(workspace.Path("runtime"));

        Assert.False(factory.Exists("202609"));
        await using (var db = factory.Open("202609"))
        {
            Assert.Empty(db.CollectRecords);
        }

        Assert.True(factory.Exists("202609"));
        Assert.Equal(workspace.Path("runtime", "data_202609.db"), factory.GetPath("202609"));

        // 二次打开不应重复建表或抛错。
        await using (var db = factory.Open("202609"))
        {
            Assert.Empty(db.PalletSessions);
        }
    }

    [Fact]
    public async Task List_month_keys_is_sorted_and_ignores_unrelated_files()
    {
        using var workspace = new TempWorkspace();
        var factory = new RuntimeDbFactory(workspace.Path("runtime"));
        await using (var db = factory.Open("202601")) { _ = db; }
        await using (var db = factory.Open("202512")) { _ = db; }
        await File.WriteAllTextAsync(workspace.Path("runtime", "readme.txt"), "x");
        await File.WriteAllTextAsync(workspace.Path("runtime", "other.db"), "x");

        Assert.Equal(new[] { "202512", "202601" }, factory.ListMonthKeys());
    }

    [Fact]
    public async Task Delete_month_removes_database_and_allows_recreate()
    {
        using var workspace = new TempWorkspace();
        var factory = new RuntimeDbFactory(workspace.Path("runtime"));
        await using (var db = factory.Open("202604")) { _ = db; }
        Assert.True(factory.Exists("202604"));

        // Microsoft.Data.Sqlite 默认开启连接池，池中空闲连接仍握着 .db 文件句柄，
        // 会直接让 File.Delete 抛 IOException；这里先清空池以模拟"进程内未打开过该月库"的场景。
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        factory.DeleteMonth("202604");
        Assert.False(factory.Exists("202604"));
        Assert.DoesNotContain("202604", factory.ListMonthKeys());

        // 删除后再次打开视为新库，不残留数据。
        await using var recreated = factory.Open("202604");
        Assert.Empty(recreated.CollectRecords);
    }

    [Fact]
    public void Delete_month_on_missing_database_is_noop()
    {
        using var workspace = new TempWorkspace();
        var factory = new RuntimeDbFactory(workspace.Path("runtime"));
        factory.DeleteMonth("199901");
        Assert.Empty(factory.ListMonthKeys());
    }
}

/// <summary>序列号生成：按日计数、跨日重置、并发不重号。</summary>
public class SerialNumberGeneratorTests
{
    [Fact]
    public async Task Generates_daily_incrementing_serial()
    {
        using var workspace = new TempWorkspace();
        await using var db = await TestDatabase.CreateConfigAsync(workspace.Path("config.db"));
        var generator = new SerialNumberGenerator(db);
        var day = new DateTime(2026, 9, 19, 8, 0, 0);

        Assert.Equal("20260919-000001", await generator.NextAsync(day));
        Assert.Equal("20260919-000002", await generator.NextAsync(day));
        Assert.Equal("20260919-000003", await generator.NextAsync(day.AddHours(5)));
    }

    [Fact]
    public async Task Counter_resets_on_new_day()
    {
        using var workspace = new TempWorkspace();
        await using var db = await TestDatabase.CreateConfigAsync(workspace.Path("config.db"));
        var generator = new SerialNumberGenerator(db);

        Assert.Equal("20260919-000001", await generator.NextAsync(new DateTime(2026, 9, 19)));
        Assert.Equal("20260920-000001", await generator.NextAsync(new DateTime(2026, 9, 20)));
        Assert.Equal("20260919-000002", await generator.NextAsync(new DateTime(2026, 9, 19, 23, 59, 59)));
    }

    [Fact]
    public async Task Concurrent_requests_never_duplicate_serial()
    {
        using var workspace = new TempWorkspace();
        await using var db = await TestDatabase.CreateConfigAsync(workspace.Path("config.db"));
        var generator = new SerialNumberGenerator(db);
        var day = new DateTime(2026, 9, 19);

        var serials = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => generator.NextAsync(day)));

        Assert.Equal(24, serials.Distinct().Count());
        Assert.Equal("20260919-000024", serials.Last());
        Assert.All(serials, s => Assert.StartsWith("20260919-", s));
        Assert.Single(await db.SerialCounters.ToListAsync());
    }
}

/// <summary>在制会话索引：写入、按托盘覆盖、列表与移除。</summary>
public class ActiveSessionStoreTests
{
    private static ActiveSessionIndex Session(string pallet, string serial, DateTime start, long sessionId = 0, string monthKey = "202609")
        => new() { PalletCode = pallet, SerialNo = serial, StartTime = start, SessionId = sessionId, MonthKey = monthKey };

    [Fact]
    public async Task Upsert_inserts_then_updates_by_pallet_code()
    {
        using var workspace = new TempWorkspace();
        await using var db = await TestDatabase.CreateConfigAsync(workspace.Path("config.db"));
        var store = new ActiveSessionStore(db);
        var start = new DateTime(2026, 9, 19, 9, 0, 0);

        await store.UpsertAsync(Session("P0001", "20260919-000001", start));
        var inserted = await store.FindByPalletAsync("P0001");
        Assert.NotNull(inserted);
        Assert.Equal("20260919-000001", inserted!.SerialNo);
        Assert.True(inserted.Id > 0);

        await store.UpsertAsync(Session("P0001", "20260919-000009", start.AddMinutes(5), sessionId: 7, monthKey: "202610"));
        var updated = await store.FindByPalletAsync("P0001");
        Assert.NotNull(updated);
        Assert.Equal(inserted.Id, updated!.Id);
        Assert.Equal("20260919-000009", updated.SerialNo);
        Assert.Equal(7, updated.SessionId);
        Assert.Equal("202610", updated.MonthKey);

        Assert.Single(await db.ActiveSessions.ToListAsync());
    }

    [Fact]
    public async Task Remove_by_pallet_drops_entry_and_ignores_unknown()
    {
        using var workspace = new TempWorkspace();
        await using var db = await TestDatabase.CreateConfigAsync(workspace.Path("config.db"));
        var store = new ActiveSessionStore(db);
        var start = new DateTime(2026, 9, 19, 9, 0, 0);

        await store.UpsertAsync(Session("P0001", "S1", start));
        await store.UpsertAsync(Session("P0002", "S2", start.AddSeconds(1)));

        await store.RemoveByPalletAsync("P0001");
        Assert.Null(await store.FindByPalletAsync("P0001"));
        Assert.NotNull(await store.FindByPalletAsync("P0002"));

        // 重复移除不抛异常。
        await store.RemoveByPalletAsync("P0001");
        await store.RemoveByPalletAsync("NOT-EXIST");
    }

    [Fact]
    public async Task List_returns_entries_ordered_by_start_time()
    {
        using var workspace = new TempWorkspace();
        await using var db = await TestDatabase.CreateConfigAsync(workspace.Path("config.db"));
        var store = new ActiveSessionStore(db);
        var start = new DateTime(2026, 9, 19, 9, 0, 0);

        await store.UpsertAsync(Session("P0003", "S3", start.AddMinutes(2)));
        await store.UpsertAsync(Session("P0001", "S1", start));
        await store.UpsertAsync(Session("P0002", "S2", start.AddMinutes(1)));

        var list = await store.ListAsync();
        Assert.Equal(new[] { "P0001", "P0002", "P0003" }, list.Select(x => x.PalletCode).ToArray());
    }
}

/// <summary>审计日志：落库字段完整、按时间倒序、可截断。</summary>
public class AuditLoggerTests
{
    [Fact]
    public async Task Write_persists_all_fields()
    {
        using var workspace = new TempWorkspace();
        await using var db = await TestDatabase.CreateConfigAsync(workspace.Path("config.db"));
        var logger = new AuditLogger(db);

        await logger.WriteAsync("engineer", "Update", "TagDefinition", "42", "上限=10", "上限=12");

        var log = await db.AuditLogs.SingleAsync();
        Assert.Equal("engineer", log.UserName);
        Assert.Equal("Update", log.Action);
        Assert.Equal("TagDefinition", log.EntityType);
        Assert.Equal("42", log.EntityKey);
        Assert.Equal("上限=10", log.OldValue);
        Assert.Equal("上限=12", log.NewValue);
        Assert.True(log.Time > DateTime.Now.AddMinutes(-1));
    }

    [Fact]
    public async Task Query_respects_take_and_returns_newest_first()
    {
        using var workspace = new TempWorkspace();
        await using var db = await TestDatabase.CreateConfigAsync(workspace.Path("config.db"));
        var logger = new AuditLogger(db);

        for (var i = 0; i < 5; i++)
        {
            await logger.WriteAsync("admin", $"Action{i}", "Station", i.ToString(), null, null);
            await Task.Delay(10);
        }

        var all = await logger.QueryAsync();
        Assert.Equal(5, all.Count);
        Assert.Equal("Action4", all[0].Action);
        Assert.Equal("Action0", all[^1].Action);

        var limited = await logger.QueryAsync(2);
        Assert.Equal(2, limited.Count);
        Assert.Equal("Action4", limited[0].Action);
        Assert.Equal("Action3", limited[1].Action);
    }

    [Fact]
    public async Task Write_accepts_nullable_keys_and_values()
    {
        using var workspace = new TempWorkspace();
        await using var db = await TestDatabase.CreateConfigAsync(workspace.Path("config.db"));
        var logger = new AuditLogger(db);

        await logger.WriteAsync("admin", "Login", "User", null, null, null);

        var log = await db.AuditLogs.SingleAsync();
        Assert.Null(log.EntityKey);
        Assert.Null(log.OldValue);
        Assert.Null(log.NewValue);
    }
}

/// <summary>配置仓库：快照装配、版本自增、点位于曲线的归一化约束。</summary>
public class ConfigRepositoryTests
{
    private static async Task<(TempWorkspace Workspace, ConfigDbContext Db, PlcConnection Plc)> SeedAsync()
    {
        var workspace = new TempWorkspace();
        var db = await TestDatabase.CreateConfigAsync(workspace.Path("config.db"));
        db.SystemSettings.Add(new SystemSettings { ScanIntervalMs = 99, RetentionYears = 5 });
        db.ConfigVersions.Add(new ConfigVersion { Version = 7 });

        var plc = new PlcConnection
        {
            Name = "模拟PLC",
            Brand = PlcBrand.Simulator,
            Heartbeat = new HeartbeatSettings { Address = "D0", IntervalMs = 500, Enabled = true },
            Stations =
            [
                new Station
                {
                    Code = "ST010",
                    Name = "上料工站",
                    Sequence = 10,
                    IsFirstStation = true,
                    TriggerAddress = "D1000",
                    PalletCodeAddress = "D1010",
                    PositionCount = 1,
                    Tags =
                    [
                        new TagDefinition { Code = "ST010_P1", Name = "压力", Address = "D1100", DataType = PlcDataType.Float, PositionIndex = 1 }
                    ],
                    Curves =
                    [
                        new CurveDefinition
                        {
                            Code = "ST010_PD",
                            Name = "位移压力曲线",
                            PointCount = 10,
                            PositionIndex = 1,
                            Series = [new CurveSeries { Name = "压力", Role = SeriesRole.Y, StartAddress = "D2000" }]
                        }
                    ]
                }
            ]
        };

        db.PlcConnections.Add(plc);
        await db.SaveChangesAsync();
        return (workspace, db, plc);
    }

    [Fact]
    public async Task Snapshot_assembles_settings_plcs_and_station_graph()
    {
        var (workspace, db, plc) = await SeedAsync();
        using var ws = workspace;
        await using var dbScope = db;
        var repo = new ConfigRepository(db);

        var snapshot = await repo.GetSnapshotAsync();

        Assert.Equal(99, snapshot.Settings.ScanIntervalMs);
        Assert.Equal(5, snapshot.Settings.RetentionYears);
        Assert.Equal(7, snapshot.Version);
        Assert.Single(snapshot.PlcConnections);
        Assert.Single(snapshot.Stations);

        var station = snapshot.Stations[0];
        Assert.Equal("ST010", station.Code);
        Assert.True(station.IsFirstStation);
        Assert.Equal(plc.Id, station.PlcConnectionId);
        Assert.Single(station.Tags);
        Assert.Single(station.Curves);
        Assert.Equal("压力", station.Curves.First().Series.First().Name);
        Assert.NotNull(snapshot.PlcConnections[0].Heartbeat);
        Assert.Equal("D0", snapshot.PlcConnections[0].Heartbeat!.Address);
    }

    [Fact]
    public async Task Snapshot_falls_back_to_defaults_when_unseeded()
    {
        using var workspace = new TempWorkspace();
        await using var db = await TestDatabase.CreateConfigAsync(workspace.Path("config.db"));
        var repo = new ConfigRepository(db);

        var snapshot = await repo.GetSnapshotAsync();

        // 未写入 SystemSettings 时应回落到默认值而不是抛异常。
        Assert.Equal(new SystemSettings().ScanIntervalMs, snapshot.Settings.ScanIntervalMs);
        Assert.Empty(snapshot.PlcConnections);
        Assert.Empty(snapshot.Stations);
        Assert.Equal(0, snapshot.Version);
        Assert.Equal(0, await repo.GetVersionAsync());
    }

    [Fact]
    public async Task Bump_version_increments_existing_row_or_creates_first()
    {
        var (workspace, db, _) = await SeedAsync();
        using var ws = workspace;
        await using var dbScope = db;
        var repo = new ConfigRepository(db);

        await repo.BumpVersionAsync();
        await repo.BumpVersionAsync();
        Assert.Equal(9, await repo.GetVersionAsync());

        using var emptyWorkspace = new TempWorkspace();
        await using var emptyDb = await TestDatabase.CreateConfigAsync(emptyWorkspace.Path("config.db"));
        var emptyRepo = new ConfigRepository(emptyDb);
        await emptyRepo.BumpVersionAsync();
        Assert.Equal(1, await emptyRepo.GetVersionAsync());
    }

    [Fact]
    public async Task Save_station_collapses_positions_to_single_product()
    {
        var (workspace, db, plc) = await SeedAsync();
        using var ws = workspace;
        await using var dbScope = db;
        var repo = new ConfigRepository(db);

        var station = new Station
        {
            PlcConnectionId = plc.Id,
            Code = "ST020",
            Name = "压装工站",
            Sequence = 20,
            TriggerAddress = "D1200",
            PalletCodeAddress = "D1210",
            PositionCount = 4,
            Positions =
            [
                new ProductPositionDefinition { Index = 2, Name = "产品位2", OccupiedAddress = "D1500" },
                new ProductPositionDefinition { Index = 1, Name = "产品位1" }
            ]
        };

        await repo.SaveStationAsync(station);

        Assert.Equal(1, station.PositionCount);
        var kept = Assert.Single(station.Positions);
        Assert.Equal(1, kept.Index);
        Assert.Equal("产品", kept.Name);
        Assert.Null(kept.OccupiedAddress);

        // 保存动作本身也要推版本，采集侧才能热加载。
        Assert.Equal(8, await repo.GetVersionAsync());

        var stored = await repo.GetStationAsync(station.Id);
        Assert.NotNull(stored);
        Assert.Equal("ST020", stored!.Code);
        Assert.Single(stored.Positions);
    }

    [Fact]
    public async Task Stations_are_returned_in_sequence_order_with_plc_attached()
    {
        var (workspace, db, plc) = await SeedAsync();
        using var ws = workspace;
        await using var dbScope = db;
        var repo = new ConfigRepository(db);

        await repo.SaveStationAsync(new Station { PlcConnectionId = plc.Id, Code = "ST005", Name = "前置工站", Sequence = 5, TriggerAddress = "D900", PalletCodeAddress = "D910" });

        var stations = await repo.GetStationsAsync();
        Assert.Equal(new[] { "ST005", "ST010" }, stations.Select(s => s.Code).ToArray());
        Assert.All(stations, s => Assert.NotNull(s.PlcConnection));
    }

    [Fact]
    public async Task Save_tag_clamps_position_index_to_zero_or_one()
    {
        var (workspace, db, plc) = await SeedAsync();
        using var ws = workspace;
        await using var dbScope = db;
        var repo = new ConfigRepository(db);
        var stationId = (await repo.GetStationsAsync()).First(s => s.Code == "ST010").Id;

        var high = new TagDefinition { StationId = stationId, Code = "T_HIGH", Name = "越界高位", Address = "D1800", DataType = PlcDataType.Float, PositionIndex = 5 };
        await repo.SaveTagAsync(high);
        Assert.Equal(1, high.PositionIndex);

        var low = new TagDefinition { StationId = stationId, Code = "T_LOW", Name = "越界低位", Address = "D1810", DataType = PlcDataType.Float, PositionIndex = -4 };
        await repo.SaveTagAsync(low);
        Assert.Equal(0, low.PositionIndex);

        await repo.DeleteTagAsync(high.Id);
        Assert.Null(db.Tags.FirstOrDefault(t => t.Id == high.Id));
        Assert.NotNull(db.Tags.FirstOrDefault(t => t.Id == low.Id));
    }

    [Fact]
    public async Task Save_curve_forces_product_position()
    {
        var (workspace, db, plc) = await SeedAsync();
        using var ws = workspace;
        await using var dbScope = db;
        var repo = new ConfigRepository(db);
        var stationId = (await repo.GetStationsAsync()).First(s => s.Code == "ST010").Id;

        var curve = new CurveDefinition
        {
            StationId = stationId,
            Code = "ST010_CURVE2",
            Name = "第二条曲线",
            PointCount = 20,
            PositionIndex = 9,
            Series = [new CurveSeries { Name = "压力", Role = SeriesRole.Y, StartAddress = "D3000" }]
        };

        await repo.SaveCurveAsync(curve);
        Assert.Equal(1, curve.PositionIndex);

        var stored = await repo.GetStationAsync(stationId);
        Assert.Contains(stored!.Curves, c => c.Code == "ST010_CURVE2");

        await repo.DeleteCurveAsync(curve.Id);
        Assert.False(db.Curves.Any(c => c.Id == curve.Id));
    }

    [Fact]
    public async Task Curve_criteria_are_synced_by_key_on_save()
    {
        var (workspace, db, plc) = await SeedAsync();
        using var ws = workspace;
        await using var dbScope = db;
        var repo = new ConfigRepository(db);
        var stationId = plc.Stations.First().Id;
        var curveId = (await repo.GetStationsAsync()).First(s => s.Id == stationId).Curves.First().Id;

        // 新增：Id 为 0 的判据落库后应当拿到主键。
        await repo.SaveCurveCriteriaAsync(curveId, [new CurveCriterion { PeakMax = 20, SeriesName = "压力" }]);

        var inserted = Assert.Single(db.CurveCriteria.ToList());
        Assert.True(inserted.Id > 0);
        Assert.Equal(20d, inserted.PeakMax!.Value);
        Assert.Equal("压力", inserted.SeriesName);
        Assert.Equal(curveId, inserted.CurveDefinitionId);
        // UI 直接读导航集合显示条数，同一行不能在里面出现两份。
        Assert.Single((await repo.GetStationAsync(stationId))!.Curves.First().Criteria);

        // 修改 + 再新增：带着原主键改值，同时追加一条。
        await repo.SaveCurveCriteriaAsync(curveId,
        [
            new CurveCriterion { Id = inserted.Id, PeakMax = 25, SeriesName = "压力" },
            new CurveCriterion { SeriesName = "位移", HoldSlopeMin = -0.5 }
        ]);

        var edited = db.CurveCriteria.ToList();
        Assert.Equal(2, edited.Count);
        Assert.Equal(25d, edited.Single(c => c.Id == inserted.Id).PeakMax!.Value);
        Assert.Contains(edited, c => c.SeriesName == "位移" && c.HoldSlopeMin == -0.5);
        Assert.Equal(2, (await repo.GetStationAsync(stationId))!.Curves.First().Criteria.Count);

        // 减少：提交时去掉压力那条，库里不能留下孤儿行。
        var keepId = edited.Single(c => c.SeriesName == "位移").Id;
        await repo.SaveCurveCriteriaAsync(curveId,
            [new CurveCriterion { Id = keepId, SeriesName = "位移", HoldSlopeMin = -0.5 }]);

        var only = Assert.Single(db.CurveCriteria.ToList());
        Assert.Equal(keepId, only.Id);
        Assert.Equal("位移", only.SeriesName);
        Assert.Single((await repo.GetStationAsync(stationId))!.Curves.First().Criteria);

        // 清空：提交空集合等于关闭波形判定。
        await repo.SaveCurveCriteriaAsync(curveId, []);
        Assert.Empty(db.CurveCriteria.ToList());
        Assert.Empty((await repo.GetStationAsync(stationId))!.Curves.First().Criteria);

        // 曲线不存在时静默返回，不抛异常也不落任何行。
        await repo.SaveCurveCriteriaAsync(999_999, [new CurveCriterion { PeakMax = 1 }]);
        Assert.Empty(db.CurveCriteria.ToList());
    }

    [Fact]
    public async Task Curve_criteria_are_included_in_snapshot_and_station_queries()
    {
        var (workspace, db, plc) = await SeedAsync();
        using var ws = workspace;
        await using var dbScope = db;
        var repo = new ConfigRepository(db);
        var stationId = plc.Stations.First().Id;
        var curveId = (await repo.GetStationsAsync()).First(s => s.Id == stationId).Curves.First().Id;

        await repo.SaveCurveCriteriaAsync(curveId, [new CurveCriterion { SeriesName = "压力", FallRatioMax = 0.4 }]);

        // 采集器拿的是快照，判据必须随快照一起下发，否则配置改了但判定不生效。
        var snapshot = await repo.GetSnapshotAsync();
        var fromSnapshot = Assert.Single(snapshot.Stations[0].Curves.First().Criteria);
        Assert.Equal("压力", fromSnapshot.SeriesName);
        Assert.Equal(0.4d, fromSnapshot.FallRatioMax!.Value);

        var stations = await repo.GetStationsAsync();
        Assert.Single(stations.First(s => s.Id == stationId).Curves.First().Criteria);
    }

    [Fact]
    public async Task Recipe_limits_are_synced_by_key_and_survive_a_round_trip()
    {
        var (workspace, db, plc) = await SeedAsync();
        using var ws = workspace;
        await using var dbScope = db;
        var repo = new ConfigRepository(db);
        var tagId = (await repo.GetStationsAsync()).First(s => s.Code == "ST010").Tags.First().Id;

        await repo.SaveRecipeAsync(new Recipe { Code = "B200", Name = "型号 B200" });
        Assert.True(await db.Recipes.AnyAsync(x => x.Code == "B200"));

        // 界面走的是「取一份游离实体 → 改完整体提交」，测试照同一条路径走。
        var recipe = (await repo.GetRecipesAsync()).Single(r => r.Code == "B200");

        recipe.Limits = [new RecipeLimit { TagId = tagId, UpperLimit = 16 }];
        await repo.SaveRecipeAsync(recipe);

        var stored = Assert.Single(db.RecipeLimits.ToList());
        Assert.Equal(recipe.Id, stored.RecipeId);
        Assert.Equal(tagId, stored.TagId);
        Assert.Equal(16d, stored.UpperLimit!.Value);
        Assert.Null(stored.LowerLimit);

        // 修改 + 再新增：带原主键改值，同时追加一条。
        recipe = (await repo.GetRecipesAsync()).Single(r => r.Code == "B200");
        var firstId = recipe.Limits.Single().Id;
        recipe.Limits =
        [
            new RecipeLimit { Id = firstId, TagId = tagId, UpperLimit = 15 },
            new RecipeLimit { TagId = tagId + 1000, WarningUpperLimit = 45 }
        ];
        await repo.SaveRecipeAsync(recipe);

        var edited = db.RecipeLimits.ToList();
        Assert.Equal(2, edited.Count);
        Assert.Equal(15d, edited.Single(x => x.Id == firstId).UpperLimit!.Value);
        Assert.Contains(edited, x => x.TagId == tagId + 1000 && x.WarningUpperLimit == 45);

        // 减少：提交时去掉第一条，库里不能留下孤儿行。
        recipe = (await repo.GetRecipesAsync()).Single(r => r.Code == "B200");
        var keep = recipe.Limits.Single(x => x.TagId == tagId + 1000);
        recipe.Limits = [new RecipeLimit { Id = keep.Id, TagId = keep.TagId, WarningUpperLimit = 45 }];
        await repo.SaveRecipeAsync(recipe);
        Assert.Equal(keep.Id, Assert.Single(db.RecipeLimits.ToList()).Id);

        // 清空：等于该型号不做任何覆盖。
        recipe = (await repo.GetRecipesAsync()).Single(r => r.Code == "B200");
        recipe.Limits = [];
        await repo.SaveRecipeAsync(recipe);
        Assert.Empty(db.RecipeLimits.ToList());

        // 同一点位重复提交只保留第一条，避免撞 (RecipeId, TagId) 唯一索引。
        recipe = (await repo.GetRecipesAsync()).Single(r => r.Code == "B200");
        recipe.Limits =
        [
            new RecipeLimit { TagId = tagId, UpperLimit = 14 },
            new RecipeLimit { TagId = tagId, UpperLimit = 99 }
        ];
        await repo.SaveRecipeAsync(recipe);
        Assert.Equal(14d, Assert.Single(db.RecipeLimits.ToList()).UpperLimit!.Value);
    }

    [Fact]
    public async Task Active_recipe_pointer_round_trips_and_rejects_disabled_models()
    {
        var (workspace, db, plc) = await SeedAsync();
        using var ws = workspace;
        await using var dbScope = db;
        var repo = new ConfigRepository(db);

        var recipe = new Recipe { Code = "C300", Name = "型号 C300" };
        await repo.SaveRecipeAsync(recipe);

        Assert.Null((await repo.GetSnapshotAsync()).ActiveRecipe);

        await repo.SetActiveRecipeAsync(recipe.Id);
        var active = (await repo.GetSnapshotAsync()).ActiveRecipe;
        Assert.NotNull(active);
        Assert.Equal("C300", active!.Code);

        // 停用的型号不能被设为当前型号。
        recipe.Enabled = false;
        await repo.SaveRecipeAsync(recipe);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.SetActiveRecipeAsync(recipe.Id));

        // 停用当前型号时指针被清掉，不留「选着但已停用」的状态。
        recipe.Enabled = true;
        await repo.SaveRecipeAsync(recipe);
        await repo.SetActiveRecipeAsync(recipe.Id);
        recipe.Enabled = false;
        await repo.SaveRecipeAsync(recipe);
        Assert.Null((await repo.GetSnapshotAsync()).ActiveRecipe);

        // 不存在的型号同样要报错，而不是静默失败。
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.SetActiveRecipeAsync(999_999));

        // 传 null 表示回到「全部使用点位默认限值」。
        await repo.SetActiveRecipeAsync(null);
        Assert.Null((await repo.GetSnapshotAsync()).ActiveRecipe);
    }

    [Fact]
    public async Task Deleting_the_active_recipe_clears_the_pointer()
    {
        var (workspace, db, plc) = await SeedAsync();
        using var ws = workspace;
        await using var dbScope = db;
        var repo = new ConfigRepository(db);

        var recipe = new Recipe { Code = "D400", Name = "型号 D400" };
        await repo.SaveRecipeAsync(recipe);
        await repo.SetActiveRecipeAsync(recipe.Id);
        Assert.NotNull((await repo.GetSnapshotAsync()).ActiveRecipe);

        await repo.DeleteRecipeAsync(recipe.Id);

        Assert.Null((await repo.GetSnapshotAsync()).ActiveRecipe);
        Assert.Empty(db.Recipes.ToList());

        // 删不存在的型号是静默无操作。
        await repo.DeleteRecipeAsync(recipe.Id);
    }

    [Fact]
    public async Task Snapshot_refuses_to_activate_a_disabled_recipe()
    {
        var (workspace, db, plc) = await SeedAsync();
        using var ws = workspace;
        await using var dbScope = db;
        var repo = new ConfigRepository(db);

        var recipe = new Recipe { Code = "E500", Name = "型号 E500" };
        await repo.SaveRecipeAsync(recipe);
        await repo.SetActiveRecipeAsync(recipe.Id);

        var snapshot = await repo.GetSnapshotAsync();
        Assert.Single(snapshot.Recipes);
        Assert.Equal("E500", snapshot.ActiveRecipe!.Code);

        // 直接把指针改成指向一个已停用型号（模拟历史数据或手工改库），快照必须拒绝生效。
        recipe.Enabled = false;
        await repo.SaveRecipeAsync(recipe);
        await repo.SetActiveRecipeAsync(null);

        var settings = await db.SystemSettings.FirstAsync();
        settings.ActiveRecipeId = recipe.Id;
        await db.SaveChangesAsync();

        var fallback = await repo.GetSnapshotAsync();
        Assert.Single(fallback.Recipes);
        Assert.Null(fallback.ActiveRecipe);
    }

    [Fact]
    public async Task Saving_a_tag_with_contradictory_limits_is_refused_and_nothing_is_written()
    {
        var (workspace, db, plc) = await SeedAsync();
        using var ws = workspace;
        await using var dbScope = db;
        var repo = new ConfigRepository(db);
        var station = plc.Stations.First();

        var before = await repo.GetSnapshotAsync();

        // 黄线跑到红线外侧：采集端只会默默收敛，所以必须在写库这一步就拒绝。
        var bad = new TagDefinition
        {
            StationId = station.Id,
            Code = "ST010_P9",
            Name = "压力2",
            Address = "D1190",
            DataType = PlcDataType.Float,
            LowerLimit = 5,
            UpperLimit = 20,
            WarningLowerLimit = 2
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repo.SaveTagAsync(bad));
        Assert.Contains("ST010_P9", error.Message);
        Assert.Contains("预警下限不能低于规格下限", error.Message);

        var after = await repo.GetSnapshotAsync();
        Assert.Equal(before.Stations.Sum(s => s.Tags.Count), after.Stations.Sum(s => s.Tags.Count));
        Assert.DoesNotContain(after.Stations.SelectMany(s => s.Tags), t => t.Code == "ST010_P9");

        // 自洽的一套必须能存 —— 否则就是校验本身写错了。
        bad.WarningLowerLimit = 6;
        await repo.SaveTagAsync(bad);
        Assert.Contains((await repo.GetSnapshotAsync()).Stations.SelectMany(s => s.Tags), t => t.Code == "ST010_P9");
    }

    [Fact]
    public async Task Recipe_override_is_checked_after_merging_with_the_tag_defaults()
    {
        var (workspace, db, plc) = await SeedAsync();
        using var ws = workspace;
        await using var dbScope = db;
        var repo = new ConfigRepository(db);
        var tag = plc.Stations.First().Tags.First();
        tag.LowerLimit = 5;
        tag.UpperLimit = 20;
        await repo.SaveTagAsync(tag);

        // 单看这条覆盖行毫无问题（只有黄线，没有红线），但它是要和点位的红线 20 合并生效的。
        var alone = TagLimits.From(new RecipeLimit { TagId = tag.Id, WarningUpperLimit = 90 });
        Assert.Null(alone.ConsistencyError());

        var recipe = new Recipe
        {
            Code = "F600",
            Name = "型号 F600",
            Limits = [new RecipeLimit { TagId = tag.Id, WarningUpperLimit = 90 }]
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repo.SaveRecipeAsync(recipe));
        Assert.Contains("F600", error.Message);
        Assert.Contains("预警上限不能高于规格上限", error.Message);
        Assert.Empty(db.Recipes.ToList());

        // 收紧到红线内侧就能存，且留空字段仍然是"沿用点位默认值"而不是"清空"。
        recipe.Limits.Single().WarningUpperLimit = 16;
        await repo.SaveRecipeAsync(recipe);

        var snapshot = await repo.GetSnapshotAsync();
        var effective = RecipeLimitResolver.Resolve(
            snapshot.Stations.SelectMany(s => s.Tags).Single(t => t.Id == tag.Id),
            snapshot.Recipes.Single());
        Assert.Equal(5d, effective.Lower);
        Assert.Equal(20d, effective.Upper);
        Assert.Equal(16d, effective.WarningUpper);
    }

    [Fact]
    public async Task Override_row_pointing_at_a_deleted_tag_does_not_lock_the_recipe()
    {
        var (workspace, db, _) = await SeedAsync();
        using var ws = workspace;
        await using var dbScope = db;
        var repo = new ConfigRepository(db);

        // 点位已经不在配置库里（删点位留下的残留覆盖行）：不能因此把型号编辑器锁死，
        // 这种行没有可比对的默认值，跳过即可。
        var recipe = new Recipe
        {
            Code = "G700",
            Name = "型号 G700",
            Limits = [new RecipeLimit { TagId = 999_999, WarningUpperLimit = 90 }]
        };

        await repo.SaveRecipeAsync(recipe);
        Assert.Single(db.Recipes.ToList());
    }

    [Fact]
    public async Task Save_settings_updates_existing_row_and_bumps_version()
    {
        var (workspace, db, _) = await SeedAsync();
        using var ws = workspace;
        await using var dbScope = db;
        var repo = new ConfigRepository(db);

        var settings = await db.SystemSettings.FirstAsync();
        settings.MesEnabled = true;
        settings.MesEndpoint = "http://mes.local/api";

        await repo.SaveSettingsAsync(settings);

        var snapshot = await repo.GetSnapshotAsync();
        Assert.True(snapshot.Settings.MesEnabled);
        Assert.Equal("http://mes.local/api", snapshot.Settings.MesEndpoint);
        Assert.Equal(8, snapshot.Version);
        Assert.Single(await db.SystemSettings.ToListAsync());
    }

    [Fact]
    public async Task Plc_connection_can_be_read_saved_and_deleted()
    {
        var (workspace, db, plc) = await SeedAsync();
        using var ws = workspace;
        await using var dbScope = db;
        var repo = new ConfigRepository(db);

        var loaded = await repo.GetPlcConnectionAsync(plc.Id);
        Assert.NotNull(loaded);
        Assert.Equal("模拟PLC", loaded!.Name);
        Assert.NotNull(loaded.Heartbeat);

        loaded.Name = "模拟PLC-改";
        await repo.SavePlcConnectionAsync(loaded);
        Assert.Equal("模拟PLC-改", (await repo.GetPlcConnectionAsync(plc.Id))!.Name);

        var extra = new PlcConnection { Name = "第二台", Brand = PlcBrand.Simulator };
        await repo.SavePlcConnectionAsync(extra);
        Assert.Equal(2, (await repo.GetPlcConnectionsAsync()).Count);

        await repo.DeletePlcConnectionAsync(extra.Id);
        Assert.Single(await repo.GetPlcConnectionsAsync());
        Assert.Equal(10, await repo.GetVersionAsync());
    }

    [Fact]
    public async Task Delete_missing_entities_is_noop()
    {
        var (workspace, db, _) = await SeedAsync();
        using var ws = workspace;
        await using var dbScope = db;
        var repo = new ConfigRepository(db);

        await repo.DeleteStationAsync(9999);
        await repo.DeleteTagAsync(9999);
        await repo.DeleteCurveAsync(9999);
        await repo.DeletePlcConnectionAsync(9999);

        // 空操作不应推版本。
        Assert.Equal(7, await repo.GetVersionAsync());
    }
}
