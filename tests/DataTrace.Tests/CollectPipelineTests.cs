using DataTrace.Application.Realtime;
using DataTrace.Application.Runtime;
using DataTrace.Collector;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Infrastructure.Persistence;
using DataTrace.Plc.Codec;
using DataTrace.Plc.Queue;
using DataTrace.Plc.Simulator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Tests;

/// <summary>
/// 采集流水线端到端：从模拟 PLC 触发到落库、写回响应码、实时状态广播。
/// 全部走真实的 SQLite 月库与曲线文件，只有 PLC 用内存模拟器替代。
/// </summary>
public class CollectPipelineTests
{
    /// <summary>按种子装载一轮完整工站数据（含曲线），后续断言都以此为基础。</summary>
    private static void Load(CollectHarness harness, Station station, string palletCode, bool injectNg = false, int seed = 7)
        => SimulatorScenario.LoadStationCycle(harness.Simulator, harness.Plc, station, palletCode, new SimulatedCycleOptions
        {
            Random = new Random(seed),
            InjectNg = injectNg
        });

    private static float ReadFloat(CollectHarness harness, string address)
        => ValueCodec.DecodeFloat(
            new[] { harness.Simulator.GetWord(address), harness.Simulator.GetWord(Shift(address)) },
            harness.Plc.FloatWordOrder);

    private static string Shift(string address)
    {
        var prefix = new string(address.TakeWhile(char.IsLetter).ToArray());
        var number = int.Parse(address[prefix.Length..]);
        return $"{prefix}{number + 1}";
    }

