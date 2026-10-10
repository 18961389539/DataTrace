using DataTrace.Application.Configuration;
using DataTrace.Collector;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
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

    private IRenderedComponent<Stations> Render(Station[]? stations = null, IStationTrialReader? reader = null)
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
            Stations = stations ?? [Station()]
        };

        if (reader is not null)
            Context.Services.AddSingleton(reader);
        RenderPopoverHost();
        return Context.Render<Stations>();
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
    public void Trial_and_save_actions_share_the_station_toolbar()
    {
        var cut = Render();

        var toolbar = cut.Find(".dt-station-actions");

        Assert.Contains("试读一次", toolbar.TextContent);
        Assert.Contains("保存 ST010", toolbar.TextContent);
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

    [Fact]
    public async Task Switching_stations_clears_the_completed_trial()
    {
        var other = Station();
        other.Id = 20;
        other.Code = "ST020";
        other.Sequence = 20;
        Trial.Result = Result();
        var cut = Render([Station(), other]);
        ClickButton(cut, "试读一次");

        await cut.InvokeAsync(() => cut.FindAll(".station-list-item-select")[1].Click());

        Assert.Contains("保存 ST020", cut.Markup);
        Assert.DoesNotContain("试读结果", cut.Markup);
        Assert.DoesNotContain("PLT-000123", cut.Markup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Switching_stations_discards_a_late_trial_even_after_switching_back(bool switchBack)
    {
        var other = Station();
        other.Id = 20;
        other.Code = "ST020";
        other.Sequence = 20;
        var pending = new TaskCompletionSource<StationTrialResult>();
        var reader = new Mock<IStationTrialReader>();
        reader.Setup(service => service.ReadAsync(It.IsAny<Station>(), It.IsAny<PlcConnection>(), It.IsAny<CancellationToken>()))
            .Returns(pending.Task);
        var cut = Render([Station(), other], reader.Object);
        var reading = cut.FindAll("button").Single(button => button.TextContent.Contains("试读一次")).ClickAsync();
        reader.Verify(service => service.ReadAsync(It.IsAny<Station>(), It.IsAny<PlcConnection>(), It.IsAny<CancellationToken>()), Times.Once);
        await cut.InvokeAsync(() => cut.FindAll(".station-list-item-select")[1].Click());
        if (switchBack)
            await cut.InvokeAsync(() => cut.FindAll(".station-list-item-select")[0].Click());

        await cut.InvokeAsync(() => pending.SetResult(Result()));
        await reading;

        Assert.DoesNotContain("试读结果", cut.Markup);
        Assert.DoesNotContain("PLT-000123", cut.Markup);
        Audit.Verify(service => service.WriteAsync("admin", "Trial", "Station", "ST010", null,
            It.IsAny<string>(), It.IsAny<CancellationToken>(), "Success", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Editing_addresses_marks_the_trial_stale_and_keeps_the_read_snapshot(bool editWhileReading)
    {
        var pending = new TaskCompletionSource<StationTrialResult>();
        Station? captured = null;
        var reader = new Mock<IStationTrialReader>();
        reader.Setup(service => service.ReadAsync(It.IsAny<Station>(), It.IsAny<PlcConnection>(), It.IsAny<CancellationToken>()))
            .Callback<Station, PlcConnection, CancellationToken>((station, connection, token) => captured = station)
            .Returns(pending.Task);
        var station = Station();
        station.Positions.First().OccupiedAddress = "D1020";
        var cut = Render([station], reader.Object);
        var reading = cut.FindAll("button").Single(button => button.TextContent.Contains("试读一次")).ClickAsync();
        if (!editWhileReading)
        {
            await cut.InvokeAsync(() => pending.SetResult(Result()));
            await reading;
        }
        await cut.InvokeAsync(() => cut.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "托盘码地址")
            .Instance.ValueChanged.InvokeAsync("D2010"));
        await cut.InvokeAsync(() => cut.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "有料地址")
            .Instance.ValueChanged.InvokeAsync("D2020"));
        if (editWhileReading)
        {
            await cut.InvokeAsync(() => pending.SetResult(Result()));
            await reading;
        }

        Assert.Equal("D1010", captured!.PalletCodeAddress);
        Assert.Equal("D1020", captured.Positions.First().OccupiedAddress);
        var panel = cut.Find(".dt-trial-panel");
        Assert.Contains("已过期", panel.TextContent);
        Assert.Contains("ST010", panel.TextContent);
        Assert.Contains("D1010", panel.TextContent);
        Assert.Contains("D1020", panel.TextContent);
        Assert.DoesNotContain("D2010", panel.TextContent);
        Assert.DoesNotContain("D2020", panel.TextContent);

        reader.Setup(service => service.ReadAsync(It.IsAny<Station>(), It.IsAny<PlcConnection>(), It.IsAny<CancellationToken>()))
            .Callback<Station, PlcConnection, CancellationToken>((station, connection, token) => captured = station)
            .ReturnsAsync(Result());
        ClickButton(cut, "试读一次");
        Assert.Equal("D2010", captured.PalletCodeAddress);
        Assert.Equal("D2020", captured.Positions.First().OccupiedAddress);
        panel = cut.Find(".dt-trial-panel");
        Assert.DoesNotContain("已过期", panel.TextContent);
        Assert.Contains("D2010", panel.TextContent);
        Assert.Contains("D2020", panel.TextContent);
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
