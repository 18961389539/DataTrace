using DataTrace.Application.Configuration;
using DataTrace.Domain.Entities;
using DataTrace.Web.Components.Pages;

namespace DataTrace.Web.Tests;

/// <summary>系统设置页：读取降级、MES 地址守卫、停用采集二次确认、保存下发。</summary>
public class SettingsPageTests : WebTestBase
{
    private IRenderedComponent<Settings> Render(Action<SystemSettings>? mutate = null)
    {
        var settings = new SystemSettings();
        mutate?.Invoke(settings);
        Config.Snapshot = new AppConfigurationSnapshot { Settings = settings, Version = 1 };

        var cut = Context.RenderComponent<Settings>();
        cut.WaitForState(() => !cut.Markup.Contains("mud-skeleton"));
        return cut;
    }

    [Fact]
    public void ShowsSkeletonThenForm()
    {
        var cut = Render();

        Assert.Contains("扫描周期", cut.Markup);
        Assert.DoesNotContain("系统设置读取失败", cut.Markup);
        Assert.Single(Config.Calls.Where(c => c == nameof(Config.GetSnapshotAsync)));
    }

    [Fact]
    public void ReadFailureShowsAlertAndToast()
    {
        Config.FailWith = () => new InvalidOperationException("配置库损坏");

        var cut = Context.RenderComponent<Settings>();

        Assert.Contains("系统设置读取失败，请刷新页面重试。", cut.Markup);
        Assert.Equal("读取系统设置失败：配置库损坏", Toast.LastMessage);
        Assert.Equal(Severity.Error, Toast.LastSeverity);
    }

    [Fact]
    public void SaveIsBlockedWhenMesEnabledWithoutHttpEndpoint()
    {
        var cut = Render(s =>
        {
            s.MesEnabled = true;
            s.MesEndpoint = "mes.local/api";
        });

        ClickButton(cut, "保存");

        // 只弹警告，绝不落库：现场一旦写了坏地址，Outbox 会静默堆积。
        Assert.Empty(Config.SavedSettings);
        Assert.Equal(Severity.Warning, Toast.LastSeverity);
        Assert.Contains("不是合法的 http(s) 地址", Toast.LastMessage);
    }

    [Theory]
    [InlineData("http://mes.local/api")]
    [InlineData("https://mes.local/api")]
    public void SaveAcceptsHttpAndHttpsEndpoints(string endpoint)
    {
        var cut = Render(s =>
        {
            s.MesEnabled = true;
            s.MesEndpoint = endpoint;
        });

        ClickButton(cut, "保存");

        Assert.Equal(endpoint, Assert.Single(Config.SavedSettings).MesEndpoint);
        Assert.Equal(Severity.Success, Toast.LastSeverity);
    }

    [Fact]
    public void SavePushesSimulatorAutoRunToLine()
    {
        var cut = Render(s => s.SimulatorAutoRun = true);

        ClickButton(cut, "保存");

        Assert.Equal(new[] { true }, Simulator.RunningChanges);
    }

    [Fact]
    public void SaveFailureReportsErrorWithoutThrowing()
    {
        var cut = Render();
        Config.FailWith = () => new InvalidOperationException("只读");

        ClickButton(cut, "保存");

        Assert.Equal(Severity.Error, Toast.LastSeverity);
        Assert.Contains("保存失败：只读", Toast.LastMessage);
    }

    [Fact]
    public void EnablingCollectTogglesWithoutConfirmation()
    {
        var cut = Render(s => s.CollectEnabled = false);

        ToggleSwitch(cut, true);

        Assert.Empty(Dialogs.MessageBoxes);
        Assert.True(Config.Snapshot.Settings.CollectEnabled);
    }

    [Fact]
    public void DisablingCollectAsksForConfirmation()
    {
        var cut = Render(s => s.CollectEnabled = true);

        ToggleSwitch(cut, false);

        var box = Assert.Single(Dialogs.MessageBoxes);
        Assert.Equal("停用采集", box.Title);
        Assert.Contains("实时看板与查询都不会再有新数据", box.Message);
        Assert.False(Config.Snapshot.Settings.CollectEnabled);
    }

    [Fact]
    public void CancellingConfirmationKeepsCollectOn()
    {
        Dialogs.MessageBoxResult = false;
        var cut = Render(s => s.CollectEnabled = true);

        ToggleSwitch(cut, false);

        Assert.Single(Dialogs.MessageBoxes);
        Assert.True(Config.Snapshot.Settings.CollectEnabled);
    }

    [Fact]
    public void ReloadDiscardsUnsavedEdits()
    {
        var cut = Render(s => s.ScanIntervalMs = 1000);

        ClickButton(cut, "放弃修改并重读");

        Assert.Equal(2, Config.Calls.Count(c => c == nameof(Config.GetSnapshotAsync)));
        Assert.Equal(1000, Config.Snapshot.Settings.ScanIntervalMs);
    }
}
