using DataTrace.Application.Reporting;
using DataTrace.Collector;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Evaluation;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Tests;

/// <summary>
/// 过程能力端到端：真实采集 → 月库 → 窄投影取数 → 解析配置库里的规格限 → 计算。
/// 这一条链路断在任何一环，界面上的 Cpk 都是假的，所以必须整体验证。
/// </summary>
public class SpcServiceTests
{
    [Fact]
    public async Task Process_capability_reads_samples_and_spec_limits_end_to_end()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        var tag = station.Tags.Single(t => t.Code == "ST010_P1");

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
        Assert.Equal(tag.Code, report!.TagCode);
        Assert.Equal(tag.Name, report.TagName);
        Assert.Equal(6, report.Summary.Count);
        Assert.Equal(6, report.Samples.Count);

        // 规格限来自配置库，不是硬编码。
        Assert.Equal(tag.LowerLimit, report.LowerLimit);
        Assert.Equal(tag.UpperLimit, report.UpperLimit);

        // 样本不到 30 个时必须明说"仅供参考"，不能给个好数字就完事。
        Assert.Equal(SpcVerdict.InsufficientData, report.Summary.Verdict);
        Assert.Contains("样本不足", report.Summary.Note);
        Assert.True(report.Summary.Cpk > 0);
        Assert.True(report.Summary.UpperControlLimit > report.Summary.CenterLine);
        Assert.True(report.Summary.LowerControlLimit < report.Summary.CenterLine);

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
        var tag = harness.Station(0).Tags.Single(t => t.Code == "ST010_P1");
        var spc = harness.Scope.ServiceProvider.GetRequiredService<ISpcService>();

        var report = await spc.GetProcessCapabilityAsync(
            tag.Id, DateTime.Today.AddDays(-10), DateTime.Today.AddDays(-9));

        Assert.NotNull(report);
        Assert.Equal(0, report!.Summary.Count);
        Assert.Empty(report.Samples);
        Assert.Empty(report.Violations);
        Assert.Equal(SpcVerdict.InsufficientData, report.Summary.Verdict);
        Assert.Contains("没有采样数据", report.Summary.Note);
    }

    [Fact]
    public async Task Process_capability_uses_the_active_recipe_limits()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        var tag = station.Tags.Single(t => t.Code == "ST010_P1");

        for (var i = 0; i < 6; i++)
        {
            SimulatorScenario.LoadStationCycle(harness.Simulator, harness.Plc, station, $"P03{i:00}",
                new SimulatedCycleOptions { Random = new Random(i + 1) });
            await harness.RunAsync(station);
        }

        var spc = harness.Scope.ServiceProvider.GetRequiredService<ISpcService>();
        var from = DateTime.Today.AddDays(-1);
        var to = DateTime.Today.AddDays(1);

        // 未选择型号：按点位默认规格限 5~20 评估。
        var before = await spc.GetProcessCapabilityAsync(tag.Id, from, to);
        Assert.Equal(20d, before!.UpperLimit!.Value);
        Assert.Equal("", before.RecipeCode);

        // 采集时落库的规格限就是这一套，所以没有点被重算。
        Assert.Equal(0, before.SamplesRejudgedByNewLimits);

        // 切到 A100：同一批数据按收紧后的上限 16 评估 —— 报表口径必须跟着判定口径走。
        var recipe = harness.Snapshot.Recipes.Single(r => r.Code == "A100");
        await harness.ConfigRepository.SetActiveRecipeAsync(recipe.Id);

        var after = await spc.GetProcessCapabilityAsync(tag.Id, from, to);
        Assert.Equal(16d, after!.UpperLimit!.Value);
        Assert.Equal("A100", after.RecipeCode);
        // 上限收紧 → 上侧余量变小 → Cpu 必然下降（与均值落在哪一侧无关）。
        Assert.True(after.Summary.Cpu < before.Summary.Cpu);

        // 但这 6 个点是按上限 20 判废之后采的：换型号重估的同时必须说清重算了多少点，
        // 否则屏幕上的 Cpk 和托盘上的判定结果对不上，还没人知道。
        Assert.Equal(6, after.SamplesRejudgedByNewLimits);
    }
}
