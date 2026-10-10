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
        SetupJs(ctx);

        return ctx.Render<LineChart>(parameters => parameters
            .Add(p => p.Series, new[] { new LineSeries { Name = "压力", Values = values } })
            .Add(p => p.MaxPoints, maxPoints)
            .Add(p => p.ShowLegend, true));
    }

    private static void SetupJs(TestContext ctx)
    {
        var module = ctx.JSInterop.SetupModule("./js/echarts-line-chart.js");
        module.SetupVoid("update", _ => true);
        module.SetupVoid("dispose", _ => true);
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
    public void AxisChartRendersAnAccessibleEChartsContainer()
    {
        var cut = Render(new[] { 3f, 1f, 4f }, 20);

        var chart = cut.Find("div.dt-chart");
        Assert.Equal("img", chart.GetAttribute("role"));
        Assert.Contains("X 折线图：压力", chart.GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll("svg.dt-chart"));
        Assert.Empty(cut.FindAll(".dt-chart-readout"));
    }

    [Fact]
    public void SparklineUsesTheSameEChartsContainerWithoutTheLegend()
    {
        var ctx = new TestContext();
        ctx.Services.AddMudServices();
        SetupJs(ctx);
        var cut = ctx.Render<LineChart>(parameters => parameters
            .Add(p => p.Series, new[] { new LineSeries { Name = "压力", Values = new[] { 1f, 2f } } })
            .Add(p => p.ShowAxis, false)
            .Add(p => p.ShowLegend, false)
            .Add(p => p.ShowPoints, false));

        Assert.Single(cut.FindAll("div.dt-chart"));
        Assert.Empty(cut.FindAll(".dt-legend"));
        Assert.Empty(cut.FindAll(".dt-chart-readout"));
    }
}
