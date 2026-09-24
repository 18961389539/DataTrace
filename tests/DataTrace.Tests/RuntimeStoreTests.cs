using DataTrace.Application.Runtime;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Infrastructure.Persistence;
using DataTrace.Infrastructure.Storage;

namespace DataTrace.Tests;

/// <summary>运行库读写：事务落库、跨月查询、会话生命周期、曲线外置。</summary>
public class RuntimeStoreTests
{
    private static readonly DateTime Day1 = new(2026, 9, 19, 8, 0, 0, DateTimeKind.Local);

    private sealed class RuntimeEnv : IAsyncDisposable
    {
        private readonly TempWorkspace _workspace;
        private readonly ConfigDbContext _config;

        private RuntimeEnv(TempWorkspace workspace, ConfigDbContext config, RuntimeDbFactory factory, CurveFileStore curves, ActiveSessionStore sessions, RuntimeStore store)
        {
            _workspace = workspace;
            _config = config;
            Factory = factory;
            Curves = curves;
            Sessions = sessions;
            Store = store;
        }

        public RuntimeDbFactory Factory { get; }

        public CurveFileStore Curves { get; }

        public ActiveSessionStore Sessions { get; }

        public RuntimeStore Store { get; }

        public string CurveRoot => Path.Combine(_workspace.Root, "curves");

        public static async Task<RuntimeEnv> CreateAsync()
        {
            var workspace = new TempWorkspace();
            var config = await TestDatabase.CreateConfigAsync(workspace.Path("config.db"));
            var factory = new RuntimeDbFactory(workspace.Path("runtime"));
            var curves = new CurveFileStore(workspace.Path("curves"));
            var sessions = new ActiveSessionStore(config);
            return new RuntimeEnv(workspace, config, factory, curves, sessions, new RuntimeStore(factory, curves, sessions));
        }

        public async ValueTask DisposeAsync()
        {
            await _config.DisposeAsync();
            _workspace.Dispose();
        }
    }

