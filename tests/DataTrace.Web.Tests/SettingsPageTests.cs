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

        Assert.Contains("扫描间隔", cut.Markup);
        Assert.DoesNotContain("系统设置读取失败", cut.Markup);
        Assert.Single(Config.Calls.Where(c => c == nameof(Config.GetSnapshotAsync)));
    }

    [Fact]
    public void ReadFailureShowsAlertAndToast()
    {
        Config.FailWith = () => new InvalidOperationException("配置库损坏");

        var cut = Context.RenderComponent<Settings>();

        // 读配置在 await 之后：首帧还是骨架，降级态要等下一轮渲染。
        cut.WaitForState(() => cut.Markup.Contains("系统配置读取失败，请刷新页面重试。"));
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
        // 从旧地址改起：页面只在"真的有改动"时才保存，一上来就是目标值会直接跳过保存。
        var cut = Render(s =>
        {
            s.MesEnabled = true;
            s.MesEndpoint = "http://old.local/api";
        });

        TypeIntoAriaLabel(cut, "MES 地址", endpoint);
        ClickButton(cut, "保存");

        Assert.Equal(endpoint, Assert.Single(Config.SavedSettings).MesEndpoint);
        Assert.Equal(Severity.Success, Toast.LastSeverity);
    }

    [Fact]
    public void SavePushesSimulatorAutoRunToLine()
    {
        var cut = Render(s => s.SimulatorAutoRun = false);

        // 先改一处让页面变"脏"，否则保存会被"没有需要保存的更改"挡下。
        TypeIntoAriaLabel(cut, "扫描间隔(ms)", "120");
        ClickButton(cut, "保存");

        Assert.Equal(new[] { false }, Simulator.RunningChanges);
    }

    [Fact]
    public void SaveFailureReportsErrorWithoutThrowing()
    {
        var cut = Render();
        TypeIntoAriaLabel(cut, "扫描间隔(ms)", "120");
        Config.FailWith = () => new InvalidOperationException("只读");

        ClickButton(cut, "保存");

        Assert.Equal(Severity.Error, Toast.LastSeverity);
        Assert.Contains("保存失败：只读", Toast.LastMessage);
    }

    [Fact]
    public void EnablingCollectTogglesWithoutConfirmation()
    {
        var cut = Render(s => s.CollectEnabled = false);

        // 启用是恢复生产，不需要二次确认；停用才要。
        ClickButton(cut, "重新启用采集");

        Assert.Empty(Dialogs.MessageBoxes);
        // 断言页面上看到的开关状态：库里的那一份要等「保存」才改。
        Assert.Contains("采集已启用", cut.Markup);
    }

    [Fact]
    public void DisablingCollectAsksForConfirmation()
    {
        var cut = Render(s => s.CollectEnabled = true);

        ClickButton(cut, "停用采集");

        var box = Assert.Single(Dialogs.MessageBoxes);
        Assert.Equal("停用采集", box.Title);
        Assert.Contains("实时看板与查询仍可看历史数据", box.Message);
        Assert.Contains("采集已停用", cut.Markup);
    }

    [Fact]
    public void CancellingConfirmationKeepsCollectOn()
    {
        Dialogs.MessageBoxResult = false;
        var cut = Render(s => s.CollectEnabled = true);

        ClickButton(cut, "停用采集");

        Assert.Single(Dialogs.MessageBoxes);
        Assert.Contains("采集已启用", cut.Markup);
    }

    [Fact]
    public void ReloadDiscardsUnsavedEdits()
    {
        var cut = Render(s => s.ScanIntervalMs = 1000);

        // 改动之后底部才会换成"放弃 / 保存"这一对，也才谈得上"放弃"。
        TypeIntoAriaLabel(cut, "扫描间隔(ms)", "120");
        Assert.Contains("有未保存的更改", cut.Markup);

        ClickButton(cut, "放弃");

        Assert.Equal(2, Config.Calls.Count(c => c == nameof(Config.GetSnapshotAsync)));
        Assert.Equal(1000, Config.Snapshot.Settings.ScanIntervalMs);
        Assert.DoesNotContain("有未保存的更改", cut.Markup);
    }
}
