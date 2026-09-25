using DataTrace.Application.Configuration;
using DataTrace.Application.Evaluation;
using DataTrace.Application.Reporting;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Web.Components.Pages;
using DataTrace.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DataTrace.Web.Tests;

/// <summary>
/// 波形基线页的取数节奏：日期控件在加载中没禁用，快速改两次区间会让两个请求并发，
/// 先发的后返回就会把新结果覆盖掉 —— 页面停在旧区间的报表上却显示新日期。
/// </summary>
public class CurveBaselinePageTests : WebTestBase
{
    private readonly Mock<ICurveTemplateService> _templates = new();
    private readonly Mock<ICurveBaselineCache> _cache = new();

    public CurveBaselinePageTests()
    {
        Config.Snapshot = new AppConfigurationSnapshot
        {
            Stations =
            [
                new Station
                {
                    Id = 10,
                    Code = "ST010",
                    Name = "压装",
                    Curves = [new CurveDefinition { Id = 3, StationId = 10, Code = "ST010_PD", Name = "位移压力曲线" }]
                }
            ]
        };

        _cache.SetupGet(c => c.Current).Returns((CurveBaselineSnapshot?)null);
    }

    private IRenderedComponent<CurveBaseline> Render()
    {
        Context.Services.AddSingleton(_templates.Object);
        Context.Services.AddSingleton(_cache.Object);
        // MudSelect 的浮层要一个 provider：真实应用由 MainLayout 提供。
        Context.RenderComponent<MudBlazor.MudPopoverProvider>();
        return Context.RenderComponent<CurveBaseline>();
    }

    private static CurveBaselineReport Report(string curveCode) => new()
    {
        CurveDefinitionId = 3,
        CurveCode = curveCode,
        CurveName = "位移压力曲线",
        SeriesName = "压力",
        Template = CurveTemplateBuilder.Build([]),
        Shadow = new CurveShadowComparison()
    };

    /// <summary>加载途中改区间：在途请求结束后必须按最新条件重跑，界面停在最后一次的结果上。</summary>
    [Fact]
    public async Task A_range_change_while_loading_is_replayed_with_the_new_range()
    {
        var slow = new TaskCompletionSource<CurveBaselineReport?>();
        var ranges = new List<DateTime>();
        _templates
            .Setup(s => s.GetBaselineAsync(
                It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, string?, DateTime, DateTime, int, int, CancellationToken>(
                (_, _, from, _, _, _, _) =>
                {
                    ranges.Add(from);
                    return ranges.Count == 1 ? slow.Task : Task.FromResult<CurveBaselineReport?>(Report("SECOND"));
                });

        var cut = Render();

        // 首屏那次还在飞，此时用户改了起始日期（与真人操作同一条路径）。
        var filter = cut.FindComponent<DateRangeFilter>();
        await cut.InvokeAsync(() => filter.Instance.FromChanged.InvokeAsync(DateTime.Today.AddDays(-29)));
        await cut.InvokeAsync(() => filter.Instance.OnRangeChanged.InvokeAsync());

        // 在途请求返回后，页面立刻用新条件重跑一轮。
        slow.SetResult(Report("FIRST"));
        cut.WaitForState(() => cut.Markup.Contains("SECOND"));

        Assert.Equal(2, ranges.Count);
        Assert.Equal(DateTime.Today.AddDays(-29), ranges[1]);
        Assert.DoesNotContain("FIRST", cut.Markup);
    }
}