    /// <summary>
    /// 月库补列必须真跑一次才敢信：EnsureCreated 对已存在的文件是空操作，
    /// 老月份库只能靠 AddColumnIfMissing 补上，否则 EF 一读点位就 no such column，
    /// 而且只在打开"上个月那个旧文件"时才炸——新库全绿也发现不了。
    /// </summary>
    [Fact]
    public async Task Existing_month_db_gains_the_persisted_limit_columns()
    {
        string[] columns = ["LowerLimit", "UpperLimit", "WarningLowerLimit", "WarningUpperLimit"];
        const string monthKey = "202609";

        using var workspace = new TempWorkspace();
        var root = workspace.Path("runtime");
        var path = new RuntimeDbFactory(root).GetPath(monthKey);

        await using (var created = new RuntimeDbFactory(root).Open(monthKey))
        {
            // 必须真的把列名写进查询里：Any() 会编译成 SELECT EXISTS，一个列名都不碰，
            // 缺列也照样绿 —— 那等于什么都没测。
            ReadLimitColumns(created);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // 模拟老版本留下的月份库：把新加的四列去掉。
        await using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            await raw.OpenAsync();
            foreach (var column in columns)
            {
                await using var drop = raw.CreateCommand();
                drop.CommandText = $"""ALTER TABLE "TagValues" DROP COLUMN "{column}" """;
                await drop.ExecuteNonQueryAsync();
            }
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // 换一个新工厂实例（等价于新进程）重新打开同一个文件：必须补回来并且读得动。
        await using (var reopened = new RuntimeDbFactory(root).Open(monthKey))
        {
            ReadLimitColumns(reopened);
        }

        var present = new List<string>();
        await using (var check = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            await check.OpenAsync();
            await using var probe = check.CreateCommand();
            probe.CommandText = "SELECT name FROM pragma_table_info('TagValues')";
            await using var reader = await probe.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                present.Add(reader.GetString(0));
            }
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        Assert.All(columns, column => Assert.Contains(column, present, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 用一次真的引用列名的查询探月库结构。
    /// 老月份库缺列时 EF 会抛 no such column —— 而 Any() 编译成 SELECT EXISTS，
    /// 一个列名都不碰，缺列也照样返回成功，所以不能用它当探针。
    /// </summary>
    private static void ReadLimitColumns(RuntimeDbContext ctx)
        => ctx.TagValues
            .Select(t => new { t.LowerLimit, t.UpperLimit, t.WarningLowerLimit, t.WarningUpperLimit })
            .ToList();

    private static CollectRecord BuildRecord(
        string palletCode,
        string serialNo,
        DateTime triggerTime,
        Judgement judgement = Judgement.Ok,
        int stationId = 10,
        string stationCode = "ST010",
        double? pressure = 12.5,
        long sessionId = 0)
    {
        var outOfLimit = judgement == Judgement.Ng;
        return new CollectRecord
        {
            PalletSessionId = sessionId,
            SerialNo = serialNo,
            PalletCode = palletCode,
            StationId = stationId,
            StationCode = stationCode,
            TriggerTime = triggerTime,
            CompleteTime = triggerTime.AddMilliseconds(120),
            DurationMs = 120,
            ResultCode = ResultCodes.Success,
            Judgement = judgement,
            Products = [new ProductRecord { PositionIndex = 1, Occupied = true, Judgement = judgement, NgReason = outOfLimit ? "压力超限" : null }],
            TagValues =
            [
                new TagValue
                {
                    TagId = stationId * 10 + 1,
                    TagCode = $"{stationCode}_P1",
                    TagName = "压力",
                    PositionIndex = 1,
                    DataType = PlcDataType.Float,
                    NumericValue = pressure,
                    IsOutOfLimit = outOfLimit
                }
            ]
        };
    }

    private static CollectSaveRequest FirstStation(string monthKey, string palletCode, string serialNo, DateTime triggerTime, Judgement judgement = Judgement.Ok, bool withCurve = false)
        => new()
        {
            MonthKey = monthKey,
            UpsertSession = new PalletSession { SerialNo = serialNo, PalletCode = palletCode, StartTime = triggerTime, Status = SessionStatus.Open },
            ActiveSession = new ActiveSessionIndex { PalletCode = palletCode, SerialNo = serialNo, MonthKey = monthKey, StartTime = triggerTime },
            Record = BuildRecord(palletCode, serialNo, triggerTime, judgement),
            Curves = withCurve
                ?
                [
                    new CurvePayloadWrite
                    {
                        Record = new CurveRecord { CurveDefinitionId = 1, CurveCode = "ST010_PD", CurveName = "位移压力曲线", PositionIndex = 1, PointCount = 3 },
                        Payload = new CurvePayload
                        {
                            PointCount = 3,
                            Series = [new CurveSeriesPayload { Name = "压力", Role = SeriesRole.Y, Values = [1f, 2f, 3f] }]
                        }
                    }
                ]
                : []
        };

    private static CollectSaveRequest FollowingStation(
        string monthKey,
        string palletCode,
        string serialNo,
        DateTime triggerTime,
        long sessionId,
        bool close = false,
        Judgement judgement = Judgement.Ok,
        int stationId = 30,
        string stationCode = "ST030")
        => new()
        {
            MonthKey = monthKey,
            CloseSession = close,
            RemoveActiveSession = close,
            Record = BuildRecord(palletCode, serialNo, triggerTime, judgement, stationId, stationCode, sessionId: sessionId)
        };

    private static CollectQueryRequest Query(string? pallet = null, string? serial = null, int? stationId = null, Judgement? judgement = null, int skip = 0, int take = 50)
        => new()
        {
            From = new DateTime(2026, 1, 1),
            To = new DateTime(2026, 12, 31, 23, 59, 59),
            PalletCode = pallet,
            SerialNo = serial,
            StationId = stationId,
            Judgement = judgement,
            Skip = skip,
            Take = take
        };

    [Fact]
    public async Task First_station_save_persists_record_session_and_curve_file()
    {
        await using var env = await RuntimeEnv.CreateAsync();
        var request = FirstStation("202609", "P0001", "20260919-000001", Day1, withCurve: true);

        await env.Store.SaveAsync(request);

        Assert.True(request.Record.Id > 0);
        Assert.True(request.Record.PalletSessionId > 0);

        var curveRecord = Assert.Single(request.Record.Curves);
        Assert.StartsWith("2026/09/19/", curveRecord.RelativePath);
        Assert.True(curveRecord.FileSize > 0);
        Assert.NotEqual(0u, curveRecord.Crc32);

        var payload = await env.Curves.ReadAsync(curveRecord.RelativePath);
        Assert.Equal(3, payload.PointCount);

        // 在制索引必须落到配置库，跨月重放时才找得到运行库。
        var active = await env.Sessions.FindByPalletAsync("P0001");
        Assert.NotNull(active);
        Assert.Equal(request.Record.PalletSessionId, active!.SessionId);
        Assert.Equal("202609", active.MonthKey);
    }

    [Fact]
    public async Task Save_without_curve_writes_nothing_to_curve_store()
    {
        await using var env = await RuntimeEnv.CreateAsync();
        await env.Store.SaveAsync(FirstStation("202609", "P0001", "S1", Day1));

        Assert.Empty(Directory.GetFiles(env.CurveRoot, "*.curve", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Query_returns_record_header_without_products()
    {
        await using var env = await RuntimeEnv.CreateAsync();
        await env.Store.SaveAsync(FirstStation("202609", "P0001", "S1", Day1));

        var result = await env.Store.QueryAsync(Query(pallet: "P0001"));

        Assert.Equal(1, result.Total);
        var item = Assert.Single(result.Items);
        Assert.Equal("202609", item.MonthKey);
        Assert.Equal("P0001", item.Record.PalletCode);
        Assert.Equal(ResultCodes.Success, item.Record.ResultCode);
        Assert.Equal(Judgement.Ok, item.Record.Judgement);
        // 列表查询刻意不 Include Products，避免导出万行时拉齐整图。
        Assert.Empty(item.Record.Products);
    }

    [Fact]
    public async Task Query_filters_by_station_judgement_and_serial()
    {
        await using var env = await RuntimeEnv.CreateAsync();
        await env.Store.SaveAsync(FirstStation("202609", "P0001", "20260919-000001", Day1));
        await env.Store.SaveAsync(FirstStation("202609", "P0002", "20260919-000002", Day1.AddMinutes(1), Judgement.Ng));
        var ok = await env.Store.QueryAsync(Query(pallet: "P0001"));
        var okSessionId = ok.Items[0].Record.PalletSessionId;
        await env.Store.SaveAsync(FollowingStation("202609", "P0001", "20260919-000001", Day1.AddMinutes(2), okSessionId, judgement: Judgement.Ok, stationId: 20, stationCode: "ST020"));

        Assert.Equal(2, (await env.Store.QueryAsync(Query(stationId: 10))).Total);
        Assert.Equal(1, (await env.Store.QueryAsync(Query(stationId: 20))).Total);
        Assert.Equal(1, (await env.Store.QueryAsync(Query(judgement: Judgement.Ng))).Total);
        Assert.Equal(2, (await env.Store.QueryAsync(Query(serial: "20260919-000001"))).Total);
        Assert.Equal(1, (await env.Store.QueryAsync(Query(pallet: "P0002"))).Total);
    }

    [Fact]
    public async Task Query_paginates_and_reports_full_total()
    {
        await using var env = await RuntimeEnv.CreateAsync();
        for (var i = 1; i <= 5; i++)
        {
            await env.Store.SaveAsync(FirstStation("202609", $"P000{i}", $"S{i}", Day1.AddMinutes(i)));
        }

        var page = await env.Store.QueryAsync(Query(take: 2, skip: 2));

        Assert.Equal(5, page.Total);
        Assert.Equal(2, page.Items.Count);
        // 多库分页时月份库按倒序拼接，同月内按触发时间倒序。
        Assert.Equal(new[] { "P0003", "P0002" }, page.Items.Select(i => i.Record.PalletCode).ToArray());
    }

    [Fact]
    public async Task Query_spans_multiple_month_databases()
    {
        await using var env = await RuntimeEnv.CreateAsync();
        await env.Store.SaveAsync(FirstStation("202601", "P0001", "S1", new DateTime(2026, 1, 5, 8, 0, 0)));
        await env.Store.SaveAsync(FirstStation("202607", "P0002", "S2", new DateTime(2026, 7, 5, 8, 0, 0)));

        var result = await env.Store.QueryAsync(Query());

        Assert.Equal(2, result.Total);
        Assert.Equal(new[] { "202607", "202601" }, result.Items.Select(i => i.MonthKey).ToArray());
    }

    [Fact]
    public async Task Query_outside_time_range_returns_nothing()
    {
        await using var env = await RuntimeEnv.CreateAsync();
        await env.Store.SaveAsync(FirstStation("202609", "P0001", "S1", Day1));

        var result = await env.Store.QueryAsync(new CollectQueryRequest
        {
            From = new DateTime(2026, 10, 1),
            To = new DateTime(2026, 10, 31)
        });

        Assert.Equal(0, result.Total);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Query_for_missing_month_directory_returns_nothing()
    {
        await using var env = await RuntimeEnv.CreateAsync();
        var result = await env.Store.QueryAsync(Query());
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task Get_record_includes_products_tags_and_curves()
    {
        await using var env = await RuntimeEnv.CreateAsync();
        var request = FirstStation("202609", "P0001", "S1", Day1, withCurve: true);
        await env.Store.SaveAsync(request);

        var loaded = await env.Store.GetRecordAsync("202609", request.Record.Id);

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Products);
        Assert.Single(loaded.TagValues);
        Assert.Single(loaded.Curves);
        Assert.Equal(12.5, loaded.TagValues.Single().NumericValue);
    }

    [Fact]
    public async Task Get_record_returns_null_for_missing_month_or_id()
    {
        await using var env = await RuntimeEnv.CreateAsync();
        var request = FirstStation("202609", "P0001", "S1", Day1);
        await env.Store.SaveAsync(request);

        Assert.Null(await env.Store.GetRecordAsync("202608", request.Record.Id));
        Assert.Null(await env.Store.GetRecordAsync("202609", 999_999));
    }

    [Fact]
    public async Task Get_session_returns_its_records_with_products()
    {
        await using var env = await RuntimeEnv.CreateAsync();
        var first = FirstStation("202609", "P0001", "20260919-000001", Day1);
        await env.Store.SaveAsync(first);
        var sessionId = first.Record.PalletSessionId;
        await env.Store.SaveAsync(FollowingStation("202609", "P0001", "20260919-000001", Day1.AddMinutes(1), sessionId, stationId: 20, stationCode: "ST020"));
        await env.Store.SaveAsync(FollowingStation("202609", "P0001", "20260919-000001", Day1.AddMinutes(2), sessionId, close: true));

        var session = await env.Store.GetSessionAsync("202609", sessionId);

        Assert.NotNull(session);
        Assert.Equal(SessionStatus.Closed, session!.Status);
        Assert.Equal(3, session.Records.Count);
        Assert.All(session.Records, r => Assert.Single(r.Products));
        Assert.Null(await env.Store.GetSessionAsync("202608", sessionId));
        Assert.Null(await env.Store.GetSessionAsync("202609", 999_999));
    }

    [Fact]
    public async Task Session_records_are_ordered_by_trigger_time()
    {
        await using var env = await RuntimeEnv.CreateAsync();
        var first = FirstStation("202609", "P0001", "S1", Day1);
        await env.Store.SaveAsync(first);
        var sessionId = first.Record.PalletSessionId;
        await env.Store.SaveAsync(FollowingStation("202609", "P0001", "S1", Day1.AddMinutes(5), sessionId, stationId: 30, stationCode: "ST030"));
        await env.Store.SaveAsync(FollowingStation("202609", "P0001", "S1", Day1.AddMinutes(2), sessionId, stationId: 20, stationCode: "ST020"));

        var records = await env.Store.GetSessionRecordsAsync("202609", sessionId);

        Assert.Equal(new[] { "ST010", "ST020", "ST030" }, records.Select(r => r.StationCode).ToArray());
        Assert.Empty(await env.Store.GetSessionRecordsAsync("202608", sessionId));
    }

    [Fact]
    public async Task Closing_session_clears_active_index()
    {
        await using var env = await RuntimeEnv.CreateAsync();
        var first = FirstStation("202609", "P0001", "S1", Day1);
        await env.Store.SaveAsync(first);
        Assert.NotNull(await env.Sessions.FindByPalletAsync("P0001"));

        await env.Store.SaveAsync(FollowingStation("202609", "P0001", "S1", Day1.AddMinutes(3), first.Record.PalletSessionId, close: true));

        Assert.Null(await env.Sessions.FindByPalletAsync("P0001"));
        var session = await env.Store.GetSessionAsync("202609", first.Record.PalletSessionId);
        Assert.NotNull(session);
        Assert.Equal(SessionStatus.Closed, session!.Status);
        Assert.NotNull(session.EndTime);
    }

    [Fact]
    public async Task Mark_session_abnormal_sets_status_and_end_time()
    {
        await using var env = await RuntimeEnv.CreateAsync();
        var first = FirstStation("202609", "P0001", "S1", Day1);
        await env.Store.SaveAsync(first);

        var end = Day1.AddMinutes(9);
        await env.Store.MarkSessionAbnormalAsync("202609", first.Record.PalletSessionId, end);

        var session = await env.Store.GetSessionAsync("202609", first.Record.PalletSessionId);
        Assert.NotNull(session);
        Assert.Equal(SessionStatus.Abnormal, session!.Status);
        Assert.Equal(end, session.EndTime);

        // 月份库或会话不存在时静默返回，不抛异常。
        await env.Store.MarkSessionAbnormalAsync("202608", first.Record.PalletSessionId, end);
        await env.Store.MarkSessionAbnormalAsync("202609", 999_999, end);
    }

    [Fact]
    public async Task Query_for_report_filters_by_time_and_keeps_tags()
    {
        await using var env = await RuntimeEnv.CreateAsync();
        await env.Store.SaveAsync(FirstStation("202609", "P0001", "S1", Day1));
        await env.Store.SaveAsync(FirstStation("202609", "P0002", "S2", Day1.AddDays(10), Judgement.Ng));

        var records = await env.Store.QueryForReportAsync(Day1, Day1.AddHours(1));

        var record = Assert.Single(records);
        Assert.Equal("P0001", record.PalletCode);
        Assert.Single(record.TagValues);
        Assert.Single(record.Products);
        Assert.Equal(2, (await env.Store.QueryForReportAsync(Day1, Day1.AddDays(30))).Count);
    }

    [Fact]
    public async Task Report_projections_read_narrow_columns_from_sqlite()
    {
        await using var env = await RuntimeEnv.CreateAsync();
        await env.Store.SaveAsync(FirstStation("202609", "P0001", "S1", Day1));
        await env.Store.SaveAsync(FirstStation("202609", "P0002", "S2", Day1.AddHours(2), Judgement.Ng));
        await env.Store.SaveAsync(FirstStation("202609", "P0003", "S3", Day1.AddDays(10)));

        // 吞吐投影：只带时间 / 工站 / 判定，区间过滤在 SQL 侧完成。
        var points = await env.Store.QueryJudgementPointsAsync(Day1, Day1.AddHours(3), null);
        Assert.Equal(2, points.Count);
        Assert.All(points, p => Assert.Equal(10, p.StationId));
        Assert.Single(points, p => p.Judgement == Judgement.Ng);

        // 工站过滤同样下推到 SQL。
        Assert.Empty(await env.Store.QueryJudgementPointsAsync(Day1, Day1.AddHours(3), 99));
        Assert.Equal(3, (await env.Store.QueryJudgementPointsAsync(Day1, Day1.AddDays(30), null)).Count);

        // 不良投影：只回带超限点位的名称与代码。
        var defects = await env.Store.QueryOutOfLimitTagsAsync(Day1, Day1.AddHours(3));
        var defect = Assert.Single(defects);
        Assert.Equal("压力", defect.TagName);
        Assert.Equal("ST010_P1", defect.TagCode);
        Assert.Empty(await env.Store.QueryOutOfLimitTagsAsync(Day1, Day1.AddHours(1)));

        // 趋势投影：按 TagId 过滤，时间升序，并带出托盘码。
        const int tagId = 10 * 10 + 1;
        var trend = await env.Store.QueryTagTrendAsync(Day1, Day1.AddDays(30), tagId);
        Assert.Equal(3, trend.Count);
        Assert.Equal(12.5d, trend[0].Value);
        Assert.Equal("P0001", trend[0].PalletCode);
        Assert.True(trend.Zip(trend.Skip(1)).All(p => p.First.Time <= p.Second.Time));
        Assert.Empty(await env.Store.QueryTagTrendAsync(Day1, Day1.AddDays(30), tagId + 99));

        // 规格限那两列是后加的：老行读出来必须是 null，不能兜成 0——
        // 兜成 0 会让"按当前规格限重算了多少点"的比对把每条老数据都算成口径不一致。
        Assert.All(trend, p => Assert.Null(p.LowerLimit));
        Assert.All(trend, p => Assert.Null(p.UpperLimit));
    }

    [Fact]
    public async Task Curve_features_round_trip_through_the_month_database()
    {
        await using var env = await RuntimeEnv.CreateAsync();
        var request = FirstStation("202609", "P0001", "S1", Day1, withCurve: true);
        var write = request.Curves[0];
        write.Record.Features =
        [
            CurveFeatureExtractor.ToEntity("压力", SeriesRole.Y, CurveFeatureExtractor.Extract([1f, 2f, 3f]))
        ];

        await env.Store.SaveAsync(request);

        var record = await env.Store.GetRecordAsync("202609", request.Record.Id);
        var curve = Assert.Single(record!.Curves);
        var feature = Assert.Single(curve.Features);
        Assert.Equal("压力", feature.SeriesName);
        Assert.Equal(SeriesRole.Y, feature.Role);
        Assert.Equal(3, feature.PointCount);
        Assert.Equal(3d, feature.Peak);
        Assert.Equal(2d, feature.Mean);
        Assert.Equal(4d, feature.Area);
        Assert.Equal(1d, feature.RiseSlope, 1e-6);

        // 未带特征时不应产生任何特征行。
        var plain = FirstStation("202609", "P0009", "S9", Day1.AddMinutes(30), withCurve: true);
        await env.Store.SaveAsync(plain);
        var plainRecord = await env.Store.GetRecordAsync("202609", plain.Record.Id);
        Assert.Empty(Assert.Single(plainRecord!.Curves).Features);
    }

    [Fact]
    public async Task Curve_feature_projection_filters_by_definition_series_time_and_take()
    {
        await using var env = await RuntimeEnv.CreateAsync();

        await SaveCurveFeature(env, "P0001", Day1, definition: 1, "压力", SeriesRole.Y, peak: 12);
        await SaveCurveFeature(env, "P0002", Day1.AddMinutes(1), definition: 1, "压力", SeriesRole.Y, peak: 13);
        await SaveCurveFeature(env, "P0003", Day1.AddMinutes(2), definition: 1, "位移", SeriesRole.X, peak: 5);
        await SaveCurveFeature(env, "P0004", Day1.AddMinutes(3), definition: 2, "压力", SeriesRole.Y, peak: 99);

        var from = Day1.AddHours(-1);
        var to = Day1.AddHours(1);

        // 只回该曲线定义该序列的行，且按时间升序（时间序列的相邻关系必须真实）。
        var pressure = await env.Store.QueryCurveFeaturesAsync(1, "压力", from, to, 50);
        Assert.Equal(2, pressure.Count);
        Assert.Equal(new[] { "P0001", "P0002" }, pressure.Select(p => p.PalletCode).ToArray());
        Assert.All(pressure, p => Assert.Equal(SeriesRole.Y, p.Role));
        Assert.Equal(12d, pressure[0].Feature.Peak);
        Assert.Equal(Day1, pressure[0].Time);

        // take 取的是最新若干条。
        var latest = await env.Store.QueryCurveFeaturesAsync(1, "压力", from, to, 1);
        Assert.Equal("P0002", Assert.Single(latest).PalletCode);

        // 不限定序列名 → 该定义的全部序列，且不会串到别的曲线定义。
        Assert.Equal(3, (await env.Store.QueryCurveFeaturesAsync(1, null, from, to, 50)).Count);

        // 时间窗下推到 SQL：只有落在窗内的那条。
        var narrow = await env.Store.QueryCurveFeaturesAsync(1, null, Day1, Day1.AddSeconds(30), 50);
        Assert.Equal("P0001", Assert.Single(narrow).PalletCode);

        // take <= 0 直接返回空，不做无意义的查询。
        Assert.Empty(await env.Store.QueryCurveFeaturesAsync(1, null, from, to, 0));
    }

    private static async Task SaveCurveFeature(
        RuntimeEnv env,
        string palletCode,
        DateTime time,
        int definition,
        string seriesName,
        SeriesRole role,
        double peak)
    {
        var request = FirstStation("202609", palletCode, palletCode, time, withCurve: true);
        request.Curves[0].Record.CurveDefinitionId = definition;
        request.Curves[0].Record.Features =
        [
            new CurveFeature { SeriesName = seriesName, Role = role, PointCount = 50, Peak = peak }
        ];
        await env.Store.SaveAsync(request);
    }

    [Fact]
    public async Task Tag_issue_projections_separate_warnings_from_defects()
    {
        await using var env = await RuntimeEnv.CreateAsync();

        // 预警行：判定仍然是 OK，只是落在黄区。
        var warned = FirstStation("202609", "P0001", "S1", Day1);
        warned.Record.TagValues.First().IsWarning = true;
        await env.Store.SaveAsync(warned);

        // 不良行：超规格。
        await env.Store.SaveAsync(FirstStation("202609", "P0002", "S2", Day1.AddHours(1), Judgement.Ng));

        var warnings = await env.Store.QueryWarningTagsAsync(Day1, Day1.AddDays(1));
        var warning = Assert.Single(warnings);
        Assert.Equal("压力", warning.TagName);
        Assert.Equal("ST010_P1", warning.TagCode);

        // 两类标记互不串台。
        Assert.Single(await env.Store.QueryOutOfLimitTagsAsync(Day1, Day1.AddDays(1)));
        Assert.Empty(await env.Store.QueryWarningTagsAsync(Day1.AddHours(2), Day1.AddDays(1)));

        // 补列迁移生效：IsWarning 能写进月库并读回。
        var stored = await env.Store.GetRecordAsync("202609", warned.Record.Id);
        Assert.True(Assert.Single(stored!.TagValues).IsWarning);
    }

    [Fact]
    public async Task Delete_month_removes_database_and_its_records()
    {
        await using var env = await RuntimeEnv.CreateAsync();
        var request = FirstStation("202609", "P0001", "S1", Day1);
        await env.Store.SaveAsync(request);
        Assert.True(env.Factory.Exists("202609"));

        // 连接池里的空闲连接会占住 .db 文件句柄，先清池再验证整月删除。
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await env.Store.DeleteMonthAsync("202609");

        Assert.False(env.Factory.Exists("202609"));
        Assert.Null(await env.Store.GetRecordAsync("202609", request.Record.Id));
        Assert.Equal(0, (await env.Store.QueryAsync(Query())).Total);

        await env.Store.DeleteMonthAsync("202609");
    }

    [Fact]
    public async Task Failed_save_rolls_back_transaction_leaving_no_record()
    {
        await using var env = await RuntimeEnv.CreateAsync();

        var first = FirstStation("202609", "P0001", "S-UNIQUE", Day1, withCurve: true);
        await env.Store.SaveAsync(first);
        var keptFile = CurveFile(env, first);
        Assert.True(File.Exists(keptFile));

        // 会话序列号在月份库内有唯一索引，重复写入必须整体回滚。
        // 曲线代码换个名字：文件名里带序列号，两次写的是同一个序列号，
        // 不换代码的话两条曲线会落在同一个路径上，看不出回滚只清了自己写的那个。
        var duplicate = FirstStation("202609", "P0002", "S-UNIQUE", Day1.AddMinutes(1), withCurve: true);
        duplicate.Curves[0].Record.CurveCode = "ST010_PD2";
        await Assert.ThrowsAnyAsync<Exception>(() => env.Store.SaveAsync(duplicate));

        Assert.Equal(0, (await env.Store.QueryAsync(Query(pallet: "P0002"))).Total);
        Assert.Equal(1, (await env.Store.QueryAsync(Query(pallet: "P0001"))).Total);
        Assert.Null(await env.Sessions.FindByPalletAsync("P0002"));

        // 回滚还得把已经落盘的曲线文件收回去：曲线写在 CurveFileStore 的根目录下，
        // 而不是"工作目录/data/curves"——Windows 服务的工作目录是 System32，
        // 按后者拼路径的话这条清理会静默失效，只剩孤儿文件。
        Assert.NotEmpty(duplicate.Curves[0].Record.RelativePath!);
        Assert.False(File.Exists(CurveFile(env, duplicate)));

        // 且只删自己刚写的那个。
        Assert.True(File.Exists(keptFile));
    }

    private static string CurveFile(RuntimeEnv env, CollectSaveRequest request)
        => Path.Combine(env.CurveRoot,
            request.Curves[0].Record.RelativePath!.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Active_session_index_is_upserted_per_pallet()
    {
        await using var env = await RuntimeEnv.CreateAsync();
        await env.Store.SaveAsync(FirstStation("202609", "P0001", "S1", Day1));
        await env.Store.SaveAsync(FirstStation("202609", "P0002", "S2", Day1.AddMinutes(1)));

        var list = await env.Sessions.ListAsync();
        Assert.Equal(2, list.Count);

        // 同一托盘二次上料覆盖旧索引而不是新增。
        await env.Store.SaveAsync(FirstStation("202609", "P0001", "S3", Day1.AddMinutes(2)));
        Assert.Equal(2, (await env.Sessions.ListAsync()).Count);
        Assert.Equal("S3", (await env.Sessions.FindByPalletAsync("P0001"))!.SerialNo);
    }
}
