using DataTrace.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace DataTrace.Web.Tests;

/// <summary>
/// 折线图组件的接线。抽稀算法本身在 <see cref="ChartUtil.SampleEnvelope"/> 有直接单测
/// （DataTrace.Tests 按文件链接引用 ChartUtil），这里只守住一件事：
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
}
