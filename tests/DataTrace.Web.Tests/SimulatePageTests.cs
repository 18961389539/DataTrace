using DataTrace.Application.Configuration;
using DataTrace.Collector;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Pages;

namespace DataTrace.Web.Tests;

/// <summary>PLC 仿真页：工站筛选与排序、托盘码校验、按钮互斥、触发前是否绑定模拟 PLC。</summary>
public class SimulatePageTests : WebTestBase
{
    private static Station St(int id, string code, int sequence, int plcId, bool enabled = true) => new()
    {
        Id = id,
        Code = code,
        Name = code,
        Sequence = sequence,
        PlcConnectionId = plcId,
        Enabled = enabled,
        TriggerAddress = "D200",
        TriggerValue = 1,
        PalletCodeAddress = "D100",
        PalletCodeLength = 16,
        PalletCodeDataType = PlcDataType.String
    };

    private static PlcConnection Plc(int id, PlcBrand brand) => new()
    {
        Id = id,
        Name = $"PLC{id}",
        Brand = brand,
        Enabled = true
    };

    private IRenderedComponent<Simulate> Render(
        IEnumerable<Station>? stations = null,
        IEnumerable<PlcConnection>? plcs = null,
        SystemSettings? settings = null)
    {
        Config.Snapshot = new AppConfigurationSnapshot
        {
            Stations = (stations ?? [St(1, "ST010", 1, 1)]).ToList(),
            PlcConnections = (plcs ?? [Plc(1, PlcBrand.Simulator)]).ToList(),
            Settings = settings ?? new SystemSettings()
        };

        return Context.RenderComponent<Simulate>();
    }

    [Fact]
    public void ShowsTriggerButtonsForEnabledStationsInSequenceOrder()
    {
        var cut = Render(
            stations:
            [
                St(1, "ST030", 2, 1),
                St(2, "ST010", 1, 1),
                St(3, "ST090", 3, 1, enabled: false)
            ]);

        var buttons = cut.FindAll("button")
            .Where(b => b.TextContent.Contains("触发"))
            .Select(b => b.TextContent.Trim())
            .ToList();

        Assert.Equal(["触发 ST010", "触发 ST030"], buttons);
    }

    [Fact]
    public void PromptsForStationSetupWhenNothingEnabled()
    {
        var cut = Render(stations: [St(1, "ST010", 1, 1, enabled: false)]);

        Assert.Contains("还没有启用的工站", cut.Markup);
        Assert.DoesNotContain("触发 ST010", cut.Markup);
    }

    [Fact]
    public void StatusChipFollowsSimulatorRunningFlag()
    {
        Simulator.Status = new LineSimulatorStatus { Running = true, CurrentPallet = "P0007" };

        var cut = Render();

        Assert.Contains("自动跑线中", cut.Markup);
        Assert.Contains("P0007", cut.Markup);
    }

    [Fact]
    public void EmptyPalletCodeBlocksRunAndExplainsWhy()
    {
        var cut = Render();

        TypeInto(cut, "指定托盘码", "   ");
        ClickButton(cut, "走完一条线");

        Assert.Contains("托盘码不能为空", cut.Markup);
        Assert.Empty(Simulator.RunRequests);
    }

    [Fact]
    public void OverlongPalletCodeIsRejectedBeforeHittingTheLine()
    {
        var cut = Render();

        TypeInto(cut, "指定托盘码", new string('P', 65));
        ClickButton(cut, "走完一条线");

        Assert.Contains("托盘码最长 64 个字符", cut.Markup);
        Assert.Empty(Simulator.RunRequests);
    }

    [Fact]
    public void RunTrimsPalletCodeAndReportsSuccess()
    {
        var cut = Render();

        TypeInto(cut, "指定托盘码", "  P0007  ");
        ClickButton(cut, "走完一条线");

        Assert.Equal(new[] { "P0007" }, Simulator.RunRequests);
        Assert.Equal(Severity.Success, Toast.LastSeverity);
        Assert.Contains("P0007 已仿真走线", Toast.LastMessage);
    }

    [Fact]
    public void RunFailureBecomesToastInsteadOfBreakingTheCircuit()
    {
        Simulator.FailRunWith = () => new InvalidOperationException("PLC 未连接");
        var cut = Render();

        ClickButton(cut, "走完一条线");

        Assert.Equal(Severity.Error, Toast.LastSeverity);
        Assert.Contains("PLC 未连接", Toast.LastMessage);
    }

    [Fact]
    public void AutoRunSwitchStartsLineAndPersistsSetting()
    {
        // 默认就是 true，先落到关闭态，拨到开才有"变化"可言。
        var cut = Render(settings: new SystemSettings { SimulatorAutoRun = false });

        ToggleSwitch(cut, true);

        Assert.Equal(new[] { true }, Simulator.RunningChanges);
        Assert.Contains(nameof(IConfigRepository.SaveSettingsAsync), Config.Calls);
        Assert.True(Config.Snapshot.Settings.SimulatorAutoRun);
    }

    [Fact]
    public void TriggerRefusesStationsNotBoundToSimulatorPlc()
    {
        var cut = Render(
            stations: [St(1, "ST010", 1, plcId: 7)],
            plcs: [Plc(7, PlcBrand.MitsubishiMc3E)]);

        ClickButton(cut, "触发 ST010");

        Assert.Equal(Severity.Warning, Toast.LastSeverity);
        Assert.Contains("该工站未绑定模拟 PLC", Toast.LastMessage);
        Assert.False(Simulators.TryGet(7, out _));
    }

    [Fact]
    public void TriggerLoadsCycleIntoSimulatorMemory()
    {
        var cut = Render();

        ClickButton(cut, "触发 ST010");

        Assert.Equal(Severity.Success, Toast.LastSeverity);
        Assert.Contains("已触发 ST010", Toast.LastMessage);
        Assert.True(Simulators.TryGet(1, out var driver));
        Assert.Equal(1, (short)driver!.GetWord("D200"));
    }

    [Fact]
    public void DisposingPageUnsubscribesFromSimulatorEvents()
    {
        var cut = Render();

        cut.Dispose();

        // 已释放的组件再收到回调会抛异常；这里必须安静地什么都不做。
        Simulator.RaiseChanged();
    }

    [Fact]
    public void SaveFailureKeepsButtonUsableAndWarns()
    {
        var cut = Render(settings: new SystemSettings { SimulatorAutoRun = false });
        Config.FailWith = () => new InvalidOperationException("配置库锁定");

        ToggleSwitch(cut, true);

        Assert.Equal(Severity.Error, Toast.LastSeverity);
        Assert.Contains("保存仿真参数失败：配置库锁定", Toast.LastMessage);
    }
}
