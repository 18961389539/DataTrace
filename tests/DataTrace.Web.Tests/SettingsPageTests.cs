using DataTrace.Application.Backup;
using DataTrace.Application.Configuration;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Web.Components.Pages;
using DataTrace.Web.Services;
using DataTrace.Shared;
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
        var cut = Context.Render<Settings>();
        cut.WaitForState(() => !cut.Markup.Contains("mud-skeleton"));
        return cut;
    }

    [Fact]
    public void ShowsSkeletonThenForm()
    {
        var cut = Render();

        Assert.Contains("扫描间隔", cut.Markup);
        Assert.DoesNotContain("系统设置读取失败", cut.Markup);
        Assert.Single(Config.Calls, c => c == nameof(Config.GetSnapshotAsync));
    }

    [Fact]
    public void Legacy_write_retry_values_are_read_only_and_say_that_automatic_resend_is_disabled()
    {
        var cut = Render();

        Assert.Contains("旧版写回重试配置", cut.Markup);
        Assert.Contains("旧配置·只读", cut.Markup);
        Assert.Contains("不会自动重发", cut.Markup);
        Assert.DoesNotContain(cut.FindAll("input"), input =>
            input.GetAttribute("aria-label") is "写回重试次数" or "写回重试间隔(ms)");
    }

    [Fact]
    public void Configuration_revision_history_shows_changes_with_credentials_redacted()
    {
        const string oldSecret = "old-secret-value";
        const string newSecret = "new-secret-value";
        const string driverCredential = "driver-credential-value";
        const string mesEndpointCredential = "https://mes.local/push?token=endpoint-token";
        var summary = new ConfigurationRevisionSummary(
            9, 4, new DateTime(2026, 10, 11, 9, 30, 0), "admin", "修正工站地址", "Save",
            "Station", "ST010", "Passed: full snapshot", 8,
            "{\"changes\":[{\"path\":\"$.plcConnections[0].password\",\"before\":\"old-secret-value\",\"after\":\"new-secret-value\"},{\"path\":\"$.plcConnections[0].extra\",\"before\":\"driver-credential-value\",\"after\":\"new-driver-credential-value\"},{\"path\":\"$.settings.mesEndpoint\",\"before\":\"https://mes.local/push?token=endpoint-token\",\"after\":\"https://mes.local/push?token=new-endpoint-token\"},{\"path\":\"$.stations[0].triggerAddress\",\"before\":\"D100\",\"after\":\"D110\"}]}");
        Config.RevisionHistory.Add(summary);
        Config.RevisionDetails[summary.Id] = new ConfigurationRevision
        {
            Id = summary.Id,
            ParentRevisionId = summary.ParentRevisionId,
            ValidationResult = summary.ValidationResult,
            DiffJson = summary.DiffJson,
            SnapshotJson = "{}"
        };

        var cut = Render();

        Assert.Contains("配置修订历史", cut.Markup);
        Assert.Contains("修正工站地址", cut.Markup);
        cut.FindAll("button").Single(button => button.TextContent.Contains("查看差异", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("$.plcConnections[0].password", cut.Markup);
            Assert.Contains("[已脱敏]", cut.Markup);
            Assert.DoesNotContain(oldSecret, cut.Markup);
            Assert.DoesNotContain(newSecret, cut.Markup);
            Assert.DoesNotContain(driverCredential, cut.Markup);
            Assert.DoesNotContain("new-driver-credential-value", cut.Markup);
            Assert.DoesNotContain(mesEndpointCredential, cut.Markup);
            Assert.DoesNotContain("new-endpoint-token", cut.Markup);
            Assert.Contains("D100", cut.Markup);
            Assert.Contains("D110", cut.Markup);
        });
    }

    [Fact]
    public void ShowsFullBackupCoverageAndVerifiedFileCounts()
    {
        Backup.Setup(backup => backup.GetStatus()).Returns(new BackupStatusSnapshot
        {
            Enabled = true,
            LastSucceeded = true,
            LastSuccessAt = DateTimeOffset.Now,
            LastBackupFormatVersion = 2,
            LastDatabaseCount = 3,
            LastFileCount = 9,
            LastVerifiedFileCount = 9
        });

        var cut = Render();

        Assert.Contains("全量已校验", cut.Markup);
        Assert.Contains("v2", cut.Markup);
        Assert.Contains("3 个数据库", cut.Markup);
        Assert.Contains("9 / 9 个文件通过", cut.Markup);
        Assert.Contains("audit-archive", cut.Markup);
        Assert.Contains("SHA-256", cut.Markup);
    }

    [Fact]
    public void MarksLegacyDatabaseOnlyBackupAsNeedingFullBackup()
    {
        Backup.Setup(backup => backup.GetStatus()).Returns(new BackupStatusSnapshot
        {
            Enabled = true,
            LastSucceeded = true,
            LastSuccessAt = DateTimeOffset.Now,
            LastFileCount = 2
        });

        var cut = Render();

        Assert.Contains("旧版数据库备份", cut.Markup);
        Assert.Contains("旧版仅含数据库", cut.Markup);
        Assert.Contains("请重新生成全量备份", cut.Markup);
    }

    [Fact]
    public void ReadFailureShowsAlertAndToast()
    {
        Config.FailWith = () => new InvalidOperationException("配置库损坏");

        RenderPopoverHost();
        var cut = Context.Render<Settings>();

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
        Assert.Contains("已启用 MES 推送，但地址不是合法的 http(s) 地址", cut.Markup);
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

        // 保存整条链是异步的（读快照 → 落库 → 通知模拟器）：断言要等它落定，
        // 否则机器一忙就变成"还没轮到就断言"的偶发失败。
        cut.WaitForAssertion(() => Assert.Equal(new[] { false }, Simulator.RunningChanges));
    }

    [Fact]
    public void Unsaved_changes_show_a_summary_of_affected_settings()
    {
        var cut = Render();

        TypeIntoAriaLabel(cut, "扫描间隔(ms)", "120");

        Assert.Contains("查看本次改动 · 1 项", cut.Markup);
        Assert.Contains("扫描间隔：", cut.Markup);
        Assert.Contains("120 ms", cut.Markup);
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
        // 第二道门的勾选语要和后果对得上，否则用户勾的是一句看不懂的话。
        Assert.Contains("采集端不再读取任何工站", box.Acknowledge);
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

    /// <summary>
    /// 「关于」里要显示构建标识：现场核对"这台机器跑的是哪次提交"只有这一个入口。
    /// </summary>
    [Fact]
    public void About_section_shows_the_build_marker()
    {
        var cut = Render();

        Assert.Contains("构建标识", cut.Markup);
        Assert.Contains(BuildInfo.CommitLabel, cut.Markup);
    }

    /// <summary>
    /// 保留年数决定"什么时候删历史"，是删除授权而不是普通配置项：非管理员只能看。
    /// </summary>
    /// <remarks>
    /// 界面只读是第一道；绕过界面直接调服务的路径由 SettingsSaveService 的守卫拦住
    /// （见 DataTrace.Tests 的 SettingsSaveServiceTests）。
    /// </remarks>
    [Fact]
    public void RetentionYears_are_read_only_for_non_admins()
    {
        UseRole(AppRoles.Engineer);
        var cut = Render(s => s.RetentionYears = 3);

        Assert.True(InputForAriaLabel(cut, "保留年数").HasAttribute("disabled"));
        Assert.True(InputForAriaLabel(cut, "审计日志保留年数").HasAttribute("disabled"));
        Assert.Contains("仅管理员可修改", cut.Markup);
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
    public void Invalid_scan_interval_scrolls_to_the_collection_settings_section()
    {
        var cut = Render(s => s.ScanIntervalMs = 200);
        TypeIntoAriaLabel(cut, "扫描间隔(ms)", "");

        ClickButton(cut, "保存");

        cut.WaitForAssertion(() => Assert.Equal(
            "sec-collect",
            Context.JSInterop.VerifyInvoke("dtScrollIntoView").Arguments[0]));
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
        // 勾选语要把"删到哪个月"重复一遍：这是全局唯一的不可逆删除。
        Assert.Contains("无法恢复", box.Acknowledge);
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

    /// <summary>
    /// 审计写不进去时整笔回滚：不许出现"设置改了但查不到是谁改的"，也不许报成保存成功。
    /// </summary>
    /// <remarks>
    /// 这里断言的是页面行为（失败提示 + 没有成功提示）。真的"库里也没改"由
    /// DataTrace.Tests 用真 SQLite 配置库验证（替身不建真事务）。
    /// </remarks>
    [Fact]
    public void AuditFailureRollsTheSettingsBackInsteadOfSavingWithoutATrace()
    {
        Audit
            .Setup(a => a.WriteAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                "Success", "Blazor Server UI", null, null))
            .ThrowsAsync(new InvalidOperationException("审计库只读"));

        var cut = Render();
        TypeIntoAriaLabel(cut, "扫描间隔(ms)", "120");
        ClickButton(cut, "保存");

        Assert.Contains(Toast.Messages, m => m.Contains("审计库只读"));
        // 报"已保存并下发"的话，用户会以为新参数生效了，实际库里没变。
        Assert.DoesNotContain(Toast.Messages, m => m.Contains("配置已保存并下发采集端"));
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

    [Fact]
    public void SaveRejectsAnAlarmWebhookThatIsNotHttp()
    {
        var cut = Render();

        TypeIntoAriaLabel(cut, "异常呼叫地址", "notify.local/hook");
        ClickButton(cut, "保存");

        Assert.Empty(Config.SavedSettings);
        Assert.Equal(Severity.Warning, Toast.LastSeverity);
        Assert.Contains("异常呼叫地址不是合法的 http(s) 地址", Toast.LastMessage);
        Assert.Contains("异常呼叫地址不是合法的 http(s) 地址", cut.Markup);
    }

    [Fact]
    public void SaveStoresTheAlarmWebhook()
    {
        var cut = Render();

        TypeIntoAriaLabel(cut, "异常呼叫地址", "https://notify.local/datatrace");
        ClickButton(cut, "保存");

        Assert.Equal("https://notify.local/datatrace", Assert.Single(Config.SavedSettings).AlarmWebhookUrl);
    }
}
