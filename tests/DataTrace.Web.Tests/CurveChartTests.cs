using DataTrace.Application.Runtime;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DataTrace.Web.Tests;

/// <summary>
/// 曲线图组件的导出闸门与留痕：原始采样点属于可追溯数据，
/// 查询/明细/报表三处导出都已经收敛到"管理员/工程师 + 写审计"，这里不能是唯一的例外。
/// </summary>
public class CurveChartTests : WebTestBase
{
    private static CurvePayload Payload() => new()
    {
        PointCount = 3,
        Series =
        [
            new CurveSeriesPayload { Name = "位移", Role = SeriesRole.X, Values = [1f, 2f, 3f] },
            new CurveSeriesPayload { Name = "压力", Role = SeriesRole.Y, Values = [10f, 20f, 30f] }
        ]
    };

    private IRenderedComponent<CurveChart> Render(string auditKey = "202609/7/ST010_PD")
        => Context.RenderComponent<CurveChart>(parameters => parameters
            .Add(p => p.Payload, Payload())
            .Add(p => p.AuditKey, auditKey));

    [Theory]
    [InlineData(AppRoles.Administrator, true)]
    [InlineData(AppRoles.Engineer, true)]
    [InlineData(AppRoles.Operator, false)]
    [InlineData(AppRoles.Viewer, false)]
    public void Export_button_is_only_rendered_for_roles_that_may_export(string role, bool visible)
    {
        UseRole(role);

        var cut = Render();

        Assert.Equal(visible, cut.FindAll("button").Any(b => b.TextContent.Contains("导出数据")));
    }

    [Fact]
    public void Export_writes_an_audit_entry_naming_the_record_and_the_series()
    {
        var cut = Render("202609/7/ST010_PD");

        ClickButton(cut, "导出数据");

        // 键定位到"哪条记录的哪条曲线"，明细与条数放变更列。
        Audit.Verify(
            a => a.WriteAsync(
                "admin", "Export", "CurveDefinition", "202609/7/ST010_PD", null,
                It.Is<string>(s => s.Contains("series=位移/压力") && s.Contains("points=3")),
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal("已导出 3 个采样点", Toast.LastMessage);
    }

    [Fact]
    public void A_failed_audit_write_does_not_look_like_a_failed_export()
    {
        Audit
            .Setup(a => a.WriteAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("config.db 被占用"));
        var cut = Render();

        ClickButton(cut, "导出数据");

        // 文件已经在用户手里了，报"导出失败"会让现场重导一次。
        Assert.Contains("已导出 3 个采样点", Toast.LastMessage);
        Assert.Contains("审计记录写入失败", Toast.LastMessage);
        Assert.Equal(Severity.Warning, Toast.LastSeverity);
    }

    /// <summary>没有成对数据（只有单序列，按序号画）时不导出，也不该留下审计。</summary>
    [Fact]
    public void Export_is_refused_when_there_is_no_paired_data()
    {
        var cut = Context.RenderComponent<CurveChart>(parameters => parameters
            .Add(p => p.Payload, new CurvePayload
            {
                PointCount = 2,
                Series = [new CurveSeriesPayload { Name = "压力", Role = SeriesRole.Y, Values = [10f, 20f] }]
            }));

        ClickButton(cut, "导出数据");

        Audit.Verify(
            a => a.WriteAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Contains("没有可导出的成对数据", Toast.LastMessage);
    }
}