    [Fact]
    public async Task First_station_collect_saves_record_and_writes_success()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        Load(harness, station, "P0001");

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var query = await harness.QueryAsync("P0001");
        Assert.True(query.Total >= 1);
        Assert.Equal("P0001", query.Items[0].Record.PalletCode);
        Assert.Equal(ResultCodes.Success, query.Items[0].Record.ResultCode);
        Assert.Equal(1, station.PositionCount);
        var detailed = await harness.RuntimeStore.GetRecordAsync(query.Items[0].MonthKey, query.Items[0].Record.Id);
        Assert.Single(detailed!.Products);
    }

    [Fact]
    public async Task Simulated_line_walks_all_stations_and_can_inject_ng()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var stations = harness.Stations;

        foreach (var station in stations)
        {
            Load(harness, station, "P0008", seed: 7);
            Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));
        }

        var okQuery = await harness.QueryAsync("P0008");
        Assert.Equal(3, okQuery.Total);

        var ngStation = stations[1];
        Load(harness, stations[0], "P0009", seed: 3);
        await harness.RunAsync(stations[0]);
        Load(harness, ngStation, "P0009", injectNg: true, seed: 3);
        Assert.Equal(ResultCodes.DataValidationFailed, await harness.RunAsync(ngStation));
    }

    [Fact]
    public async Task Full_line_closes_session_and_clears_active_index()
    {
        await using var harness = await CollectHarness.CreateAsync();

        foreach (var station in harness.Stations)
        {
            Load(harness, station, "P0010");
            await harness.RunAsync(station);
        }

        var query = await harness.QueryAsync("P0010");
        Assert.Equal(3, query.Total);
        var sessionId = query.Items[0].Record.PalletSessionId;
        Assert.True(sessionId > 0);

        var sessions = harness.Scope.ServiceProvider.GetRequiredService<IActiveSessionStore>();
        Assert.Null(await sessions.FindByPalletAsync("P0010"));

        var session = await harness.RuntimeStore.GetSessionAsync(query.Items[0].MonthKey, sessionId);
        Assert.NotNull(session);
        Assert.Equal(SessionStatus.Closed, session!.Status);
        Assert.Equal(query.Items[0].Record.SerialNo, session.SerialNo);
        Assert.NotNull(session.EndTime);

        // 三个工站共用同一序列号，说明会话被正确复用而非重复建。
        Assert.Single(query.Items.Select(i => i.Record.SerialNo).Distinct());
    }

    [Fact]
    public async Task Invalid_pallet_code_is_rejected_before_storage()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        Load(harness, station, "P0001");
        // 把托盘码改写成含非法字符，模拟现场条码读偏导致读到脏字符。
        harness.Simulator.SetAscii(station.PalletCodeAddress, "***", station.PalletCodeLength, harness.Plc.StringHighByteFirst);

        Assert.Equal(ResultCodes.InvalidPalletCode, await harness.RunAsync(station));

        var query = await harness.QueryAsync("***");
        Assert.Equal(0, query.Total);

        var status = harness.StatusHub.Stations.Single();
        Assert.Equal(ResultCodes.InvalidPalletCode, status.LastResultCode);
        Assert.Equal(Judgement.Ng, status.LastJudgement);
        Assert.Equal(StationRuntimeState.Idle, status.State);
    }

    [Fact]
    public async Task Empty_pallet_code_is_rejected()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        Load(harness, station, "P0001");
        harness.Simulator.SetAscii(station.PalletCodeAddress, "", station.PalletCodeLength, harness.Plc.StringHighByteFirst);

        Assert.Equal(ResultCodes.InvalidPalletCode, await harness.RunAsync(station));
        Assert.Equal(0, (await harness.QueryAsync("")).Total);
    }

    [Fact]
    public async Task Plc_read_failure_writes_read_failed_and_marks_station_fault()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        Load(harness, station, "P0001");
        harness.Simulator.SetFailReads(true);

        Assert.Equal(ResultCodes.PlcReadFailed, await harness.RunAsync(station));

        var status = harness.StatusHub.Stations.Single();
        Assert.Equal(StationRuntimeState.Fault, status.State);
        Assert.Equal(ResultCodes.PlcReadFailed, status.LastResultCode);
        Assert.NotNull(status.LastError);
        Assert.Equal(0, (await harness.QueryAsync("P0001")).Total);
    }

    [Fact]
    public async Task Station_without_active_session_reports_process_abnormal()
    {
        await using var harness = await CollectHarness.CreateAsync();
        // 直接跑第二工站：没有首站开的会话，等价于现场跳站。
        var station = harness.Station(1);
        Load(harness, station, "P0500");

        Assert.Equal(ResultCodes.ProcessAbnormal, await harness.RunAsync(station));

        var query = await harness.QueryAsync("P0500");
        var record = Assert.Single(query.Items).Record;
        Assert.Equal(Judgement.Ng, record.Judgement);
        Assert.Contains("跳站", record.ErrorMessage);
        Assert.StartsWith("ORPHAN-", record.SerialNo);
    }

    [Fact]
    public async Task Unoccupied_position_is_recorded_as_empty_product()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        station.Positions.First().OccupiedAddress = "D1099";
        Load(harness, station, "P0002");
        // 有料信号为 0 → 该工位本轮空载。
        harness.Simulator.SetWord("D1099", 0);

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var item = Assert.Single((await harness.QueryAsync("P0002")).Items);
        var record = await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id);
        Assert.NotNull(record);
        var product = Assert.Single(record!.Products);
        Assert.Equal(1, product.PositionIndex);
        Assert.False(product.Occupied);
        Assert.Equal(Judgement.None, product.Judgement);
        Assert.Equal(Judgement.None, record.Judgement);
    }

    [Fact]
    public async Task Out_of_limit_tag_fails_validation_and_records_reason()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        Load(harness, station, "P0003");
        // 覆盖压力点位为超上限值（上限 20kN）。
        harness.Simulator.SetFloat("D1100", 999f, harness.Plc.FloatWordOrder);

        Assert.Equal(ResultCodes.DataValidationFailed, await harness.RunAsync(station));

        var item = Assert.Single((await harness.QueryAsync("P0003")).Items);
        Assert.Equal(Judgement.Ng, item.Record.Judgement);
        var record = await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id);
        Assert.NotNull(record);
        var product = Assert.Single(record!.Products);
        Assert.Equal(Judgement.Ng, product.Judgement);
        Assert.Contains("超限", product.NgReason);

        var pressure = await harness.RuntimeStore.GetRecordAsync(item.MonthKey, record.Id);
        Assert.NotNull(pressure);
        var tag = Assert.Single(pressure!.TagValues.Where(t => t.TagCode == "ST010_P1"));
        Assert.True(tag.IsOutOfLimit);
        Assert.Equal(999d, tag.NumericValue);
    }

    [Fact]
    public async Task Curves_are_externalized_and_surfaced_in_live_status()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        Load(harness, station, "P0004");

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var item = Assert.Single((await harness.QueryAsync("P0004")).Items);
        // 列表查询不带导航集合，曲线引用需要按记录主键回查。
        var record = await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id);
        var curveRecord = Assert.Single(record!.Curves);
        Assert.Equal("ST010_PD", curveRecord.CurveCode);
        Assert.Equal(50, curveRecord.PointCount);
        Assert.StartsWith("20", curveRecord.RelativePath);
        Assert.True(curveRecord.FileSize > 0);
        Assert.NotEqual(0u, curveRecord.Crc32);

        var store = harness.Scope.ServiceProvider.GetRequiredService<ICurveFileStore>();
        var payload = await store.ReadAsync(curveRecord.RelativePath);
        Assert.Equal(50, payload.PointCount);
        Assert.Equal(2, payload.Series.Count);
        Assert.Contains(payload.Series, s => s.Role == SeriesRole.Y);
        Assert.Contains(payload.Series, s => s.Role == SeriesRole.X);

        // 实时看板直接拿到 Y 序列用于画图，不需要再去读曲线文件。
        var status = harness.StatusHub.Stations.Single();
        var live = Assert.Single(status.LastCurves);
        Assert.Equal("位移压力曲线", live.Name);
        Assert.Equal(50, live.Values.Length);
        Assert.Equal(2, status.LastTags.Count);
    }

    /// <summary>
    /// 点数非法的曲线（历史/手工改库留下的 PointCount = 0）必须被整条跳过：
    /// 读计划本来就不给它安排读取，求值侧若照样跑，会拿全 0 特征去判据，
    /// 把一条本来合格的记录拖成 NG，还写下一个指向错误的判废原因。
    /// </summary>
    [Fact]
    public async Task Curve_with_invalid_point_count_is_skipped_instead_of_failing_the_record()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        station.Curves.First().PointCount = 0;
        Load(harness, station, "P0004");

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var item = Assert.Single((await harness.QueryAsync("P0004")).Items);
        Assert.Equal(Judgement.Ok, item.Record.Judgement);
        // 也不该为它留下一条空曲线记录（点数 0、无特征）。
        var record = await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id);
        Assert.Empty(record!.Curves);
    }

    /// <summary>
    /// 位地址必须明确失败，而不是静默按 0 采集。
    /// </summary>
    /// <remarks>
    /// 读计划只收字地址，位地址会被丢掉：点位会读到空数组（Bool 恒 false）、
    /// 有料点位会被当成"未占用"、触发位则永远不触发。这些表现都指向"设备/程序有问题"，
    /// 现场很难想到是地址写法的事，所以这里要求工站状态上留下明确原因。
    /// </remarks>
    [Fact]
    public async Task BitAddress_for_a_tag_fails_with_an_explicit_reason()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        station.Tags.First().Address = "M100";
        Load(harness, station, "P0006");

        Assert.Equal(ResultCodes.PlcReadFailed, await harness.RunAsync(station));

        // 读不到数据就不能判定，因此这条不落采集记录，只在工站状态上报错。
        var status = harness.StatusHub.Stations.Single(x => x.StationId == station.Id);
        Assert.Equal(StationRuntimeState.Fault, status.State);
        Assert.Contains("位地址", status.LastError);
        Assert.Contains("M100", status.LastError);
    }

    [Fact]
    public async Task BitAddress_for_the_pallet_code_fails_with_an_explicit_reason()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        station.PalletCodeAddress = "M200";
        Load(harness, station, "P0007");

        Assert.Equal(ResultCodes.PlcReadFailed, await harness.RunAsync(station));

        var status = harness.StatusHub.Stations.Single(x => x.StationId == station.Id);
        Assert.Contains("位地址", status.LastError);
        Assert.Contains("托盘码", status.LastError);
    }

    [Fact]
    public async Task Mes_outbox_is_enqueued_only_for_successful_last_station()
    {
        await using var harness = await CollectHarness.CreateAsync(configureMes: true);

        foreach (var station in harness.Stations)
        {
            Load(harness, station, "P0005");
            await harness.RunAsync(station);
        }

        using var scope = harness.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        var outbox = await db.MesOutbox.ToListAsync();

        var item = Assert.Single(outbox);
        Assert.Equal(MesOutboxStatus.Pending, item.Status);
        Assert.Equal("P0005", item.PalletCode);
        Assert.Equal(0, item.AttemptCount);
        Assert.Contains(item.SerialNo, item.PayloadJson);
    }

    [Fact]
    public async Task Ng_line_does_not_enqueue_mes_payload()
    {
        await using var harness = await CollectHarness.CreateAsync(configureMes: true);
        var stations = harness.Stations;
        Load(harness, stations[0], "P0006");
        await harness.RunAsync(stations[0]);
        Load(harness, stations[1], "P0006", injectNg: true, seed: 3);
        await harness.RunAsync(stations[1]);

        using var scope = harness.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        Assert.Empty(await db.MesOutbox.ToListAsync());
    }

    [Fact]
    public async Task Reuploading_same_pallet_marks_previous_session_abnormal()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);

        Load(harness, station, "P0007");
        await harness.RunAsync(station);
        var first = Assert.Single((await harness.QueryAsync("P0007")).Items).Record;

        // 同一托盘码二次上料：旧的未关闭会话必须被标记异常并让位。
        Load(harness, station, "P0007", seed: 11);
        await harness.RunAsync(station);

        var query = await harness.QueryAsync("P0007");
        Assert.Equal(2, query.Total);
        Assert.Equal(2, query.Items.Select(i => i.Record.SerialNo).Distinct().Count());

        var previous = await harness.RuntimeStore.GetSessionAsync(query.Items[0].MonthKey, first.PalletSessionId);
        Assert.NotNull(previous);
        Assert.Equal(SessionStatus.Abnormal, previous!.Status);
        Assert.NotNull(previous.EndTime);

        var sessions = harness.Scope.ServiceProvider.GetRequiredService<IActiveSessionStore>();
        var active = await sessions.FindByPalletAsync("P0007");
        Assert.NotNull(active);
        Assert.Equal(query.Items[0].Record.SerialNo, active!.SerialNo);
    }

    [Fact]
    public async Task Station_status_and_event_feed_are_updated_after_collect()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        Load(harness, station, "P0011");

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var status = Assert.Single(harness.StatusHub.Stations);
        Assert.Equal(station.Code, status.StationCode);
        Assert.Equal(station.Name, status.StationName);
        Assert.Equal(station.Sequence, status.Sequence);
        Assert.Equal(StationRuntimeState.Idle, status.State);
        Assert.Equal("P0011", status.LastPalletCode);
        Assert.Equal(ResultCodes.Success, status.LastResultCode);
        Assert.Equal(Judgement.Ok, status.LastJudgement);
        Assert.True(status.LastRecordId > 0);
        Assert.NotNull(status.LastCompleteTime);
        Assert.NotNull(status.LastMonthKey);
        Assert.Equal(2, status.LastTags.Count);

        var feed = Assert.Single(harness.StatusHub.Recent);
        Assert.Equal("P0011", feed.PalletCode);
        Assert.Equal(ResultCodes.Success, feed.ResultCode);
        Assert.Equal(status.LastRecordId, feed.RecordId);
    }

    [Fact]
    public async Task Disabled_stations_are_not_part_of_the_seeded_line()
    {
        await using var harness = await CollectHarness.CreateAsync();

        // 演示产线是 3 工站、首尾已标记，采集侧依赖这两个标记判断会话开关。
        Assert.Equal(3, harness.Stations.Count);
        Assert.True(harness.Stations[0].IsFirstStation);
        Assert.True(harness.Stations[^1].IsLastStation);
        Assert.All(harness.Stations, s => Assert.True(s.Enabled));
        Assert.All(harness.Stations, s => Assert.Equal(1, s.PositionCount));
        Assert.All(harness.Stations, s => Assert.Equal(new[] { 0, 1 }, s.Tags.Select(t => t.PositionIndex).OrderBy(i => i).ToArray()));
        Assert.All(harness.Stations, s => Assert.Single(s.Curves));
    }

    [Fact]
    public async Task Seeded_demo_line_limits_satisfy_the_write_path_rule()
    {
        await using var harness = await CollectHarness.CreateAsync();

        // 演示数据是 Seeder 直接写 EF 的，绕开仓储里那道限值一致性校验；而现场第一眼看到、
        // 照着改的就是这套数据 —— 它自己黄线压红线，等于把脏配置当范例发出去。
        foreach (var tag in harness.Stations.SelectMany(s => s.Tags))
        {
            Assert.Null(TagLimits.From(tag).ConsistencyError());
        }

        foreach (var recipe in harness.Snapshot.Recipes)
        {
            foreach (var tag in harness.Stations.SelectMany(s => s.Tags))
            {
                Assert.Null(RecipeLimitResolver.Resolve(tag, recipe).ConsistencyError());
            }
        }
    }

    [Fact]
    public async Task Tag_scale_and_offset_are_applied_when_decoding()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        var tag = station.Tags.Single(t => t.Code == "ST010_P1");
        tag.Scale = 2;
        tag.Offset = -1;
        Load(harness, station, "P0013");
        // 寄存器原始值 10 → 工程量 10 * 2 + (-1) = 19，落在 5–20 限内。
        harness.Simulator.SetFloat("D1100", 10f, harness.Plc.FloatWordOrder);

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var item = Assert.Single((await harness.QueryAsync("P0013")).Items);
        var record = await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id);
        var pressure = Assert.Single(record!.TagValues.Where(t => t.TagCode == "ST010_P1"));
        Assert.Equal(19d, pressure.NumericValue!.Value, precision: 4);
        Assert.False(pressure.IsOutOfLimit);

        // 未配置缩放的点位直接透传原始值。
        var temperature = record.TagValues.Single(t => t.TagCode == "ST010_TEMP");
        Assert.Equal(ReadFloat(harness, "D1110"), temperature.NumericValue!.Value, precision: 4);
    }

    [Fact]
    public async Task Curve_features_are_extracted_and_persisted_with_the_record()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        Load(harness, station, "P0020");

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var item = Assert.Single((await harness.QueryAsync("P0020")).Items);
        var record = await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id);
        var curve = Assert.Single(record!.Curves);

        // 每条序列一行特征，与曲线文件同属一次采集的产物。
        Assert.Equal(2, curve.Features.Count);
        var y = curve.Features.Single(f => f.Role == SeriesRole.Y);
        var x = curve.Features.Single(f => f.Role == SeriesRole.X);
        Assert.Equal("压力", y.SeriesName);
        Assert.Equal("位移", x.SeriesName);
        Assert.Equal(50, y.PointCount);
        Assert.Equal(50, x.PointCount);

        // 压力序列为 7.5 + amp*sin(...) + 噪声，amp ∈ [3.5, 6]、噪声 < 0.15，
        // 因此峰值必然落在 7.5 ~ 13.65，且波形平滑（相邻点跳变远小于 1）。
        Assert.InRange(y.Peak, 7.5d, 13.7d);
        Assert.InRange(y.Min, 1.3d, 7.7d);
        Assert.True(y.Mean > 0);
        Assert.True(y.Area > 0);
        Assert.True(y.StdDev > 0);
        Assert.InRange(y.PeakIndex, 0, 49);
        Assert.True(y.MaxStep < 1d);
        Assert.True(y.Peak >= y.Mean && y.Mean >= y.Min);

        // 位移序列 = 采样比例 × (4.5 ~ 5.5)，首点为 0、末点接近满量程，
        // 因此峰值为 4.5 ~ 5.5、最小值恰为 0、整体上升斜率为正。
        Assert.Equal(0d, x.Min);
        Assert.InRange(x.Peak, 4.5d, 5.5d);
        Assert.True(x.RiseSlope > 0);
        Assert.InRange(x.FallRatio, 0d, 0.25d);
    }

    [Fact]
    public async Task Violated_curve_criterion_fails_validation_with_waveform_reason()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        // 峰值下限 999 对任何一条模拟曲线都不可能满足。
        station.Curves.Single().Criteria.Add(new CurveCriterion { PeakMin = 999 });
        Load(harness, station, "P0021");

        Assert.Equal(ResultCodes.DataValidationFailed, await harness.RunAsync(station));

        var item = Assert.Single((await harness.QueryAsync("P0021")).Items);
        Assert.Equal(Judgement.Ng, item.Record.Judgement);
        var record = await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id);
        Assert.NotNull(record);
        var product = Assert.Single(record!.Products);
        Assert.Equal(Judgement.Ng, product.Judgement);
        Assert.Contains("波形异常", product.NgReason);
        Assert.Contains("峰值", product.NgReason);
    }

    [Fact]
    public async Task Satisfied_curve_criterion_keeps_the_record_ok()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        station.Curves.Single().Criteria.Add(new CurveCriterion
        {
            PeakMin = 0,
            PeakMax = 1000,
            AreaMin = -1e9,
            AreaMax = 1e9,
            MaxStepMax = 1000,
            StdDevMax = 1000
        });
        Load(harness, station, "P0022");

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));
        var record = Assert.Single((await harness.QueryAsync("P0022")).Items).Record;
        Assert.Equal(Judgement.Ok, record.Judgement);
    }

    [Fact]
    public async Task Blank_series_name_applies_to_the_y_series_only()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        // 未指定序列名 → 只作用于主序列（Y 压力，峰值 7.5~13.65）。
        // 位移序列峰值仅 4.5~5.5，6 这个阈值把两条序列干净地分开：
        // 若判据被误作用到 X 上，本轮就会被判不合格。
        station.Curves.Single().Criteria.Add(new CurveCriterion { PeakMin = 6 });
        Load(harness, station, "P0023");

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));
    }

    [Fact]
    public async Task Curve_criterion_can_target_a_named_series()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        // 显式指向位移序列：峰值上限 1 一定被突破。
        station.Curves.Single().Criteria.Add(new CurveCriterion { SeriesName = "位移", PeakMax = 1 });
        Load(harness, station, "P0024");

        Assert.Equal(ResultCodes.DataValidationFailed, await harness.RunAsync(station));

        var item = Assert.Single((await harness.QueryAsync("P0024")).Items);
        var record = await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id);
        Assert.Contains("波形异常", Assert.Single(record!.Products).NgReason);
    }

    [Fact]
    public async Task Disabled_curve_criterion_is_ignored()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        station.Curves.Single().Criteria.Add(new CurveCriterion { Enabled = false, PeakMin = 999 });
        Load(harness, station, "P0025");

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));
    }

    [Fact]
    public async Task Tag_limit_reason_takes_precedence_over_waveform_reason()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        station.Curves.Single().Criteria.Add(new CurveCriterion { PeakMin = 999 });
        Load(harness, station, "P0026");
        // 同时把压力点位打到超上限，点位原因应当成为首因。
        harness.Simulator.SetFloat("D1100", 999f, harness.Plc.FloatWordOrder);

        Assert.Equal(ResultCodes.DataValidationFailed, await harness.RunAsync(station));

        var item = Assert.Single((await harness.QueryAsync("P0026")).Items);
        var product = Assert.Single((await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id))!.Products);
        Assert.Contains("超限", product.NgReason);
        Assert.DoesNotContain("波形异常", product.NgReason);
    }

    [Fact]
    public async Task Criteria_saved_to_the_config_database_are_applied_on_the_next_cycle()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var curveId = harness.Station(0).Curves.Single().Id;

        // 走真实配置库：界面保存判据 → 快照刷新 → 采集器按新判据判定。
        await harness.ConfigRepository.SaveCurveCriteriaAsync(curveId, [new CurveCriterion { PeakMin = 999 }]);
        await harness.RefreshSnapshotAsync();

        var refreshed = harness.Snapshot.Stations.OrderBy(s => s.Sequence).First();
        Assert.Single(refreshed.Curves.Single().Criteria);

        Load(harness, refreshed, "P0030");
        Assert.Equal(ResultCodes.DataValidationFailed, await harness.RunAsync(refreshed));

        var item = Assert.Single((await harness.QueryAsync("P0030")).Items);
        var product = Assert.Single((await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id))!.Products);
        Assert.Contains("波形异常", product.NgReason);

        // 从界面清空判据后，判定应当退回到只看点位限值。
        await harness.ConfigRepository.SaveCurveCriteriaAsync(curveId, []);
        await harness.RefreshSnapshotAsync();

        var cleared = harness.Snapshot.Stations.OrderBy(s => s.Sequence).First();
        Assert.Empty(cleared.Curves.Single().Criteria);

        Load(harness, cleared, "P0031");
        Assert.Equal(ResultCodes.Success, await harness.RunAsync(cleared));
    }

    [Fact]
    public async Task Tag_in_the_warning_band_keeps_the_record_ok_but_is_flagged()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        // 规格限 5~20，黄线 18：19 在规格内、但已进入预警带。
        station.Tags.Single(t => t.Code == "ST010_P1").WarningUpperLimit = 18;
        Load(harness, station, "P0040");
        harness.Simulator.SetFloat("D1100", 19f, harness.Plc.FloatWordOrder);

        // 预警不判废：响应码仍是成功，不能让 PLC 因为黄区停车。
        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var item = Assert.Single((await harness.QueryAsync("P0040")).Items);
        Assert.Equal(Judgement.Ok, item.Record.Judgement);

        var full = await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id);
        var pressure = Assert.Single(full!.TagValues.Where(t => t.TagCode == "ST010_P1"));
        Assert.True(pressure.IsWarning);
        Assert.False(pressure.IsOutOfLimit);
        Assert.Equal(19d, pressure.NumericValue!.Value, precision: 3);

        // 看板拿到的实时状态同样要带出预警标记（黄区样式据此渲染）。
        var live = Assert.Single(Assert.Single(harness.StatusHub.Stations).LastTags.Where(t => t.Name == "压力"));
        Assert.True(live.Warning);
        Assert.False(live.OutOfLimit);
    }

    [Fact]
    public async Task Crossing_the_spec_limit_outranks_the_warning_band()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        station.Tags.Single(t => t.Code == "ST010_P1").WarningUpperLimit = 18;
        Load(harness, station, "P0042");
        // 21 同时越过了规格上限与预警上限：必须按超规格处理，不能只报预警。
        harness.Simulator.SetFloat("D1100", 21f, harness.Plc.FloatWordOrder);

        Assert.Equal(ResultCodes.DataValidationFailed, await harness.RunAsync(station));

        var item = Assert.Single((await harness.QueryAsync("P0042")).Items);
        var full = await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id);
        var pressure = Assert.Single(full!.TagValues.Where(t => t.TagCode == "ST010_P1"));
        Assert.True(pressure.IsOutOfLimit);
        Assert.False(pressure.IsWarning);
    }

    [Fact]
    public async Task Required_string_tag_is_validated_as_text_not_as_a_missing_number()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        // 回归：字符串点位没有数值限值，此前会被当成「数值取空」直接判超限，
        // 等于任何必填字符串点位永远判废。
        station.Tags.Add(new TagDefinition
        {
            Code = "ST010_MODEL",
            Name = "产品型号",
            Address = "D1120",
            DataType = PlcDataType.String,
            Length = 8,
            PositionIndex = 0,
            IsRequired = true
        });
        Load(harness, station, "P0041");

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var item = Assert.Single((await harness.QueryAsync("P0041")).Items);
        var full = await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id);
        var model = Assert.Single(full!.TagValues.Where(t => t.TagCode == "ST010_MODEL"));
        Assert.Equal("OK", model.TextValue);
        Assert.False(model.IsOutOfLimit);
        Assert.False(model.IsWarning);
    }

    [Fact]
    public async Task Empty_required_string_tag_still_fails_validation()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        station.Tags.Add(new TagDefinition
        {
            Code = "ST010_MODEL",
            Name = "产品型号",
            Address = "D1120",
            DataType = PlcDataType.String,
            Length = 8,
            PositionIndex = 0,
            IsRequired = true
        });
        Load(harness, station, "P0043");
        // 修正字符串点位「永远判废」之后，必填校验必须依然有效。
        harness.Simulator.SetAscii("D1120", "", 8, harness.Plc.StringHighByteFirst);

        Assert.Equal(ResultCodes.DataValidationFailed, await harness.RunAsync(station));
    }

    [Fact]
    public async Task Switching_the_active_recipe_changes_the_judgement()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        var recipe = harness.Snapshot.Recipes.Single(r => r.Code == "A100");

        // 未选择型号：压力 19 落在点位默认规格限 5~20 之内，只是进了预警带。
        Load(harness, station, "P0050");
        harness.Simulator.SetFloat("D1100", 19f, harness.Plc.FloatWordOrder);
        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));
        Assert.Equal("", Assert.Single((await harness.QueryAsync("P0050")).Items).Record.RecipeCode);

        // 切到 A100（规格上限收紧到 16）后，同一个值直接判废。
        await harness.ConfigRepository.SetActiveRecipeAsync(recipe.Id);
        await harness.RefreshSnapshotAsync();
        var switched = harness.Snapshot.Stations.OrderBy(s => s.Sequence).First();

        Load(harness, switched, "P0051");
        harness.Simulator.SetFloat("D1100", 19f, harness.Plc.FloatWordOrder);
        Assert.Equal(ResultCodes.DataValidationFailed, await harness.RunAsync(switched));

        var ngItem = Assert.Single((await harness.QueryAsync("P0051")).Items);
        Assert.Equal(Judgement.Ng, ngItem.Record.Judgement);
        // 判定依据必须能回溯：记录里要写清当时用的是哪个型号。
        Assert.Equal("A100", ngItem.Record.RecipeCode);
        var ng = await harness.RuntimeStore.GetRecordAsync(ngItem.MonthKey, ngItem.Record.Id);
        Assert.Contains("超限", Assert.Single(ng!.Products).NgReason);

        // 取消选择后又回到默认限值。
        await harness.ConfigRepository.SetActiveRecipeAsync(null);
        await harness.RefreshSnapshotAsync();
        var cleared = harness.Snapshot.Stations.OrderBy(s => s.Sequence).First();

        Load(harness, cleared, "P0052");
        harness.Simulator.SetFloat("D1100", 19f, harness.Plc.FloatWordOrder);
        Assert.Equal(ResultCodes.Success, await harness.RunAsync(cleared));
        Assert.Equal("", Assert.Single((await harness.QueryAsync("P0052")).Items).Record.RecipeCode);
    }

    [Fact]
    public async Task Recipe_can_tighten_a_warning_band_without_failing_the_part()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var recipe = harness.Snapshot.Recipes.Single(r => r.Code == "A100");
        await harness.ConfigRepository.SetActiveRecipeAsync(recipe.Id);
        await harness.RefreshSnapshotAsync();
        var station = harness.Snapshot.Stations.OrderBy(s => s.Sequence).First();

        // A100 把工站温度的预警上限收紧到 45℃（红线仍是 80℃）。
        Load(harness, station, "P0053");
        harness.Simulator.SetFloat("D1110", 50f, harness.Plc.FloatWordOrder);

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var item = Assert.Single((await harness.QueryAsync("P0053")).Items);
        var full = await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id);
        var temperature = Assert.Single(full!.TagValues.Where(t => t.TagCode == "ST010_TEMP"));
        Assert.True(temperature.IsWarning);
        Assert.False(temperature.IsOutOfLimit);
    }

    [Fact]
    public async Task Persisted_limits_are_the_resolved_ones_not_the_tag_defaults()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Snapshot.Stations.OrderBy(s => s.Sequence).First();
        var tagDefaultUpper = station.Tags.Single(t => t.Code == "ST010_P1").UpperLimit;

        // A100 把压力规格上限收紧到 16，点位自身默认是 20。
        var recipe = harness.Snapshot.Recipes.Single(r => r.Code == "A100");
        await harness.ConfigRepository.SetActiveRecipeAsync(recipe.Id);
        await harness.RefreshSnapshotAsync();
        var active = harness.Snapshot.Stations.OrderBy(s => s.Sequence).First();

        Load(harness, active, "P0054");
        harness.Simulator.SetFloat("D1100", 19f, harness.Plc.FloatWordOrder);
        Assert.Equal(ResultCodes.DataValidationFailed, await harness.RunAsync(active));

        var item = Assert.Single((await harness.QueryAsync("P0054")).Items);
        var full = await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id);
        var pressure = Assert.Single(full!.TagValues.Where(t => t.TagCode == "ST010_P1"));

        Assert.NotNull(tagDefaultUpper);
        Assert.Equal(16d, pressure.UpperLimit!.Value, precision: 3);
        Assert.NotEqual(tagDefaultUpper!.Value, pressure.UpperLimit.Value);
        // 没被覆盖的字段沿用点位默认值，也必须照样记下来。
        Assert.Equal(5d, pressure.LowerLimit!.Value, precision: 3);
    }

    [Fact]
    public async Task Later_limit_edits_do_not_rewrite_history()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Snapshot.Stations.OrderBy(s => s.Sequence).First();
        station.Tags.Single(t => t.Code == "ST010_P1").WarningUpperLimit = 18;

        Load(harness, station, "P0055");
        harness.Simulator.SetFloat("D1100", 19f, harness.Plc.FloatWordOrder);
        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var item = Assert.Single((await harness.QueryAsync("P0055")).Items);
        var key = (item.MonthKey, item.Record.Id);

        var before = await harness.RuntimeStore.GetRecordAsync(key.Item1, key.Item2);
        var recorded = Assert.Single(before!.TagValues.Where(t => t.TagCode == "ST010_P1"));
        Assert.Equal(18d, recorded.WarningUpperLimit!.Value, precision: 3);

        // 工程师事后把限值改了：已经落库的那条记录不能被重新解释，
        // 否则追溯时看到的"规格限"其实是今天的配置，等于篡改历史。
        station.Tags.Single(t => t.Code == "ST010_P1").WarningUpperLimit = 90;
        station.Tags.Single(t => t.Code == "ST010_P1").UpperLimit = 999;

        var after = await harness.RuntimeStore.GetRecordAsync(key.Item1, key.Item2);
        var unchanged = Assert.Single(after!.TagValues.Where(t => t.TagCode == "ST010_P1"));
        Assert.Equal(18d, unchanged.WarningUpperLimit!.Value, precision: 3);
        Assert.Equal(20d, unchanged.UpperLimit!.Value, precision: 3);
    }
}
