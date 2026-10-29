using System.Text;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DataTrace.Web.Tests;

public sealed class RecordDetailUnitTests : WebTestBase
{
    [Theory]
    [InlineData("bar", "bar")]
    [InlineData(null, "单位未知")]
    public async Task Detail_uses_the_saved_unit_for_values_limits_and_csv(string? unitSnapshot, string displayedUnit)
    {
        var store = new Mock<IRuntimeStore>();
        store.Setup(s => s.GetRecordAsync("202610", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectRecord
            {
                Id = 42,
                SerialNo = "SN-42",
                PalletCode = "P-42",
                StationCode = "ST-42",
                TriggerTime = new DateTime(2026, 10, 12, 9, 0, 0),
                CompleteTime = new DateTime(2026, 10, 12, 9, 0, 1),
                TagValues =
                [
                    new TagValue
                    {
                        TagName = "压力",
                        UnitSnapshot = unitSnapshot,
                        NumericValue = 12.5,
                        LowerLimit = 10,
                        UpperLimit = 20,
                        WarningLowerLimit = 11,
                        WarningUpperLimit = 19
                    }
                ]
            });
        Context.Services.AddSingleton(store.Object);
        Context.Services.AddSingleton(Mock.Of<ICurveFileStore>());
        Context.Services.AddSingleton(Mock.Of<ICollectArchiveStore>());
        RenderPopoverHost();

        var cut = Context.Render<RecordDetail>(parameters => parameters
            .Add(page => page.MonthKey, "202610")
            .Add(page => page.RecordId, 42));

        Assert.Contains($"12.5 {displayedUnit}", cut.Markup);
        Assert.Contains($"10 ~ 20 {displayedUnit}", cut.Markup);
        Assert.Contains($"11 ~ 19 {displayedUnit}", cut.Markup);

        await cut.InvokeAsync(() => cut.FindAll("button")
            .Single(button => button.TextContent.Contains("导出本记录"))
            .Click());

        var download = Assert.Single(Context.JSInterop.Invocations["dtDownload"]);
        var csv = Encoding.UTF8.GetString(Convert.FromBase64String((string)download.Arguments[1]!))
            .TrimStart('\uFEFF');
        Assert.Contains("点位名称,数值,单位,判定", csv);
        Assert.Contains($"压力,12.5,{displayedUnit},", csv);
    }
    [Fact]
    public void Detail_shows_product_trace_link_and_cavity_rows()
    {
        var store = new Mock<IRuntimeStore>();
        store.Setup(s => s.GetRecordAsync("202610", 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectRecord
            {
                Id = 7,
                SerialNo = "SN-7",
                PalletCode = "P-7",
                PalletSessionId = 99,
                StationCode = "OP30",
                TriggerTime = new DateTime(2026, 10, 12, 9, 0, 0),
                CompleteTime = new DateTime(2026, 10, 12, 9, 0, 1),
                Products =
                [
                    new ProductRecord { PositionIndex = 1, Occupied = true, Judgement = Judgement.Ok },
                    new ProductRecord { PositionIndex = 2, Occupied = true, Judgement = Judgement.Ng, NgReason = "压力超限" }
                ]
            });
        Context.Services.AddSingleton(store.Object);
        Context.Services.AddSingleton(Mock.Of<ICurveFileStore>());
        Context.Services.AddSingleton(Mock.Of<ICollectArchiveStore>());
        RenderPopoverHost();

        var cut = Context.Render<RecordDetail>(parameters => parameters
            .Add(page => page.MonthKey, "202610")
            .Add(page => page.RecordId, 7));

        Assert.Contains("产品履历", cut.Markup);
        Assert.Contains("腔位", cut.Markup);
        Assert.Contains("压力超限", cut.Markup);
        var link = cut.FindAll("a").Single(a => a.TextContent.Contains("产品履历"));
        var href = link.GetAttribute("href") ?? "";
        Assert.Contains("view=trace", href);
        Assert.Contains("trace=SN-7", href);
        Assert.Contains("sessionId=99", href);
    }
}
