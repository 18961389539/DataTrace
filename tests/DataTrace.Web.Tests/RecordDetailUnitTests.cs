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
}
