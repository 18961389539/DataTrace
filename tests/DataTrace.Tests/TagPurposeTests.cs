using DataTrace.Application.Evaluation;
using DataTrace.Application.Realtime;
using DataTrace.Collector;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure.Persistence;
using DataTrace.Infrastructure.Realtime;
using DataTrace.Plc.Planning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DataTrace.Tests;

public sealed class TagPurposeTests
{
    [Fact]
    public async Task Config_round_trips_all_purposes_and_defaults_new_tags_to_quality_measurement()
    {
        using var workspace = new TempWorkspace();
        await using var db = await TestDatabase.CreateConfigAsync(workspace.Path("config.db"));
        var tags = Enum.GetValues<TagPurpose>()
            .Select(purpose => new TagDefinition
            {
                Name = purpose.ToString(),
                Address = $"D{(int)purpose + 100}",
                DataType = PlcDataType.Float,
                Purpose = purpose
            })
            .Append(new TagDefinition { Name = "Default", Address = "D200", DataType = PlcDataType.Float })
            .ToList();
        db.PlcConnections.Add(new PlcConnection
        {
            Name = "purpose-test-plc",
            Stations =
            [
                new Station
                {
                    Code = "PURPOSE",
                    Name = "Purpose test",
                    TriggerAddress = "D10",
                    PalletCodeAddress = "D20",
                    Tags = tags
                }
            ]
        });
        await db.SaveChangesAsync();

        var persisted = await db.Tags.AsNoTracking().ToDictionaryAsync(tag => tag.Name, tag => tag.Purpose);

        Assert.Equal(TagPurpose.QualityMeasurement, persisted["Default"]);
        foreach (var purpose in Enum.GetValues<TagPurpose>())
        {
            Assert.Equal(purpose, persisted[purpose.ToString()]);
        }
    }

    [Fact]
    public async Task Config_migration_classifies_existing_rows_with_the_compatible_default()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ConfigDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ConfigDbContext(options);
        const string priorMigration = "20261012023326_AddConditionalEventConfirmation";
        await db.GetService<IMigrator>().MigrateAsync(priorMigration);

