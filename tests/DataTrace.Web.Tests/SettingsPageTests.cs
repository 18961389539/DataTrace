using DataTrace.Application.Configuration;
using DataTrace.Domain.Entities;
using DataTrace.Web.Components.Pages;
using Moq;

namespace DataTrace.Web.Tests;

/// <summary>系统设置页：读取降级、MES 地址守卫、停用采集二次确认、保存下发。</summary>
public class SettingsPageTests : WebTestBase
{
    private IRenderedComponent<Settings> Render(Action<SystemSettings>? mutate = null)
    {
        var settings = new SystemSettings();
        mutate?.Invoke(settings);
        Config.Snapshot = new AppConfigurationSnapshot { Settings = settings, Version = 1 };

        RenderPopoverHost();
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

        RenderPopoverHost();
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

    [Fact]
    public void ClearingScanIntervalBlocksTheSaveInsteadOfFallingToTheMinimum()
    {
        var cut = Render(s => s.ScanIntervalMs = 200);

        // 输入框被清空/敲进非法字符时拿到的是 null。以前 MudNumericField 会把它夹成 Min=20ms，
        // 采集端就会以十倍频率轮询 PLC，而界面只提示"配置已保存并下发采集端"。
        TypeIntoAriaLabel(cut, "扫描间隔(ms)", "");
        ClickButton(cut, "保存");

        Assert.Empty(Config.SavedSettings);
        Assert.Contains("扫描间隔不能为空", cut.Markup);
        Assert.Equal(Severity.Warning, Toast.LastSeverity);
    }

    [Fact]
    public void ClearingRetentionYearsBlocksTheSaveInsteadOfSchedulingADataPurge()
    {
        var cut = Render(s => s.RetentionYears = 3);

        // 这是最危险的夹取方向：清空后落成 1，清理任务会在 6 小时内删掉一年以前的历史。
        TypeIntoAriaLabel(cut, "保留年数", "");
        ClickButton(cut, "保存");

        Assert.Empty(Config.SavedSettings);
        Assert.Contains("保留年数不能为空", cut.Markup);
    }

    [Fact]
    public void ShrinkingRetentionYearsAsksForConfirmationAndCanBeCancelled()
    {
        Dialogs.MessageBoxResult = false;
        var cut = Render(s => s.RetentionYears = 3);

        TypeIntoAriaLabel(cut, "保留年数", "1");
        ClickButton(cut, "保存");

        // 确认框要说清"会删到哪个月"，否则用户不知道这条设置是不可逆的。
        var box = Assert.Single(Dialogs.MessageBoxes);
        Assert.Equal("确认保留年数", box.Title);
        Assert.Contains("3 → 1", box.Message);
        Assert.Contains("不可恢复", box.Message);
        Assert.Empty(Config.SavedSettings);

        // 确认之后再点一次：按新年限保存。
        Dialogs.MessageBoxResult = true;
        ClickButton(cut, "保存");

        Assert.Equal(2, Dialogs.MessageBoxes.Count);
        Assert.Equal(1, Assert.Single(Config.SavedSettings).RetentionYears);
    }

    [Fact]
    public void SaveOnlyWritesFieldsThisPageActuallyChanged()
    {
        var cut = Render(s =>
        {
            s.RetentionYears = 3;
            s.ScanIntervalMs = 200;
        });

        // 另一个会话刚把保留年数改成 5：本页手上的那一份已经过期。
        Config.Snapshot.Settings.RetentionYears = 5;

        TypeIntoAriaLabel(cut, "扫描间隔(ms)", "120");
        ClickButton(cut, "保存");

        // 只写自己改过的字段：整行覆盖会把别人刚保存的保留年数悄悄回滚成本页的旧值。
        var saved = Assert.Single(Config.SavedSettings);
        Assert.Equal(120, saved.ScanIntervalMs);
        Assert.Equal(5, saved.RetentionYears);
    }

    [Fact]
    public void AuditFailureIsReportedWithoutHidingTheSuccessfulSave()
    {
        Audit
            .Setup(a => a.WriteAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("审计库只读"));

        var cut = Render();
        TypeIntoAriaLabel(cut, "扫描间隔(ms)", "120");
        ClickButton(cut, "保存");

        // 设置已经落库下发，不能报成"保存失败"让人反复保存；但也不能一声不响。
        Assert.Single(Config.SavedSettings);
        Assert.Contains(Toast.Messages, m => m.Contains("审计记录失败") && m.Contains("审计库只读"));
        Assert.Contains(Toast.Messages, m => m.Contains("配置已保存并下发采集端"));
    }

    [Fact]
    public void MesBacklogIsVisibleInTheIntegrationSection()
    {
        Config.MesOutboxStatus = new MesOutboxSnapshot
        {
            PendingCount = 12,
            OldestPendingAt = DateTime.Now.AddHours(-3),
            LastAttemptAt = DateTime.Now.AddMinutes(-1),
            LastAttemptSucceeded = false,
            LastError = "HTTP 503",
            LastSuccessAt = DateTime.Now.AddHours(-4)
        };

        var cut = Render(s => s.MesEnabled = true);

        Assert.Contains("待推送", cut.Markup);
        Assert.Contains("12", cut.Markup);
        Assert.Contains("HTTP 503", cut.Markup);
        // 积压/失败要给一句判断，而不是只把数字摆出来。
        Assert.Contains("最近一次推送失败", cut.Markup);
    }

    [Fact]
    public void MesSectionSaysNothingIsQueuedWhenPushIsOff()
    {
        var cut = Render(s => s.MesEnabled = false);

        Assert.Contains("推送已关闭", cut.Markup);
        Assert.DoesNotContain("待推送", cut.Markup);
    }

    [Fact]
    public void MissingLogoFileIsReportedAfterSaving()
    {
        var cut = Render();

        TypeIntoAriaLabel(cut, "Logo 文件名", "不存在的logo.png");
        ClickButton(cut, "保存");

        // 保存本身要成功（文件名可能是刚拷进去还没放对位置），但必须提示顶栏会没有 Logo。
        Assert.Contains(Toast.Messages, m => m.Contains("品牌已写入 customer.json"));
        Assert.Contains(Toast.Messages, m => m.Contains("branding 目录下没有"));
    }
}
