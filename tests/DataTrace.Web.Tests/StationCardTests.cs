using DataTrace.Application.Realtime;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Shared;
using DataTrace.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace DataTrace.Web.Tests;

/// <summary>
/// 工站卡片的重绘隔离。看板每 5 秒有一次定时重画（用来推进相对时间），
/// 卡片如果跟着每次都重画，点位、tooltip、迷你曲线会被全量 diff ——
/// 大屏常亮在工控机上时这是实打实的开销，所以这里把「数据变了才画」钉住。
/// </summary>
public class StationCardTests
{
    private static TestContext NewContext()
    {
        var ctx = new TestContext();
        // MudChip 渲染后要向 JS 注册按键拦截器，strict 模式下会因为没有计划的调用而抛。
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Render<MudPopoverProvider>();
        return ctx;
    }

    [Fact]
    public void Shift_counts_render_ok_and_ng()
    {
        var ctx = NewContext();
        var cut = ctx.Render<StationCard>(parameters => parameters
            .Add(card => card.Station, Station())
            .Add(card => card.ShiftCounts, new DataTrace.Application.Reporting.StationJudgementCount
            {
                StationId = 1,
                StationCode = "ST010",
                Ok = 12,
                Ng = 3
            }));

        Assert.Contains("OK 12", cut.Markup);
        Assert.Contains("NG 3", cut.Markup);
        Assert.Contains("合格率", cut.Markup);
        Assert.Contains(((12.0 / 15.0).ToString("P1")), cut.Markup);
    }

    private static StationRuntimeStatus Station(int? durationMs = 3200) => new()
    {
        StationId = 1,
        StationCode = "ST010",
        StationName = "上料工站",
        Sequence = 1,
        State = StationRuntimeState.Idle,
        LastJudgement = Judgement.Ok,
        LastPalletCode = "PAL-0001",
        LastSerialNo = "0007",
        LastDurationMs = durationMs,
        LastPieceGap = TimeSpan.FromMilliseconds(1250),
        LastTags = [new StationLiveTag { Name = "压力", Display = "12.40", Unit = "kN" }]
    };

    private static StationLiveTag Point(string name, bool outOfLimit = false, bool warning = false)
    {
        var value = outOfLimit ? 13.2 : warning ? 11.8 : 11.0;
        return new StationLiveTag
        {
            Name = name,
            Display = value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            Unit = "kN",
            NumericValue = value,
            LowerLimit = 10,
            UpperLimit = 12,
            WarningLowerLimit = 10.5,
            WarningUpperLimit = 11.5,
            OutOfLimit = outOfLimit,
            Warning = warning
        };
    }