        var station = new Station
        {
            Code = "LEGACY",
            Name = "Legacy station",
            TriggerAddress = "D10",
            PalletCodeAddress = "D20"
        };
        db.PlcConnections.Add(new PlcConnection
        {
            Name = "legacy-plc",
            Stations = [station]
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Tags"
                ("StationId", "Name", "Address", "DataType", "Length", "Scale", "Offset",
                 "IsRequired", "PositionIndex", "Enabled", "Source")
            VALUES ({0}, 'LegacyTag', 'D100', 3, 1, 1, 0, 1, 1, 1, 0)
            """,
            station.Id);

        await db.Database.MigrateAsync();

        var migrated = await db.Tags.AsNoTracking().SingleAsync(tag => tag.Name == "LegacyTag");
        Assert.Equal(TagPurpose.QualityMeasurement, migrated.Purpose);
    }

    [Fact]
    public async Task Runtime_migration_keeps_legacy_purpose_snapshots_unknown()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.Path("runtime");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "data_202609.db");
        var options = new DbContextOptionsBuilder<RuntimeDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        const string priorMigration = "20261012113312_AddTagValueUnitSnapshot";
        await using (var legacy = new RuntimeDbContext(options))
        {
            await legacy.GetService<IMigrator>().MigrateAsync(priorMigration);
            await legacy.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "PalletSessions" ("SerialNo", "PalletCode", "StartTime", "Status", "Judgement")
                VALUES ('SN-LEGACY', 'P-LEGACY', '2026-09-01 08:00:00', 0, 0)
                """);
            await legacy.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "CollectRecords"
                    ("PalletSessionId", "SerialNo", "PalletCode", "StationId", "StationCode",
                     "TriggerTime", "CompleteTime", "DurationMs", "ResultCode", "Judgement",
                     "RecipeCode", "ArchivePath", "ArchiveFileSize", "ArchiveCrc32")
                VALUES (1, 'SN-LEGACY', 'P-LEGACY', 1, 'ST01',
                        '2026-09-01 08:00:00', '2026-09-01 08:00:01', 1, 1, 0, '', '', 0, 0)
                """);
            await legacy.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "TagValues"
                    ("CollectRecordId", "TagId", "TagName", "UnitSnapshot", "PositionIndex",
                     "DataType", "NumericValue", "IsOutOfLimit", "IsWarning")
                VALUES (1, 1, 'LegacyTag', NULL, 1, 3, 10, 0, 0)
                """);
        }

        await using var upgraded = new RuntimeDbFactory(root).Open("202609");
        var legacyValue = await upgraded.TagValues.AsNoTracking().SingleAsync();

        Assert.Null(legacyValue.PurposeSnapshot);
    }

    [Fact]
    public async Task Identity_purpose_is_snapshotted_bypasses_spec_limits_and_is_excluded_from_spc()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        var tag = station.Tags.Single(item => item.Name == "压力");
        tag.Purpose = TagPurpose.Identity;

        for (var i = 0; i < 10; i++)
        {
            var pallet = $"PURPOSE-{i:00}";
            SimulatorScenario.LoadStationCycle(harness.Simulator, harness.Plc, station, pallet);
            harness.Simulator.SetFloat("D1100", 999f, harness.Plc.FloatWordOrder);
            Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));
        }

        var item = Assert.Single((await harness.QueryAsync("PURPOSE-09")).Items);
        var record = await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id);
        var value = Assert.Single(record!.TagValues, row => row.TagId == tag.Id);
        Assert.Equal(TagPurpose.Identity, value.PurposeSnapshot);
        Assert.False(value.IsOutOfLimit);
        Assert.False(value.IsWarning);
        Assert.Null(value.UpperLimit);

        var points = await harness.RuntimeStore.QueryTagTrendAsync(
            DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1), tag.Id);
        Assert.Empty(points);
    }

    [Fact]
    public async Task Discrete_result_still_uses_explicit_spec_limits()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        var tag = station.Tags.Single(item => item.Name == "压力");
        tag.Purpose = TagPurpose.DiscreteResult;
        SimulatorScenario.LoadStationCycle(harness.Simulator, harness.Plc, station, "DISCRETE-01");
        harness.Simulator.SetFloat("D1100", 999f, harness.Plc.FloatWordOrder);

        Assert.Equal(ResultCodes.QualityRejected, await harness.RunAsync(station));

        var item = Assert.Single((await harness.QueryAsync("DISCRETE-01")).Items);
        var record = await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id);
        var value = Assert.Single(record!.TagValues, row => row.TagId == tag.Id);
        Assert.Equal(TagPurpose.DiscreteResult, value.PurposeSnapshot);
        Assert.True(value.IsOutOfLimit);

        var trend = await harness.RuntimeStore.QueryTagTrendAsync(
            DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1), tag.Id);
        Assert.Single(trend);
        Assert.Equal(TagPurpose.DiscreteResult, trend[0].PurposeSnapshot);
    }

    [Fact]
    public void Non_measurement_categories_do_not_create_warning_streaks_or_drift_alarms()
    {
        var hub = new RuntimeStatusHub();
        TagPurpose?[] monitorable = [TagPurpose.QualityMeasurement, null];
        TagPurpose?[] excluded = [TagPurpose.DiscreteResult, TagPurpose.Identity, TagPurpose.Diagnostic];
        var values = Enumerable.Repeat(10d, 10).Concat(Enumerable.Repeat(100d, 10)).ToArray();

        foreach (var purpose in monitorable.Concat(excluded))
        {
            var tagId = purpose is null ? 5 : (int)purpose.Value + 1;
            for (var i = 0; i < values.Length; i++)
            {
                hub.Publish(new CollectRecord
                {
                    StationId = 1,
                    StationCode = "ST01",
                    CompleteTime = new DateTime(2026, 10, 12).AddSeconds(i),
                    TagValues =
                    [
                        new TagValue
                        {
                            TagId = tagId,
                            TagName = $"Tag-{tagId}",
                            PurposeSnapshot = purpose,
                            NumericValue = values[i],
                            IsWarning = true
                        }
                    ]
                });
            }
        }

        Assert.Equal(new[] { 1, 5 }, hub.WarningStreaks.Select(streak => streak.TagId).Order().ToArray());
        Assert.Equal(new[] { 1, 5 }, hub.DriftNotices.Select(drift => drift.TagId).Order().ToArray());
    }

    [Fact]
    public void Missing_required_diagnostic_number_still_fails_validation()
    {
        var tag = new TagDefinition
        {
            Id = 7,
            Name = "DeviceState",
            Address = "state",
            DataType = PlcDataType.Float,
            Source = TagDataSource.JsonFile,
            Purpose = TagPurpose.Diagnostic,
            IsRequired = true
        };
        var station = new Station { PositionCount = 1, Tags = [tag] };
        var assessment = CollectEvaluator.Evaluate(
            station,
            new PlcConnection(),
            new ReadPlan([], new Dictionary<string, IReadOnlyList<ReadSlice>>()),
            [],
            new FileSourceRead(new Dictionary<int, SourceTagValue>(), "", 0, 0),
            new EmptyCurveBaselineCache(),
            null);

        Assert.True(assessment.ValidationError);
        Assert.False(Assert.Single(assessment.TagValues).IsOutOfLimit);
    }

    private sealed class EmptyCurveBaselineCache : ICurveBaselineCache
    {
        public CurveBaselineSnapshot? Current => null;

        public void Replace(CurveBaselineSnapshot snapshot)
        {
        }

        public void RetagRecipeCode(string oldCode, string newCode)
        {
        }
    }
}
