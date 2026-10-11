using DataTrace.Application.Realtime;
using DataTrace.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace DataTrace.Web.Tests;

/// <summary>
/// 迷你曲线的刷新正确性（看板 UX 审查）：三点采样指纹会漏掉中段变化，
/// 改为按「采集快照版本 + 曲线 Key」判断 —— 同一件产品内不重画，换件必重画。
/// </summary>
public class StationSparkChartTests
{
    private static TestContext NewContext()
    {
        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static StationLiveCurve Curve(float[] values) => new()
    {
        Name = "压力曲线",
        Key = "1:1",
        Values = values,
        Judgement = Domain.Enums.Judgement.Ok
    };

    [Fact]
    public async Task Same_version_key_with_changed_values_does_not_repaint()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<StationSparkChart>(p => p
            .Add(x => x.CurveName, "压力曲线")
            .Add(x => x.Values, Curve([1f, 2f, 3f]).Values)
            .Add(x => x.Tone, "ok")
            .Add(x => x.VersionKey, "202610:77:1:1"));
        var before = cut.RenderCount;

        // 同一条曲线、同一件产品：值引用不同也不该重画（看板每 5 秒的定时刷新）。
        cut.Render(p => p.Add(x => x.Values, Curve([9f, 9f, 9f]).Values));

        Assert.Equal(before, cut.RenderCount);
    }

    [Fact]
    public async Task New_record_version_repaints_even_when_three_points_match()
    {
        await using var ctx = NewContext();
        // 三点（首/中/末）完全一致、中段不同：旧指纹会漏刷，版本标识必须重画。
        var cut = ctx.Render<StationSparkChart>(p => p
            .Add(x => x.CurveName, "压力曲线")
            .Add(x => x.Values, new float[] { 1f, 5f, 3f })
            .Add(x => x.Tone, "ok")
            .Add(x => x.VersionKey, "202610:77:1:1"));
        var before = cut.RenderCount;

        cut.Render(p => p
            .Add(x => x.Values, new float[] { 1f, 6f, 3f })
            .Add(x => x.VersionKey, "202610:78:1:1"));

        Assert.True(cut.RenderCount > before, "换件后必须重画，即使三点采样完全一致");
    }

    [Theory]
    [InlineData("ng")]
    [InlineData("ok")]
    public async Task Tone_change_repaints(string tone)
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<StationSparkChart>(p => p
            .Add(x => x.CurveName, "压力曲线")
            .Add(x => x.Values, Curve([1f, 2f, 3f]).Values)
            .Add(x => x.Tone, "none")
            .Add(x => x.VersionKey, "202610:77:1:1"));
        var before = cut.RenderCount;

        cut.Render(p => p.Add(x => x.Tone, tone));

        Assert.True(cut.RenderCount > before, "判定色变化必须重画（颜色只是通道之一，但变了就要画）");
    }
}
