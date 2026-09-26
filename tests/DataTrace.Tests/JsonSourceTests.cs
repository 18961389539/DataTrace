using System.Text;
using System.Text.Json;
using DataTrace.Application.Runtime;
using DataTrace.Collector;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Validation;
using DataTrace.Infrastructure.Persistence;
using DataTrace.Infrastructure.Seeding;
using DataTrace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Tests;

/// <summary>
/// 文件源点位（工站数据来源 = JSON 文件）：取值口径、归档、失败语义。
/// </summary>
/// <remarks>
/// 现场形态是"唯一一台设备每件覆写同一个固定文件 → 置触发位 → 等回写码 → 才写下一件"，
/// 因此这里不测"等文件"与时序，只测：读到的值对不对、归档是不是原件、
/// 以及读不到/归档不了时有没有如实失败（回写码 + 不落库）。
/// </remarks>
public class JsonSourceTests
{
    // ---------- 取值口径：纯函数 ----------

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Numeric_reader_accepts_numbers_and_numeric_strings()
    {
        var root = Root("""{"a": 12.5, "b": "40", "c": "-1.5e2"}""");

        Assert.True(JsonFieldReader.TryReadNumeric(root, "a", out var a));
        Assert.Equal(12.5d, a, precision: 6);
        Assert.True(JsonFieldReader.TryReadNumeric(root, "b", out var b));
        Assert.Equal(40d, b, precision: 6);
        Assert.True(JsonFieldReader.TryReadNumeric(root, "c", out var c));
        Assert.Equal(-150d, c, precision: 6);
    }

    [Fact]
    public void Numeric_reader_rejects_values_with_units_and_non_numbers()
    {
        // 带单位的文本本该是字段配错，静默按前缀解析出一个数比取不到更危险。
        var root = Root("""{"unit": "12 kN", "text": "OK", "flag": true, "nil": null, "obj": {}, "arr": [1]}""");

        Assert.False(JsonFieldReader.TryReadNumeric(root, "unit", out _));
        Assert.False(JsonFieldReader.TryReadNumeric(root, "text", out _));
        Assert.False(JsonFieldReader.TryReadNumeric(root, "flag", out _));
        Assert.False(JsonFieldReader.TryReadNumeric(root, "nil", out _));
        Assert.False(JsonFieldReader.TryReadNumeric(root, "obj", out _));
        Assert.False(JsonFieldReader.TryReadNumeric(root, "arr", out _));
    }

    [Fact]
    public void Reader_walks_nested_paths_and_misses_are_not_errors()
    {
        var root = Root("""{"data": {"force": {"peak": 8.5}}, "M100": 40}""");

        Assert.True(JsonFieldReader.TryReadNumeric(root, "data.force.peak", out var peak));
        Assert.Equal(8.5d, peak, precision: 6);

        Assert.False(JsonFieldReader.TryReadNumeric(root, "data.force.valley", out _));
        Assert.False(JsonFieldReader.TryReadNumeric(root, "data.missing.peak", out _));
        Assert.False(JsonFieldReader.TryReadNumeric(root, "data.force.peak.deep", out _));

        // 字段名里的点会让"第一段是顶层键"的写法失效，按约定当作两层处理（JSON 字段名惯例不带头点）。
        Assert.False(JsonFieldReader.TryReadNumeric(root, "data.force", out _));
    }

    [Fact]
    public void Text_reader_keeps_the_raw_representation()
    {
        var root = Root("""{"name": "A100", "count": 12, "flag": true}""");

        Assert.True(JsonFieldReader.TryReadText(root, "name", out var name));
        Assert.Equal("A100", name);
        // 数字按原文返回：读到 12 不该变成 "12.0"。
        Assert.True(JsonFieldReader.TryReadText(root, "count", out var count));
        Assert.Equal("12", count);
        Assert.True(JsonFieldReader.TryReadText(root, "flag", out var flag));
        Assert.Equal("true", flag);
        Assert.False(JsonFieldReader.TryReadText(root, "missing", out _));
    }

