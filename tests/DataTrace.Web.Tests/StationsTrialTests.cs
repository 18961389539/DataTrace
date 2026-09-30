using DataTrace.Application.Configuration;
using DataTrace.Collector;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Pages;
using Moq;

namespace DataTrace.Web.Tests;

/// <summary>
/// 工站配置页的「试读一次」：一个只读的配置验证入口。
/// </summary>
/// <remarks>
/// 它存在的意义是"保存之前先看看地址配对没有"。所以页面要保证两件事：
/// 读得到的时候把值如实摊开；读不到的时候把原因说清楚，而不是给一个点不动的按钮。
/// </remarks>
public class StationsTrialTests : WebTestBase
{
    private static PlcConnection Mitsubishi() => new()
    {
        Id = 1,
        Name = "一号线主 PLC",
        Brand = PlcBrand.MitsubishiMc3E,
        Host = "192.168.1.10",
        Port = 5000,
        Enabled = true
    };

    private static Station Station() => new()
    {
        Id = 10,
        PlcConnectionId = 1,
        Code = "ST010",
        Name = "上料工站",
        Sequence = 10,
        TriggerAddress = "D1000",
        TriggerValue = 1,
        PalletCodeAddress = "D1010",
        PalletCodeLength = 4,
        PositionCount = 1,
        Positions = [new ProductPositionDefinition { Index = 1, Name = "产品" }],
        Enabled = true
    };

    private IRenderedComponent<Stations> Render()
    {
        // 审计要显式返回已完成的任务：页面 await 它，返回 null 会直接炸在页面上。
        Audit
            .Setup(a => a.WriteAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        Config.Snapshot = new AppConfigurationSnapshot
        {
            PlcConnections = [Mitsubishi()],
            Stations = [Station()]
        };

        RenderPopoverHost();
        return Context.RenderComponent<Stations>();
    }

    [Fact]
    public void Trial_button_is_reachable_by_an_accessible_name()
    {
        var cut = Render();

        var button = cut.FindAll("button").Single(b => b.TextContent.Contains("试读一次"));
        var label = button.GetAttribute("aria-label");
        Assert.NotNull(label);
        Assert.Contains("ST010", label);
    }

    [Fact]
    public void Trial_renders_the_values_it_read()
    {
        Trial.Result = Result();
        var cut = Render();

        ClickButton(cut, "试读一次");

        Assert.Equal(1, Trial.CallCount);
        Assert.Contains("试读结果", cut.Markup);
        Assert.Contains("12.4 kPa", cut.Markup);
        Assert.Contains("PLT-000123", cut.Markup);
        // 原始字是判断字序问题的唯一线索，必须出现在结果里。
        Assert.Contains("0x000C", cut.Markup);
    }

    [Fact]
    public void Trigger_mismatch_and_unreadable_tag_are_called_out()
    {
        Trial.Result = Result() with
        {
            Trigger = new StationTrialTrigger("D1000", 1, 7, false, null),
            Tags =
            [
                new StationTrialTag("压力", "D1030", false, "0x000C", "12.4 kPa", false, false, false, "10–20"),
                new StationTrialTag("温度", "D1040", false, "", "—", true, false, false, "不判定")
            ]
        };

        var cut = Render();
        ClickButton(cut, "试读一次");

        // "不匹配"与"未读到"是两种不同的问题，页面必须分别说清楚。
        Assert.Contains("不匹配", cut.Markup);
        Assert.Contains("未读到", cut.Markup);
    }

    [Fact]
    public void Unavailable_trial_is_disabled_and_explains_why()
    {
        Trial.Unavailable = "采集已关闭：PLC 连接未建立，无法试读。请先开启采集。";
        var cut = Render();

        var button = cut.FindAll("button").Single(b => b.TextContent.Contains("试读一次"));
        Assert.True(button.HasAttribute("disabled"));
        // 光禁用不说原因，用户只会以为页面坏了。
        Assert.Contains("采集已关闭", cut.Markup);
    }

    [Fact]
    public void Trial_failure_is_shown_instead_of_breaking_the_page()
    {
        Trial.Result = Result() with { Error = "点位地址非法: 这不是地址", Tags = [], Curves = [] };
        var cut = Render();

        ClickButton(cut, "试读一次");

        Assert.Contains("点位地址非法", cut.Markup);
        Assert.Contains("试读结果", cut.Markup);
    }

    [Fact]
    public void Trial_is_audited()
    {
        Trial.Result = Result();
        var cut = Render();

        ClickButton(cut, "试读一次");

        Audit.Verify(
            a => a.WriteAsync(
                "admin", "Trial", "Station", "ST010", null, It.IsAny<string>(),
                It.IsAny<CancellationToken>(), "Success", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Once);
    }

    private static StationTrialResult Result() => new(
        new DateTime(2026, 9, 30, 10, 0, 0),
        ElapsedMs: 47,
        BlockCount: 3,
        WordCount: 64,
        Trigger: new StationTrialTrigger("D1000", 1, 1, true, null),
        PalletCode: "PLT-000123",
        Occupied: true,
        Tags:
        [
            new StationTrialTag("压力", "D1030", false, "0x000C", "12.4 kPa", false, false, false, "10–20")
        ],
        Curves: [],
        Error: null);
}
