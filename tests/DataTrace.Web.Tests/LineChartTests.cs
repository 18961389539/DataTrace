using DataTrace.Web.Components.Shared;
using DataTrace.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace DataTrace.Web.Tests;

/// <summary>
/// 折线图组件的接线。抽稀算法本身在 <see cref="ChartUtil.SampleEnvelope"/> 有直接单测
/// （ChartUtil 在 DataTrace.Shared，DataTrace.Tests 直接引用该工程），这里只守住一件事：
/// 组件确实把真实极值显示出来了，而不是抽稀之后的近似值。
/// </summary>
public class LineChartTests
{
    private static IRenderedComponent<LineChart> Render(float[] values, int maxPoints)
    {
        var ctx = new TestContext();
        ctx.Services.AddMudServices();

        return ctx.RenderComponent<LineChart>(parameters => parameters
            .Add(p => p.Series, new[] { new LineSeries { Name = "压力", Values = values } })
            .Add(p => p.MaxPoints, maxPoints)
            .Add(p => p.ShowLegend, true));
    }

    [Fact]
    public void LegendShowsTrueExtremesEvenWhenTheSpikeFallsBetweenSamples()
    {
        // 尖峰在 517：等间隔取点命不中它。旧实现在这里会显示 "10.0 ~ 10.0"。
        var values = Enumerable.Repeat(10f, 1000).ToArray();
        values[517] = 999f;

        var legend = Render(values, 20).Find(".dt-legend").TextContent;

        Assert.Contains("10.0 ~ 999", legend);
    }

    [Fact]
    public void LegendOfAShortSeriesIsPassedThroughUnchanged()
    {
        var legend = Render(new[] { 3f, 1f, 4f, 1f, 5f }, 20).Find(".dt-legend").TextContent;

        Assert.Contains("1.0 ~ 5.0", legend);
    }

    [Fact]
    public void AxisChartExposesPerPointReadoutsForTheHoverPanel()
    {
        var cut = Render(new[] { 3f, 1f, 4f }, 20);

        // 面板是 js/datatrace.js 的落点：没有它，悬停读数整个功能不存在。
        Assert.Single(cut.FindAll(".dt-chart-readout"));

        // 数值挂在 data-tip 上（不再是 <title>：那会跟面板同时弹出两个浮层）。
        // 文案顺序是"轴名 值 → 序列名 值"，一字不差地展示给用户，改格式等于改读数。
        var points = cut.FindAll("circle.dt-point");
        Assert.Equal("X 0 → 压力 3.0", points[0].GetAttribute("data-tip"));
        Assert.Empty(cut.FindAll("circle.dt-point title"));
    }

    [Fact]
    public void SparklineDropsTheReadoutPanelAndPointHooks()
    {
        var ctx = new TestContext();
        ctx.Services.AddMudServices();
        var cut = ctx.RenderComponent<LineChart>(parameters => parameters
            .Add(p => p.Series, new[] { new LineSeries { Name = "压力", Values = new[] { 1f, 2f } } })
            .Add(p => p.ShowAxis, false)
            .Add(p => p.ShowPoints, false));

        // 迷你曲线（工站卡片）没有点位也没有可读的距离，不该背上读数这套东西。
        Assert.Empty(cut.FindAll(".dt-chart-readout"));
        Assert.Empty(cut.FindAll("circle.dt-point"));
    }
}