    [Theory]
    [InlineData("force", null)]
    [InlineData("data.force", null)]
    [InlineData("   data.force   ", null)]
    [InlineData("", "不能为空")]
    [InlineData("  ", "不能为空")]
    [InlineData("data..force", "连续")]
    [InlineData(".force", "不能出现在开头")]
    [InlineData("force.", "不能出现在开头")]
    [InlineData("data[0].force", "数组下标")]
    [InlineData("data force", "空白字符")]
    public void Field_path_shape_is_validated_before_saving(string path, string? expectedFragment)
    {
        var error = JsonFieldRules.Error(path);
        if (expectedFragment is null)
        {
            Assert.Null(error);
            return;
        }

        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error);
    }

    [Fact]
    public void Field_path_depth_and_length_are_bounded()
    {
        var deep = string.Join('.', Enumerable.Range(1, JsonFieldRules.MaxDepth + 1).Select(i => $"f{i}"));
        Assert.Contains("最多", JsonFieldRules.Error(deep)!);

        var longPath = new string('a', JsonFieldRules.MaxLength + 1);
        Assert.Contains("不能超过", JsonFieldRules.Error(longPath)!);
    }

    // ---------- 归档：与曲线文件同一套约定 ----------

    [Fact]
    public async Task Archive_round_trips_original_bytes_and_verifies_crc()
    {
        using var workspace = new TempWorkspace();
        var store = new CollectArchiveFileStore(workspace.Path("archive"));
        var content = Encoding.UTF8.GetBytes("""{"force": 12.5}""");

        var written = await store.WriteAsync(DateTime.Today.AddHours(8), "P0001", 7, content);

        Assert.Equal(content.LongLength, written.FileSize);
        Assert.EndsWith(".json", written.RelativePath);
        Assert.Contains("P0001", written.RelativePath);
        var read = await store.ReadAsync(written.RelativePath, written.Crc32);
        Assert.Equal(content, read);
    }

    [Fact]
    public async Task Archive_never_overwrites_an_existing_file()
    {
        using var workspace = new TempWorkspace();
        var store = new CollectArchiveFileStore(workspace.Path("archive"));
        var time = DateTime.Today.AddHours(8);

        var first = await store.WriteAsync(time, "P0001", 7, Encoding.UTF8.GetBytes("""{"n": 1}"""));
        var second = await store.WriteAsync(time, "P0001", 7, Encoding.UTF8.GetBytes("""{"n": 2}"""));

        // 同一毫秒、同一托盘的两件：第二份必须让开一格，否则回滚或保留策略会删掉别人的归档。
        Assert.NotEqual(first.RelativePath, second.RelativePath);
        Assert.Equal("""{"n": 1}""", Encoding.UTF8.GetString(await store.ReadAsync(first.RelativePath, first.Crc32)));
        Assert.Equal("""{"n": 2}""", Encoding.UTF8.GetString(await store.ReadAsync(second.RelativePath, second.Crc32)));
    }

    [Fact]
    public async Task Archive_reports_corruption_instead_of_missing()
    {
        using var workspace = new TempWorkspace();
        var store = new CollectArchiveFileStore(workspace.Path("archive"));
        var written = await store.WriteAsync(DateTime.Today, "P0001", 7, Encoding.UTF8.GetBytes("""{"n": 1}"""));

        await File.WriteAllBytesAsync(
            Path.Combine(workspace.Path("archive"), written.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
            Encoding.UTF8.GetBytes("""{"n": 9}"""));

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(written.RelativePath, written.Crc32));
    }

    [Fact]
    public async Task Archive_month_is_deleted_with_the_rest_of_the_month()
    {
        using var workspace = new TempWorkspace();
        var store = new CollectArchiveFileStore(workspace.Path("archive"));
        await store.WriteAsync(new DateTime(2026, 3, 5), "P0001", 7, Encoding.UTF8.GetBytes("{}"));
        await store.WriteAsync(new DateTime(2026, 4, 5), "P0002", 7, Encoding.UTF8.GetBytes("{}"));

        await store.DeleteMonthAsync("2026", "03");

        var left = Directory.GetFiles(workspace.Path("archive"), "*.json", SearchOption.AllDirectories);
        var segments = Assert.Single(left).Replace('\\', '/').Split('/');
        // …/archive/2026/04/05/xxx.json：被保留的月份（04）必须还在原来的目录层级上。
        Assert.Equal("04", segments[^3]);
        Assert.Equal("2026", segments[^4]);
    }

    // ---------- 老库升级 ----------

    /// <summary>
    /// 老配置库补新列：EnsureCreated 只管建表，已存在的库里加列全靠 Seeder 启动时那句 ALTER。
    /// 写错列名/漏掉列，现场表现是"打开工站配置就报 no such column"，而不是某一处功能不好用。
    /// </summary>
    [Fact]
    public async Task Old_config_database_gets_the_new_columns_on_startup()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        var db = ctx.Scope.ServiceProvider.GetRequiredService<ConfigDbContext>();

        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Tags DROP COLUMN Source");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Stations DROP COLUMN DataFilePath");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Stations DROP COLUMN DataFileFormat");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Stations ADD COLUMN ScriptPath TEXT NOT NULL DEFAULT ''");
        Assert.DoesNotContain("Source", await ColumnNamesAsync(db, "Tags"));

        await ctx.Scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();

        var columns = await ColumnNamesAsync(db, "Tags");
        Assert.Contains("Source", columns);
        Assert.Contains("DataFilePath", await ColumnNamesAsync(db, "Stations"));
        Assert.Contains("DataFileFormat", await ColumnNamesAsync(db, "Stations"));
        Assert.DoesNotContain("ScriptPath", await ColumnNamesAsync(db, "Stations"));
        var stations = await db.Stations.AsNoTracking().ToListAsync();
        Assert.NotEmpty(stations);
        Assert.All(stations, s => Assert.Equal(DataFileFormat.Json, s.DataFileFormat));

        // 老库里的点位必须落在"PLC 寄存器"这一支：升级不能悄悄改掉已有行为。
        var tags = await db.Tags.AsNoTracking().ToListAsync();
        Assert.NotEmpty(tags);
        Assert.All(tags, t => Assert.Equal(TagDataSource.Plc, t.Source));
    }

    /// <summary>老月库补归档列：每个进程第一次打开该月库时补齐，否则明细页与落库都会报缺列。</summary>
    [Fact]
    public async Task Old_month_database_gets_the_new_archive_columns_when_opened()
    {
        using var workspace = new TempWorkspace();
        var monthKey = DateTime.Now.ToString("yyyyMM");

        var first = new RuntimeDbFactory(workspace.Path("runtime"));
        var db = first.Open(monthKey);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE CollectRecords DROP COLUMN ArchivePath");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE CollectRecords DROP COLUMN ArchiveFileSize");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE CollectRecords DROP COLUMN ArchiveCrc32");
        await db.DisposeAsync();
        // 句柄由连接池持有，不排空的话 Windows 上删文件/重开可能撞 sharing violation。
        SqliteConnection.ClearAllPools();

        // 换一个工厂实例 = 重启进程：升级只在该进程第一次打开这个月库时发生。
        var restarted = new RuntimeDbFactory(workspace.Path("runtime"));
        var reopened = restarted.Open(monthKey);
        var columns = await ColumnNamesAsync(reopened, "CollectRecords");
        await reopened.DisposeAsync();

        Assert.Contains("ArchivePath", columns);
        Assert.Contains("ArchiveFileSize", columns);
        Assert.Contains("ArchiveCrc32", columns);
    }

    private static async Task<List<string>> ColumnNamesAsync(DbContext db, string table)
    {
        var names = new List<string>();
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    // ---------- 采集流水线：逐点位来源 ----------

    /// <summary>
    /// 把演示工站改造成"整站都取自文件"：点位改成 JSON 字段名，并把来源切到文件。
    /// </summary>
    /// <remarks>
    /// 来源是按点位选的，所以这里逐个改；工站上只留一个文件路径（一台设备写一个文件）。
    /// </remarks>
    private static Station PrepareFileStation(CollectHarness harness)
    {
        var station = harness.Station(0);
        station.DataFilePath = Path.Combine(harness.Root, "device", "result.json");
        var pressure = station.Tags.Single(t => t.Name == "压力");
        var temperature = station.Tags.Single(t => t.Name == "工站温度");
        pressure.Address = "force";
        temperature.Address = "temp";
        pressure.Source = TagDataSource.JsonFile;
        temperature.Source = TagDataSource.JsonFile;
        return station;
    }

    private static void WriteSource(Station station, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(station.DataFilePath)!);
        File.WriteAllText(station.DataFilePath, json, new UTF8Encoding(false));
    }

    /// <summary>文件源工站仍由 PLC 触发、托盘码也从 PLC 读，只有点位值不在 PLC 里。</summary>
    private static void Trigger(CollectHarness harness, Station station, string palletCode)
    {
        harness.Simulator.SetAscii(station.PalletCodeAddress, palletCode, station.PalletCodeLength, harness.Plc.StringHighByteFirst);
        harness.Simulator.Trigger(station.TriggerAddress, station.TriggerValue);
    }

    private static async Task<CollectRecord> FullRecordAsync(CollectHarness harness, string palletCode)
    {
        var item = Assert.Single((await harness.QueryAsync(palletCode)).Items);
        var record = await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id);
        Assert.NotNull(record);
        return record!;
    }

    /// <summary>同一托盘触发多次时取最新那条（列表按 TriggerTime 倒序）。</summary>
    private static async Task<CollectRecord> LatestRecordAsync(CollectHarness harness, string palletCode)
    {
        var item = (await harness.QueryAsync(palletCode)).Items[0];
        var record = await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id);
        Assert.NotNull(record);
        return record!;
    }

    [Fact]
    public async Task File_source_values_are_judged_by_the_same_limits_and_archived()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = PrepareFileStation(harness);
        WriteSource(station, """{"force": 12.5, "temp": 40}""");
        Trigger(harness, station, "P0080");

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var record = await FullRecordAsync(harness, "P0080");
        Assert.Equal(Judgement.Ok, record.Judgement);
        Assert.Equal(12.5d, record.TagValues.Single(v => v.TagName == "压力").NumericValue!.Value, precision: 4);
        Assert.Equal(40d, record.TagValues.Single(v => v.TagName == "工站温度").NumericValue!.Value, precision: 4);
        // 试点位的型号覆盖、预警带判定照旧生效（走的是同一套生效限值）。
        Assert.False(record.TagValues.Single(v => v.TagName == "压力").IsOutOfLimit);

        Assert.NotEmpty(record.ArchivePath);
        Assert.True(record.ArchiveFileSize > 0);
        Assert.NotEqual(0u, record.ArchiveCrc32);

        // 设备随后写了下一件：归档里必须还是上一件那一份，才算"归档是原件"。
        WriteSource(station, """{"force": 1, "temp": 1}""");
        var archives = harness.Scope.ServiceProvider.GetRequiredService<ICollectArchiveStore>();
        var bytes = await archives.ReadAsync(record.ArchivePath, record.ArchiveCrc32);
        Assert.Contains("12.5", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task File_source_is_re_read_on_every_trigger()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = PrepareFileStation(harness);

        WriteSource(station, """{"force": 12, "temp": 40}""");
        Trigger(harness, station, "P0081");
        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        // 同一秒内覆写、mtime 不变也照样要重读：绝不沿用上一件的值。
        WriteSource(station, """{"force": 25, "temp": 40}""");
        Trigger(harness, station, "P0081");
        Assert.Equal(ResultCodes.DataValidationFailed, await harness.RunAsync(station));

        var query = await harness.QueryAsync("P0081");
        Assert.Equal(2, query.Total);
        var latest = await LatestRecordAsync(harness, "P0081");
        Assert.Equal(25d, latest.TagValues.Single(v => v.TagName == "压力").NumericValue!.Value, precision: 4);
        Assert.Equal(Judgement.Ng, latest.Judgement);
    }

    [Fact]
    public async Task File_source_field_is_read_by_name_even_if_it_looks_like_a_plc_address()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = PrepareFileStation(harness);
        // PLC 模式下 M100 是位地址、必然失败；文件源下它只是一个字段名。
        station.Tags.Single(t => t.Name == "工站温度").Address = "M100";
        WriteSource(station, """{"force": 12, "M100": 40}""");
        Trigger(harness, station, "P0082");

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var record = await FullRecordAsync(harness, "P0082");
        Assert.Equal(40d, record.TagValues.Single(v => v.TagName == "工站温度").NumericValue!.Value, precision: 4);
    }

    [Fact]
    public async Task Curves_still_come_from_the_plc_for_file_source_stations()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = PrepareFileStation(harness);
        WriteSource(station, """{"force": 12, "temp": 40}""");
        // 曲线不走文件：它的采样值仍然从 PLC 地址读。
        var ySeries = station.Curves.Single().Series.Single(s => s.Role == SeriesRole.Y);
        harness.Simulator.SetFloat(ySeries.StartAddress, 9.5f, harness.Plc.FloatWordOrder);
        Trigger(harness, station, "P0083");

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var record = await FullRecordAsync(harness, "P0083");
        var curve = Assert.Single(record.Curves);
        Assert.Equal(50, curve.PointCount);
        Assert.NotEmpty(curve.Features);
        Assert.True(curve.Features.Single(f => f.Role == SeriesRole.Y).Peak > 0);
    }

    [Fact]
    public async Task Missing_file_fails_the_cycle_without_saving_anything()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = PrepareFileStation(harness);
        // 设备还没写过文件：读不到就整次失败，绝不拿上一件的值凑合。
        Trigger(harness, station, "P0084");

        Assert.Equal(ResultCodes.FileSourceFailed, await harness.RunAsync(station));
        Assert.Equal(0, (await harness.QueryAsync("P0084")).Total);

        var status = harness.StatusHub.Stations.Single();
        Assert.Equal(StationRuntimeState.Fault, status.State);
        Assert.Contains("数据文件读取失败", status.LastError);
    }

    [Fact]
    public async Task Broken_json_fails_the_cycle_but_keeps_the_raw_file_archived()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = PrepareFileStation(harness);
        WriteSource(station, """{"force": 12, "temp": """);
        Trigger(harness, station, "P0085");

        Assert.Equal(ResultCodes.FileSourceFailed, await harness.RunAsync(station));
        Assert.Equal(0, (await harness.QueryAsync("P0085")).Total);

        // 内容坏掉时，设备到底写了什么恰恰是最需要的证据：归档必须留下，且失败原因里指明路径。
        var archived = Directory.GetFiles(Path.Combine(harness.Root, "archive"), "*.json", SearchOption.AllDirectories);
        Assert.Single(archived);
        var status = harness.StatusHub.Stations.Single();
        Assert.Contains("不是合法 JSON", status.LastError);
        Assert.Contains("已归档", status.LastError);
    }

    [Fact]
    public async Task Archive_failure_fails_the_cycle_with_the_archive_code()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = PrepareFileStation(harness);
        WriteSource(station, """{"force": 12, "temp": 40}""");
        // 数据盘坏了/写不进去：归档失败按约定算采集失败（整次不落库、回写明确失败码）。
        var archiveRoot = Path.Combine(harness.Root, "archive");
        Directory.Delete(archiveRoot, recursive: true);
        await File.WriteAllTextAsync(archiveRoot, "占位，模拟归档目录不可用");
        Trigger(harness, station, "P0086");

        Assert.Equal(ResultCodes.ArchiveFailed, await harness.RunAsync(station));
        Assert.Equal(0, (await harness.QueryAsync("P0086")).Total);

        var status = harness.StatusHub.Stations.Single();
        Assert.Equal(StationRuntimeState.Fault, status.State);
        Assert.Contains("原始数据归档失败", status.LastError);
    }

    [Fact]
    public async Task Missing_required_field_fails_like_an_empty_plc_tag()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = PrepareFileStation(harness);
        station.Tags.Single(t => t.Name == "压力").IsRequired = true;
        WriteSource(station, """{"temp": 40}""");
        Trigger(harness, station, "P0087");

        Assert.Equal(ResultCodes.DataValidationFailed, await harness.RunAsync(station));

        var record = await FullRecordAsync(harness, "P0087");
        var force = record.TagValues.Single(v => v.TagName == "压力");
        Assert.Null(force.NumericValue);
        // 必填取空按超规格处理，与 PLC 侧一致（明细页按结果码与原因解释这一行）。
        Assert.True(force.IsOutOfLimit);
        Assert.Equal(Judgement.Ng, record.Judgement);
    }

    [Fact]
    public async Task Missing_optional_field_is_recorded_as_empty_without_failing()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = PrepareFileStation(harness);
        station.Tags.Single(t => t.Name == "工站温度").IsRequired = false;
        WriteSource(station, """{"force": 12}""");
        Trigger(harness, station, "P0088");

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var record = await FullRecordAsync(harness, "P0088");
        Assert.Null(record.TagValues.Single(v => v.TagName == "工站温度").NumericValue);
        Assert.Equal(Judgement.Ok, record.Judgement);
    }

    /// <summary>
    /// 混用来源：一个工站里既有 PLC 报的点位，也有从文件读的点位。
    /// </summary>
    /// <remarks>
    /// 这是"按点位选来源"存在的理由：智能传感器导出的力值走文件、PLC 报的保压时间走寄存器。
    /// 两侧都必须落到同一条记录里，且归档只归那份文件。
    /// </remarks>
    [Fact]
    public async Task Station_can_mix_plc_tags_and_file_tags_in_one_cycle()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        station.DataFilePath = Path.Combine(harness.Root, "device", "result.json");
        // 压力改走文件，温度照旧走 PLC 寄存器（地址不动）。
        var pressure = station.Tags.Single(t => t.Name == "压力");
        pressure.Address = "force.peak";
        pressure.Source = TagDataSource.JsonFile;

        WriteSource(station, """{"force": {"peak": 12.5}}""");
        // 温度寄存器由"设备"（这里用模拟器）写，与文件无关。
        harness.Simulator.SetFloat(
            station.Tags.Single(t => t.Name == "工站温度").Address,
            42f,
            harness.Plc.FloatWordOrder);
        Trigger(harness, station, "P0090");

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var record = await FullRecordAsync(harness, "P0090");
        Assert.Equal(12.5d, record.TagValues.Single(v => v.TagName == "压力").NumericValue!.Value, precision: 4);
        Assert.Equal(42d, record.TagValues.Single(v => v.TagName == "工站温度").NumericValue!.Value, precision: 3);
        // 归档只针对那份文件。
        Assert.NotEmpty(record.ArchivePath);
    }

    /// <summary>
    /// 有文件源点位时文件读不到 → 整次失败，哪怕同站还有能读到的 PLC 点位。
    /// </summary>
    /// <remarks>
    /// 这一件的点位集是不完整的，落一条缺了关键点位的记录比不落更危险。
    /// </remarks>
    [Fact]
    public async Task Mixed_station_fails_the_whole_cycle_when_the_file_is_missing()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        station.DataFilePath = Path.Combine(harness.Root, "device", "never-written.json");
        var pressure = station.Tags.Single(t => t.Name == "压力");
        pressure.Address = "force";
        pressure.Source = TagDataSource.JsonFile;
        Trigger(harness, station, "P0091");

        Assert.Equal(ResultCodes.FileSourceFailed, await harness.RunAsync(station));
        Assert.Equal(0, (await harness.QueryAsync("P0091")).Total);
    }

    /// <summary>停用的文件源点位不该拖着整站去读文件：它不参与采集，也就没有"读不到"。</summary>
    [Fact]
    public async Task Disabled_file_tag_does_not_require_the_file()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        station.DataFilePath = Path.Combine(harness.Root, "device", "never-written.json");
        var temperature = station.Tags.Single(t => t.Name == "工站温度");
        temperature.Address = "temp";
        temperature.Source = TagDataSource.JsonFile;
        temperature.Enabled = false;
        SimulatorScenario.LoadStationCycle(harness.Simulator, harness.Plc, station, "P0092");

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var record = await FullRecordAsync(harness, "P0092");
        Assert.Empty(record.ArchivePath);
        Assert.DoesNotContain(record.TagValues, v => v.TagName == "工站温度");
    }

    [Fact]
    public async Task Plc_source_stations_keep_reading_tags_from_their_addresses()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        // 回归：新增的文件源分支不能把 PLC 取值的默认路径改掉。
        Assert.All(station.Tags, t => Assert.Equal(TagDataSource.Plc, t.Source));
        SimulatorScenario.LoadStationCycle(harness.Simulator, harness.Plc, station, "P0089");

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var record = await FullRecordAsync(harness, "P0089");
        Assert.NotNull(record.TagValues.Single(v => v.TagName == "压力").NumericValue);
        Assert.Empty(record.ArchivePath);
        Assert.Equal(0, record.ArchiveFileSize);
    }

    [Fact]
    public async Task Csv_source_reads_the_single_data_row_by_column_name()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = PrepareFileStation(harness);
        station.DataFileFormat = DataFileFormat.Csv;
        WriteSource(station, "force,temp\n12.5,40\n");
        Trigger(harness, station, "P0093");

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var record = await FullRecordAsync(harness, "P0093");
        Assert.Equal(12.5d, record.TagValues.Single(v => v.TagName == "压力").NumericValue!.Value, precision: 4);
        Assert.Equal(40d, record.TagValues.Single(v => v.TagName == "工站温度").NumericValue!.Value, precision: 4);
        var archives = harness.Scope.ServiceProvider.GetRequiredService<ICollectArchiveStore>();
        var bytes = await archives.ReadAsync(record.ArchivePath, record.ArchiveCrc32);
        Assert.Contains("force,temp", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Csv_with_more_than_one_data_row_fails_but_keeps_the_archive()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = PrepareFileStation(harness);
        station.DataFileFormat = DataFileFormat.Csv;
        WriteSource(station, "force,temp\n12,40\n13,41\n");
        Trigger(harness, station, "P0094");

        Assert.Equal(ResultCodes.FileSourceFailed, await harness.RunAsync(station));
        Assert.Equal(0, (await harness.QueryAsync("P0094")).Total);
        var status = harness.StatusHub.Stations.Single();
        Assert.Contains("只能有表头和一行数据", status.LastError);
        Assert.Contains("已归档", status.LastError);
    }
}