    [Fact]
    public async Task Does_not_repaint_when_the_snapshot_is_unchanged()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, Station()));
        var before = cut.RenderCount;

        cut.Render(p => p.Add(x => x.Station, Station()));

        Assert.Equal(before, cut.RenderCount);
    }

    [Fact]
    public async Task Repaints_when_the_judgement_changes()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, Station()));
        var before = cut.RenderCount;

        var next = Station();
        next.LastJudgement = Judgement.Ng;
        cut.Render(p => p.Add(x => x.Station, next));

        Assert.True(cut.RenderCount > before);
    }

    [Fact]
    public async Task Repaints_when_a_tag_value_changes()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, Station()));
        var before = cut.RenderCount;

        var next = Station();
        next.LastTags = [new StationLiveTag { Name = "压力", Display = "13.10", Unit = "kN" }];
        cut.Render(p => p.Add(x => x.Station, next));

        Assert.True(cut.RenderCount > before);
    }

    /// <summary>默认只展示主曲线和 NG 曲线；展开后保留每条曲线自己的判定和异常原因。</summary>
    [Fact]
    public async Task Shows_all_curves_with_per_curve_judgement()
    {
        await using var ctx = NewContext();
        var station = Station();
        station.LastCurves =
        [
            new StationLiveCurve { Name = "压力曲线", Key = "1:1", Values = [1f, 2f, 3f], Judgement = Judgement.Ok },
            new StationLiveCurve { Name = "位移曲线", Key = "2:1", Values = [1f, 2f, 3f] },
            new StationLiveCurve
            {
                Name = "压装曲线", Key = "3:1", Values = [7f, 8f, 9f],
                Judgement = Judgement.Ng, JudgementReason = "压装曲线波形异常：峰值超基线 3σ"
            }
        ];

        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, station));

        // 默认只留主曲线和 NG 曲线，规格内/未判定的其余曲线先收起。
        Assert.Equal(2, cut.FindAll(".dt-station-curve").Count);
        Assert.Contains("压力曲线", cut.Markup);
        Assert.DoesNotContain("位移曲线", cut.Markup);
        Assert.Contains("压装曲线", cut.Markup);
        Assert.Contains("峰值超基线 3σ", cut.Find(".dt-curve-reason").TextContent);
        Assert.Equal("主要采集曲线", cut.Find(".dt-station-curves").GetAttribute("aria-label"));

        cut.Find(".dt-station-expand").Click();

        Assert.Equal(3, cut.FindAll(".dt-station-curve").Count);
        Assert.Equal(3, cut.FindComponents<StationSparkChart>().Count);
        Assert.Contains("位移曲线", cut.Markup);
        Assert.Contains("OK", cut.Markup);
        Assert.Contains("未判定", cut.Markup);
        Assert.Equal("全部采集曲线", cut.Find(".dt-station-curves").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll("[role=tablist]"));
    }

    /// <summary>换件后每条可见曲线都更新到同一份新快照。</summary>
    [Fact]
    public async Task All_curves_refresh_together_for_the_next_record()
    {
        await using var ctx = NewContext();
        var station = Station();
        station.LastCurves =
        [
            new StationLiveCurve { Name = "压力曲线", Key = "1:1", Values = [1f, 2f, 3f], Judgement = Judgement.Ok },
            new StationLiveCurve { Name = "位移曲线", Key = "2:1", Values = [4f, 5f, 6f], Judgement = Judgement.Ok }
        ];
        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, station));

        station.LastRecordId = 1;

        var next = Station();
        next.LastRecordId = 2;
        next.LastCurves =
        [
            new StationLiveCurve { Name = "压力曲线", Key = "1:1", Values = [2f, 3f, 4f], Judgement = Judgement.Ok },
            new StationLiveCurve { Name = "位移曲线", Key = "2:1", Values = [5f, 6f, 7f], Judgement = Judgement.Ok }
        ];
        cut.Render(p => p.Add(x => x.Station, next));
        cut.Find(".dt-station-expand").Click();

        var charts = cut.FindComponents<StationSparkChart>();
        Assert.Equal(2, charts.Count);
        Assert.Equal(new float[] { 2f, 3f, 4f }, charts[0].Instance.Values);
        Assert.Equal(new float[] { 5f, 6f, 7f }, charts[1].Instance.Values);
        Assert.All(charts, chart => Assert.Contains(":2:", chart.Instance.VersionKey));
    }

    /// <summary>无数据与未配置判定不能归为「正常」：汇总单独计数，行上带文字芯片。</summary>
    [Fact]
    public async Task Classifies_no_data_and_unjudged_points_separately()
    {
        await using var ctx = NewContext();
        var station = Station();
        station.LastTags =
        [
            Point("超限", outOfLimit: true),
            new StationLiveTag { Name = "没读到", Display = "-", NumericValue = null, TextValue = null },
            new StationLiveTag { Name = "没配限值", Display = "42", NumericValue = 42 }
        ];

        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, station));

        var summary = cut.Find(".dt-point-summary-label").TextContent;
        Assert.Contains("超限 1", summary);
        Assert.Contains("无数据 1", summary);
        Assert.Contains("未判定 1", summary);
        Assert.DoesNotContain("均正常", summary);
        // 未配限值先收起，避免一整站身份字段把详细卡撑满；汇总里仍然计数。
        Assert.Equal(2, cut.FindAll(".dash-tag").Count);
        Assert.Contains("无数据", cut.Markup);

        cut.Find(".dt-station-expand").Click();
        Assert.Equal(3, cut.FindAll(".dash-tag").Count);
        Assert.Contains("未判定", cut.FindAll(".dash-tag")[2].TextContent);
    }

    /// <summary>双状态：工站运行状态（采集中）与最近产品判定（上次 NG）分开显示；
    /// 采集中明确标注卡片数据是「最近完成」的，避免被当成当前件的实时值。</summary>
    [Fact]
    public async Task Busy_state_keeps_last_outcome_chip_and_labels_stale_data()
    {
        await using var ctx = NewContext();
        var station = Station();
        station.State = StationRuntimeState.Busy;
        station.LastJudgement = Judgement.Ng;

        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, station));

        Assert.Contains("采集中", cut.Markup);
        Assert.Contains("上次 NG", cut.Markup);
        Assert.Contains("最近完成", cut.Markup);
        Assert.Contains("dash-station detail busy", cut.Markup);
        Assert.DoesNotContain("dash-station detail ng", cut.Markup);
    }

    /// <summary>状态不能只靠颜色：色觉障碍用户分不出红绿边条时，图标与文字仍要能分辨。</summary>
    [Theory]
    [InlineData(Judgement.Ok, "OK")]
    [InlineData(Judgement.Ng, "NG")]
    public async Task Encodes_the_state_with_an_icon_on_top_of_the_colour(Judgement judgement, string label)
    {
        await using var ctx = NewContext();
        var station = Station();
        station.LastJudgement = judgement;

        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, station));

        Assert.Contains(label, cut.Markup);
        Assert.Contains("mud-icon-root", cut.Markup);
    }

    [Fact]
    public async Task Uses_one_fault_state_for_card_and_status_chip()
    {
        await using var ctx = NewContext();
        var station = Station();
        station.State = StationRuntimeState.Fault;
        station.LastJudgement = Judgement.Ok;

        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, station));

        Assert.Contains("dash-station detail fault", cut.Markup);
        Assert.Contains("mud-chip-color-error", cut.Markup);
        Assert.Contains("故障", cut.Markup);
    }

    [Fact]
    public async Task Shows_the_limit_range_and_numeric_exceedance()
    {
        await using var ctx = NewContext();
        var station = Station();
        station.LastTags =
        [
            new StationLiveTag
            {
                Name = "压力",
                Display = "13.2",
                Unit = "kN",
                NumericValue = 13.2,
                LowerLimit = 10,
                UpperLimit = 12,
                WarningLowerLimit = 10.5,
                WarningUpperLimit = 11.5,
                OutOfLimit = true
            }
        ];

        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, station));

        Assert.Single(cut.FindAll(".dt-limit-scale"));
        Assert.Contains("高于上限 1.2kN", cut.Find(".dt-tag-exceedance").TextContent);
        Assert.Contains("dt-limit-marker out", cut.Markup);
        Assert.Contains("规格范围 10 至 12", cut.Markup);
        Assert.DoesNotContain("规格范围", cut.Find(".dt-limit-labels").TextContent);
        Assert.Contains("10", cut.Find(".dt-limit-labels").TextContent);
        Assert.Contains("12", cut.Find(".dt-limit-labels").TextContent);
    }

    [Fact]
    public async Task Keeps_the_full_station_name_available_on_the_compact_title()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, Station()));

        Assert.Equal("ST010 上料工站", cut.Find(".dt-station-title").TextContent.Trim());
        Assert.Contains("ST010 上料工站", cut.Markup);
    }

    [Fact]
    public async Task Omits_limit_scale_when_the_record_has_no_numeric_limits()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, Station()));

        Assert.Empty(cut.FindAll(".dt-limit-scale"));
    }

    [Fact]
    public async Task Shows_every_point_for_a_station_with_five_points()
    {
        await using var ctx = NewContext();
        var station = Station();
        station.LastTags = Enumerable.Range(1, 5).Select(i => Point($"点位{i}")).ToArray();

        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, station));

        Assert.Empty(cut.FindAll(".dash-tag"));
        Assert.Contains("参数 5 项：均正常", cut.Find(".dt-point-summary-label").TextContent);
        cut.Find(".dt-station-expand").Click();
        Assert.Equal(5, cut.FindAll(".dash-tag").Count);
        Assert.Equal(5, cut.FindAll(".dt-limit-scale").Count);
        Assert.Empty(cut.FindAll(".dash-tag .mud-chip"));
    }

    [Fact]
    public async Task Shows_all_points_in_configuration_order_without_expansion()
    {
        await using var ctx = NewContext();
        var station = Station();
        station.LastTags =
        [
            Point("正常1"),
            Point("正常2"),
            Point("正常3"),
            Point("正常4"),
            Point("正常5"),
            Point("超限", outOfLimit: true),
            Point("预警", warning: true)
        ];

        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, station));

        var dense = cut.FindAll(".dash-tag");
        Assert.Equal(2, dense.Count);
        Assert.Contains("超限", dense[0].TextContent);
        Assert.Contains("预警", dense[1].TextContent);

        cut.Find(".dt-station-expand").Click();
        var rows = cut.FindAll(".dash-tag");
        Assert.Equal(7, rows.Count);
        Assert.Contains("正常1", rows[0].TextContent);
        Assert.Empty(rows[0].QuerySelectorAll(".mud-chip"));
        Assert.Contains("超限", rows[5].TextContent);
        Assert.Contains("预警", rows[6].TextContent);
        Assert.Equal(7, cut.FindAll(".dt-limit-scale").Count);
    }

    [Fact]
    public async Task Summarizes_normal_state_once_for_dense_points()
    {
        await using var ctx = NewContext();
        var station = Station();
        station.LastTags = Enumerable.Range(1, 7).Select(i => Point($"点位{i}")).ToArray();

        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, station));

        // 全部正常才说「均正常」；有任意异常/无数据/未判定时按类计数。
        // 规格内点位默认收起，展开后也不再给每一项贴「正常」芯片。
        Assert.Contains("参数 7 项：均正常", cut.Find(".dt-point-summary-label").TextContent);
        Assert.Empty(cut.FindAll(".dash-tag"));
        cut.Find(".dt-station-expand").Click();
        Assert.Equal(7, cut.FindAll(".dash-tag").Count);
        Assert.Empty(cut.FindAll(".dash-tag .mud-chip"));
    }

    [Fact]
    public async Task No_curve_station_has_no_chart_section()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, Station())
            .Add(x => x.ConfiguredCurves, Array.Empty<StationLiveCurve>()));
        Assert.Empty(cut.FindAll(".dt-station-curves"));
        Assert.Empty(cut.FindComponents<StationSparkChart>());
        Assert.Contains("未配置曲线", cut.Markup);
    }

    [Fact]
    public async Task Configured_curve_without_samples_keeps_a_no_data_notice()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, Station())
            .Add(x => x.ConfiguredCurves, new[] {
                new StationLiveCurve { Key = "1:1", Name = "压力曲线", Values = [] }
            }));
        Assert.Single(cut.FindAll(".dt-station-curves"));
        Assert.Contains("压力曲线", cut.Markup);
        Assert.Contains("本次暂无可显示数据", cut.Markup);
        Assert.Empty(cut.FindComponents<StationSparkChart>());
    }

    [Fact]
    public async Task Live_curve_replaces_its_configuration_placeholder()
    {
        await using var ctx = NewContext();
        var station = Station();
        var configured = new[] { new StationLiveCurve { Key = "1:1", Name = "压力曲线", Values = [] } };
        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, station)
            .Add(x => x.ConfiguredCurves, configured));
        var next = Station();
        next.LastRecordId = 3;
        next.LastCurves = [new StationLiveCurve { Key = "1:1", Name = "压力曲线", Values = [1f, 2f], Judgement = Judgement.Ok }];
        cut.Render(p => p.Add(x => x.Station, next));
        Assert.Single(cut.FindAll(".dt-station-curve"));
        Assert.Single(cut.FindComponents<StationSparkChart>());
        Assert.Empty(cut.FindAll(".dt-curve-empty"));
        Assert.DoesNotContain("本次暂无可显示数据", cut.Markup);
    }

    [Fact]
    public async Task Repaints_when_writeback_or_curve_reason_changes()
    {
        await using var ctx = NewContext();
        var station = Station();
        station.LastCurves = [new StationLiveCurve { Key = "1:1", Name = "压力曲线", Values = [1f, 2f], Judgement = Judgement.Ng, JudgementReason = "旧原因" }];
        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, station));
        var next = Station();
        next.LastWriteBackAttempts = 3;
        next.LastWriteBackOk = true;
        next.LastCurves = [new StationLiveCurve { Key = "1:1", Name = "压力曲线", Values = [1f, 2f], Judgement = Judgement.Ng, JudgementReason = "新原因" }];
        cut.Render(p => p.Add(x => x.Station, next));
        Assert.Contains("新原因", cut.Find(".dt-curve-reason").TextContent);
        Assert.Contains("重试 3 次后成功", cut.Markup);
        Assert.Empty(cut.FindAll(".dt-station-diagnostics"));
    }

    /// <summary>模拟器跑出来的单件耗时只有几十毫秒，按秒格式化会塌成「0.0 s」。</summary>
    [Fact]
    public async Task Keeps_millisecond_cadence_readable()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, Station(34)));

        Assert.Equal(2, cut.FindAll(".dt-station-subline .dt-station-meta").Count);
        Assert.Contains("PAL-0001", cut.Find(".dt-station-subline").TextContent);
        Assert.Equal("采集耗时", cut.FindAll(".dt-station-meta-label")[0].TextContent.Trim());
        Assert.Equal("34 ms", cut.FindAll(".dt-station-meta-value")[0].TextContent.Trim());
        Assert.Equal("产品间隔", cut.FindAll(".dt-station-meta-label")[1].TextContent.Trim());
        Assert.Equal("1.3 秒", cut.FindAll(".dt-station-meta-value")[1].TextContent.Trim());
    }

    [Fact]
    public async Task Switches_to_seconds_for_slower_stations()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, Station(3450)));

        Assert.Equal("3.5 s", cut.FindAll(".dt-station-meta-value")[0].TextContent.Trim());
    }

    [Fact]
    public async Task Compact_overview_prioritizes_issues_and_keeps_the_full_detail_drawer_entry()
    {
        await using var ctx = NewContext();
        var station = Station();
        station.State = StationRuntimeState.Busy;
        station.LastJudgement = Judgement.Ng;
        station.LastTags =
        [
            Point("正常参数"),
            Point("预警参数", warning: true),
            new StationLiveTag { Name = "无数据参数", Display = "-", NumericValue = null },
            Point("超限参数", outOfLimit: true),
            Point("另一正常参数")
        ];
        station.LastCurves =
        [
            new StationLiveCurve { Name = "完整曲线", Key = "1:1", Values = [1f, 2f] }
        ];

        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, station)
            .Add(x => x.Compact, true)
            .Add(x => x.OnOpenDetails, EventCallback.Factory.Create(this, () => { })));

        Assert.Contains("采集中", cut.Markup);
        Assert.Contains("上次 NG", cut.Markup);
        Assert.Contains("最近产品 NG", cut.Markup);
        Assert.Contains("超限 1", cut.Find(".dt-overview-issue-summary").TextContent);
        Assert.Contains("预警 1", cut.Find(".dt-overview-issue-summary").TextContent);
        Assert.Contains("无数据 1", cut.Find(".dt-overview-issue-summary").TextContent);
        Assert.Equal(3, cut.FindAll(".dt-overview-parameter").Count);
        Assert.Equal("超限参数", cut.FindAll(".dt-overview-parameter-name")[0].TextContent);
        Assert.Equal("预警参数", cut.FindAll(".dt-overview-parameter-name")[1].TextContent);
        Assert.Equal("无数据参数", cut.FindAll(".dt-overview-parameter-name")[2].TextContent);
        Assert.Empty(cut.FindAll(".dt-station-curves"));
        Assert.Equal("button", cut.Find(".dt-station-hit").GetAttribute("role"));
        Assert.Contains("打开 ST010 上料工站 详情", cut.Find(".dt-station-hit").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll(".dt-station-open-details"));
        Assert.Empty(cut.FindAll(".dt-station-detail-link"));
    }

    [Theory]
    [InlineData(Judgement.Ok, false)]
    [InlineData(Judgement.Ng, true)]
    public async Task Idle_border_follows_run_state_not_last_judgement(Judgement judgement, bool flashes)
    {
        await using var ctx = NewContext();
        var station = Station();
        station.State = StationRuntimeState.Idle;
        station.LastJudgement = judgement;

        var cut = ctx.Render<StationCard>(p => p.Add(x => x.Station, station));

        Assert.Contains("dash-station detail idle", cut.Markup);
        Assert.Contains("空闲", cut.Markup);
        Assert.DoesNotContain("dash-station detail ok", cut.Markup);
        Assert.DoesNotContain("dash-station detail ng", cut.Markup);
        if (flashes)
        {
            Assert.Contains("dt-quality-flash", cut.Markup);
            Assert.Contains("上次 NG", cut.Markup);
        }
        else
        {
            Assert.DoesNotContain("dt-quality-flash", cut.Markup);
        }
    }

    [Fact]
    public async Task Overview_card_opens_details_without_a_competing_record_link()
    {
        await using var ctx = NewContext();
        var opened = 0;
        var cut = ctx.Render<StationCard>(p => p
            .Add(x => x.Station, Station())
            .Add(x => x.Compact, true)
            .Add(x => x.OnOpenDetails, EventCallback.Factory.Create(this, () => opened++)));

        cut.Find(".dt-station-hit").Click();
        Assert.Equal(1, opened);
        Assert.Empty(cut.FindAll(".dt-station-detail-link"));
        Assert.DoesNotContain("/query/", cut.Markup);
    }

    [Fact]
    public async Task Detail_mode_keeps_an_explicit_open_control()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<StationCard>(p => p
            .Add(x => x.Station, Station())
            .Add(x => x.OnOpenDetails, EventCallback.Factory.Create(this, () => { })));

        Assert.Contains("打开工站详情", cut.Find(".dt-station-open-details").TextContent);
        Assert.Null(cut.Find(".dt-station-hit").GetAttribute("role"));
    }

    [Fact]
    public async Task Idle_empty_station_collapses_to_one_status_line()
    {
        await using var ctx = NewContext();
        var station = new StationRuntimeStatus
        {
            StationId = 10,
            StationCode = "OP10",
            StationName = "壳体上料",
            State = StationRuntimeState.Idle,
            LastJudgement = Judgement.None,
            LastTags = []
        };

        var cut = ctx.Render<StationCard>(p => p
            .Add(x => x.Station, station)
            .Add(x => x.Compact, true));

        Assert.Equal("等待托盘", cut.Find(".dt-station-idle-status").TextContent.Trim());
        Assert.DoesNotContain("最近判定", cut.Markup);
        Assert.DoesNotContain("暂无点位数据", cut.Markup);
        Assert.DoesNotContain("暂无参数数据", cut.Markup);
        Assert.DoesNotContain("尚无节拍", cut.Markup);
        Assert.Equal("尚无参数", cut.Find(".dt-station-overview-quiet").TextContent.Trim());
        Assert.Empty(cut.FindAll(".dt-station-subline .dt-station-meta"));
    }

    [Fact]
    public async Task Idle_empty_station_keeps_last_successful_pace()
    {
        await using var ctx = NewContext();
        var station = new StationRuntimeStatus
        {
            StationId = 10,
            StationCode = "OP10",
            StationName = "壳体上料",
            State = StationRuntimeState.Idle,
            LastDurationMs = 1850,
            LastPieceGap = TimeSpan.FromSeconds(12),
            LastTags = []
        };

        var cut = ctx.Render<StationCard>(p => p
            .Add(x => x.Station, station)
            .Add(x => x.Compact, true));

        Assert.Equal("上次节拍", cut.FindAll(".dt-station-meta-label")[0].TextContent.Trim());
        Assert.Equal("1.9 s", cut.FindAll(".dt-station-meta-value")[0].TextContent.Trim());
        Assert.Equal("上次间隔", cut.FindAll(".dt-station-meta-label")[1].TextContent.Trim());
        Assert.Equal("12 秒", cut.FindAll(".dt-station-meta-value")[1].TextContent.Trim());
        Assert.DoesNotContain("最近判定", cut.Markup);
    }

    [Fact]
    public void Open_alarm_hint_renders_chip_and_overview_link()
    {
        var ctx = NewContext();
        var cut = ctx.Render<StationCard>(parameters => parameters
            .Add(card => card.Station, Station())
            .Add(card => card.Compact, true)
            .Add(card => card.OpenAlarmCount, 2)
            .Add(card => card.OpenAlarmHint, "电机过热：当前 90"));

        Assert.Contains("报警 2", cut.Markup);
        Assert.Contains("/alarms?station=1", cut.Markup);
        Assert.Contains("dt-station-alarm-chip", cut.Markup);
        Assert.Contains("dt-overview-alarm", cut.Markup);
        Assert.Contains("电机过热", cut.Markup);
    }

}
