using DataTrace.Application.Reporting;
using DataTrace.Collector;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Evaluation;
using DataTrace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Tests;

/// <summary>
/// 过程能力端到端：真实采集 → 月库 → 窄投影取数 → 按落库规格限分段 → 计算。
/// 这一条链路断在任何一环，界面上的 Cpk 都是假的，所以必须整体验证。
/// </summary>
public class SpcServiceTests
{
    [Fact]
    public async Task Process_capability_reads_samples_and_spec_limits_end_to_end()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        var tag = station.Tags.Single(t => t.Name == "压力");

        for (var i = 0; i < 6; i++)
        {
            SimulatorScenario.LoadStationCycle(harness.Simulator, harness.Plc, station, $"P02{i:00}",
                new SimulatedCycleOptions { Random = new Random(i + 1) });
            Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));
        }

        var spc = harness.Scope.ServiceProvider.GetRequiredService<ISpcService>();

        var report = await spc.GetProcessCapabilityAsync(
            tag.Id, DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1));

        Assert.NotNull(report);
        Assert.Equal(tag.Name, report!.TagName);

        // 限值没动过就只有一段，界面与分段之前完全一样。
        var segment = Assert.Single(report.Segments);
        Assert.Equal(6, segment.Summary.Count);
        Assert.Equal(6, report.Samples.Count);
        Assert.False(segment.LimitsFromConfig);

        // 规格限来自这批点采集时生效的值，不是硬编码。
        Assert.Equal(tag.LowerLimit, segment.LowerLimit);
        Assert.Equal(tag.UpperLimit, segment.UpperLimit);

        // 样本不到 30 个时必须明说"仅供参考"，不能给个好数字就完事。
        Assert.Equal(SpcVerdict.InsufficientData, segment.Summary.Verdict);
        Assert.Contains("样本不足", segment.Summary.Note);
        Assert.True(segment.Summary.Cpk > 0);
        Assert.True(segment.Summary.UpperControlLimit > segment.Summary.CenterLine);
        Assert.True(segment.Summary.LowerControlLimit < segment.Summary.CenterLine);

        // 采样必须按时间升序，否则移动极差会算到假相邻关系上。
        Assert.True(report.Samples.Zip(report.Samples.Skip(1)).All(p => p.First.Time <= p.Second.Time));

        // 判异记录的序号必须落在样本范围内，否则界面点不到具体托盘。
        Assert.All(report.Violations, v =>
        {
            Assert.InRange(v.StartIndex, 0, report.Samples.Count - 1);
            Assert.InRange(v.EndIndex, v.StartIndex, report.Samples.Count - 1);
        });

        // 序号能映射回时间。
        Assert.NotNull(report.TimeAt(0));
        Assert.NotNull(report.TimeAt(report.Samples.Count - 1));
        Assert.Null(report.TimeAt(report.Samples.Count));
        Assert.Null(report.TimeAt(-1));
    }

    [Fact]
    public async Task Unknown_tag_returns_null_instead_of_throwing()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var spc = harness.Scope.ServiceProvider.GetRequiredService<ISpcService>();

        var report = await spc.GetProcessCapabilityAsync(
            999_999, DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1));

        Assert.Null(report);
    }

    [Fact]
    public async Task Range_without_samples_reports_insufficient_data()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var tag = harness.Station(0).Tags.Single(t => t.Name == "压力");
        var spc = harness.Scope.ServiceProvider.GetRequiredService<ISpcService>();

        var report = await spc.GetProcessCapabilityAsync(
            tag.Id, DateTime.Today.AddDays(-10), DateTime.Today.AddDays(-9));

        Assert.NotNull(report);
        // 空区间也要留一段，界面才能照旧显示提示而不是空白。
        var segment = Assert.Single(report!.Segments);
        Assert.Equal(0, segment.Summary.Count);
        Assert.Empty(report.Samples);
        Assert.Empty(report.Violations);
        Assert.Equal(SpcVerdict.InsufficientData, segment.Summary.Verdict);
        Assert.Contains("没有采样数据", segment.Summary.Note);
    }

    /// <summary>
    /// 换型号（收紧规格限）之后，已经采完的数据不被回头改写：
    /// 报表按"采集时生效的规格限"分段，新限值只作用于新采的点。
    /// 曾经这里断言的是相反的口径（整窗按当前型号重算）。
    /// </summary>
    [Fact]
    public async Task Spec_change_mid_window_splits_segments_instead_of_rewriting_history()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        var tag = station.Tags.Single(t => t.Name == "压力");
        var spc = harness.Scope.ServiceProvider.GetRequiredService<ISpcService>();
        var from = DateTime.Today.AddDays(-1);
        var to = DateTime.Today.AddDays(1);

        // 前 6 点按点位默认上限 20 采集。
        for (var i = 0; i < 6; i++)
        {
            LoadPressure(harness, station, $"PS{i:00}", 12f + i * 0.5f);
            Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));
        }

        var before = await spc.GetProcessCapabilityAsync(tag.Id, from, to);
        Assert.Equal(20d, before!.Segments.Single().UpperLimit);
        Assert.False(before.SpecChangedInWindow);

        // 切到 A100（压力规格上限收紧到 16），再采 6 点：同样的数值分布，上限更近。
        var recipe = harness.Snapshot.Recipes.Single(r => r.Code == "A100");
        await harness.ConfigRepository.SetActiveRecipeAsync(recipe.Id);
        await harness.RefreshSnapshotAsync();
        var switched = harness.Snapshot.Stations.OrderBy(s => s.Sequence).First();
        for (var i = 0; i < 6; i++)
        {
            LoadPressure(harness, switched, $"PA{i:00}", 12f + i * 0.5f);
            Assert.Equal(ResultCodes.Success, await harness.RunAsync(switched));
        }

        var after = await spc.GetProcessCapabilityAsync(tag.Id, from, to);
        Assert.NotNull(after);
        Assert.True(after!.SpecChangedInWindow);
        Assert.Equal(2, after.Segments.Count);

        // 第一段仍是当时生效的 20 —— 老数据的结论不会因为改了配置而漂移。
        Assert.Equal(20d, after.Segments[0].UpperLimit);
        Assert.Equal(6, after.Segments[0].Summary.Count);
        Assert.Equal(16d, after.Segments[1].UpperLimit);
        Assert.Equal(6, after.Segments[1].Summary.Count);

        // 同一批数值，上限收紧 → 上侧余量变小 → Cpu 必然下降。
        Assert.True(after.Segments[1].Summary.Cpu < after.Segments[0].Summary.Cpu);

        // 老那段的能力和换型号之前一模一样 —— 不只是标了分段，数字真的没被回头改写。
        Assert.Equal(before.Segments[0].Summary.Cpk, after.Segments[0].Summary.Cpk);

        // 段号与序号：两段各占一半，起点连续，界面点托盘才点得准。
        Assert.Equal(new[] { 1, 2 }, after.Segments.Select(s => s.Number).ToArray());
        Assert.Equal(new[] { 0, 6 }, after.Segments.Select(s => s.StartIndex).ToArray());
        Assert.Equal(new[] { 6, 6 }, after.Segments.Select(s => s.Count).ToArray());
        Assert.Equal(12, after.Samples.Count);
        Assert.All(after.Segments, s => Assert.True(s.EndTime >= s.StartTime));
    }

    /// <summary>
    /// 老库里没有那两列限值的行只能按当前配置估，并且要在界面上说清楚。
    /// 现网接手时历史数据全是这种，这条是分段逻辑最常见的入口。
    /// </summary>
    [Fact]
    public async Task Rows_without_persisted_limits_fall_back_to_the_current_config_and_say_so()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        var tag = station.Tags.Single(t => t.Name == "压力");
        for (var i = 0; i < 6; i++)
        {
            LoadPressure(harness, station, $"PL{i:00}", 12f + i * 0.5f);
            Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));
        }

        WipePersistedLimits(harness);

        var spc = harness.Scope.ServiceProvider.GetRequiredService<ISpcService>();
        var report = await spc.GetProcessCapabilityAsync(
            tag.Id, DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1));

        var segment = Assert.Single(report!.Segments);
        Assert.True(segment.LimitsFromConfig);
        Assert.Equal(tag.UpperLimit, segment.UpperLimit);
        Assert.Equal(6, segment.Summary.Count);
    }

    /// <summary>
    /// 报表按型号筛选时，兜底限值也必须取被筛型号的覆盖值：
    /// 筛了 A100 却拿当前生效型号（这里未选型号）的默认限值去兜底，
    /// 算出来的 Cp/Cpk 与图上那批样本不是一套口径。
    /// </summary>
    [Fact]
    public async Task Config_fallback_uses_the_filtered_recipe_not_the_active_one()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        var tag = station.Tags.Single(t => t.Name == "压力");
        for (var i = 0; i < 6; i++)
        {
            LoadPressure(harness, station, $"PR{i:00}", 12f + i * 0.5f);
            Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));
        }

        // 老数据：没有落库限值，只能按配置兜底。
        WipePersistedLimits(harness);

        var spc = harness.Scope.ServiceProvider.GetRequiredService<ISpcService>();
        var from = DateTime.Today.AddDays(-1);
        var to = DateTime.Today.AddDays(1);

        // 不限型号：当前未选型号 → 点位默认上限 20。
        var unrestricted = await spc.GetProcessCapabilityAsync(tag.Id, from, to);
        var byDefault = Assert.Single(unrestricted!.Segments);
        Assert.True(byDefault.LimitsFromConfig);
        Assert.Equal(tag.UpperLimit, byDefault.UpperLimit);

        // 筛 A100：A100 把压力规格上限收紧到 16，兜底就得用 16。
        var filtered = await spc.GetProcessCapabilityAsync(tag.Id, from, to, "A100");
        var byRecipe = Assert.Single(filtered!.Segments);
        Assert.Equal(16d, byRecipe.UpperLimit);
        Assert.Equal("A100", filtered.RecipeCode);

        // 筛「未选型号」：只用点位默认限值，不能把当前生效型号的覆盖带进来。
        var noRecipe = await spc.GetProcessCapabilityAsync(tag.Id, from, to, "");
        Assert.Equal(tag.UpperLimit, Assert.Single(noRecipe!.Segments).UpperLimit);
        Assert.Equal("", noRecipe.RecipeCode);

        // 型号改过编码：记录里写的是旧码，筛选下拉给的也是旧码（选项来自历史记录）。
        // 旧码必须能反查到型号，否则兜底值会悄悄退回点位默认上限 20。
        var renamed = (await harness.ConfigRepository.GetSnapshotAsync()).Recipes.Single(r => r.Code == "A100");
        renamed.Code = "B300";
        await harness.ConfigRepository.SaveRecipeAsync(renamed);

        var byPreviousCode = await spc.GetProcessCapabilityAsync(tag.Id, from, to, "A100");
        Assert.Equal(16d, Assert.Single(byPreviousCode!.Segments).UpperLimit);
        Assert.Equal("B300", byPreviousCode.RecipeCode);
    }

    /// <summary>把压力点覆盖成确定值：两段的数值分布必须一样，Cpu 才可比。</summary>
    private static void LoadPressure(CollectHarness harness, Station station, string pallet, float value)
    {
        SimulatorScenario.LoadStationCycle(harness.Simulator, harness.Plc, station, pallet);
        harness.Simulator.SetFloat("D1100", value, harness.Plc.FloatWordOrder);
    }

    /// <summary>把落库的限值列抹掉，模拟"限值随记录落库"之前的历史数据。</summary>
    private static void WipePersistedLimits(CollectHarness harness)
    {
        var factory = harness.Scope.ServiceProvider.GetRequiredService<RuntimeDbFactory>();
        using var db = factory.Open(DateTime.Today.ToString("yyyyMM"));
        db.Database.ExecuteSqlRaw(
            "UPDATE TagValues SET LowerLimit = NULL, UpperLimit = NULL, WarningLowerLimit = NULL, WarningUpperLimit = NULL");
    }
}
