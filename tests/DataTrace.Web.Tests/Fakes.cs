using DataTrace.Application.Configuration;
using DataTrace.Collector;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Dialogs;
using DataTrace.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DataTrace.Web.Tests;

/// <summary>内存配置仓库：暴露一份快照，记录写入调用。</summary>
public sealed class FakeConfigRepository : IConfigRepository
{
    public AppConfigurationSnapshot Snapshot { get; set; } = new();

    public List<string> Calls { get; } = [];

    public List<SystemSettings> SavedSettings { get; } = [];

    /// <summary>页面交回来的实体，供断言"界面把什么写下去了"。</summary>
    public List<Station> SavedStations { get; } = [];

    public List<TagDefinition> SavedTags { get; } = [];

    public List<CurveDefinition> SavedCurves { get; } = [];

    /// <summary>置为异常工厂即可让读取/保存失败，用于验证页面的降级分支。</summary>
    public Func<Exception>? FailWith { get; set; }

    public int SnapshotReads => Calls.Count(c => c == nameof(GetSnapshotAsync));

    public Task<AppConfigurationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetSnapshotAsync));
        ThrowIfConfigured();
        // 每次读都给出新的 Settings 实例，跟真库（每次从 EF 物化）一致。
        // 共用同一个实例时，界面上一改就等于改了"库里的那一行"，
        // "放弃修改"、"改了还没保存"这类用例就全都失去意义了。
        return Task.FromResult(new AppConfigurationSnapshot
        {
            Settings = CloneSettings(Snapshot.Settings),
            PlcConnections = Snapshot.PlcConnections,
            Stations = Snapshot.Stations,
            Recipes = Snapshot.Recipes,
            ActiveRecipe = Snapshot.ActiveRecipe,
            Version = Snapshot.Version
        });
    }

    private static SystemSettings CloneSettings(SystemSettings src) => new()
    {
        Id = src.Id,
        ScanIntervalMs = src.ScanIntervalMs,
        WriteRetryCount = src.WriteRetryCount,
        WriteRetryDelayMs = src.WriteRetryDelayMs,
        RetentionYears = src.RetentionYears,
        CurveRootPath = src.CurveRootPath,
        SpoolPath = src.SpoolPath,
        RuntimeDbPath = src.RuntimeDbPath,
        CollectEnabled = src.CollectEnabled,
        MesEnabled = src.MesEnabled,
        MesEndpoint = src.MesEndpoint,
        MesTimeoutSeconds = src.MesTimeoutSeconds,
        SimulatorAutoRun = src.SimulatorAutoRun,
        SimulatorIntervalMs = src.SimulatorIntervalMs,
        SimulatorNgPercent = src.SimulatorNgPercent,
        SimulatorPalletPool = src.SimulatorPalletPool,
        ActiveRecipeId = src.ActiveRecipeId
    };

    public Task SaveSettingsAsync(SystemSettings settings, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(SaveSettingsAsync));
        ThrowIfConfigured();
        SavedSettings.Add(settings);
        return Task.CompletedTask;
    }

    /// <summary>MES 推送状态：默认给一份"没有积压"的快照，用例可覆盖。</summary>
    public MesOutboxSnapshot MesOutboxStatus { get; set; } = new();

    public Task<MesOutboxSnapshot> GetMesOutboxStatusAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetMesOutboxStatusAsync));
        return Task.FromResult(MesOutboxStatus);
    }

    public Task<int> GetVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot.Version);

    public Task<IReadOnlyList<PlcConnection>> GetPlcConnectionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Snapshot.PlcConnections);

    public Task<PlcConnection?> GetPlcConnectionAsync(int id, CancellationToken cancellationToken = default)
        => Task.FromResult(Snapshot.PlcConnections.FirstOrDefault(x => x.Id == id));

    public Task SavePlcConnectionAsync(PlcConnection connection, CancellationToken cancellationToken = default)
        => Recorded(nameof(SavePlcConnectionAsync));

    public Task DeletePlcConnectionAsync(int id, CancellationToken cancellationToken = default)
        => Recorded(nameof(DeletePlcConnectionAsync));

    public Task<IReadOnlyList<Station>> GetStationsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Snapshot.Stations);

    public Task<Station?> GetStationAsync(int id, CancellationToken cancellationToken = default)
        => Task.FromResult(Snapshot.Stations.FirstOrDefault(x => x.Id == id));

    public Task SaveStationAsync(Station station, CancellationToken cancellationToken = default)
    {
        SavedStations.Add(station);
        return Recorded(nameof(SaveStationAsync));
    }

    public Task DeleteStationAsync(int id, CancellationToken cancellationToken = default)
        => Recorded(nameof(DeleteStationAsync));

    public Task SaveTagAsync(TagDefinition tag, CancellationToken cancellationToken = default)
    {
        SavedTags.Add(tag);
        return Recorded(nameof(SaveTagAsync));
    }

    public Task DeleteTagAsync(int id, CancellationToken cancellationToken = default)
        => Recorded(nameof(DeleteTagAsync));

    public Task SaveCurveAsync(CurveDefinition curve, CancellationToken cancellationToken = default)
    {
        SavedCurves.Add(curve);
        return Recorded(nameof(SaveCurveAsync));
    }

    public Task DeleteCurveAsync(int id, CancellationToken cancellationToken = default)
        => Recorded(nameof(DeleteCurveAsync));

    public Task SaveCurveCriteriaAsync(int curveId, IReadOnlyList<CurveCriterion> criteria, CancellationToken cancellationToken = default)
        => Recorded(nameof(SaveCurveCriteriaAsync));

    public Task SaveHeartbeatAsync(HeartbeatSettings heartbeat, CancellationToken cancellationToken = default)
        => Recorded(nameof(SaveHeartbeatAsync));

    public Task<IReadOnlyList<Recipe>> GetRecipesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Snapshot.Recipes);

    public Task SaveRecipeAsync(Recipe recipe, CancellationToken cancellationToken = default)
        => Recorded(nameof(SaveRecipeAsync));

    public Task SaveRecipeLimitsAsync(int recipeId, IReadOnlyList<RecipeLimit> limits, CancellationToken cancellationToken = default)
        => Recorded(nameof(SaveRecipeLimitsAsync));

    public Task DeleteRecipeAsync(int id, CancellationToken cancellationToken = default)
        => Recorded(nameof(DeleteRecipeAsync));

    public Task SetActiveRecipeAsync(int? recipeId, CancellationToken cancellationToken = default)
        => Recorded(nameof(SetActiveRecipeAsync));

    public Task BumpVersionAsync(CancellationToken cancellationToken = default)
        => Recorded(nameof(BumpVersionAsync));

    private Task Recorded(string call)
    {
        Calls.Add(call);
        return Task.CompletedTask;
    }

    /// <summary>注入了失败原因就抛出去，用来驱动页面的降级分支。</summary>
    private void ThrowIfConfigured()
    {
        if (FailWith is { } factory)
        {
            throw factory();
        }
    }
}

/// <summary>可控的模拟产线，只记录调用并回抛状态。</summary>
public sealed class FakeLineSimulator : ILineSimulator
{
    public LineSimulatorStatus Status { get; set; } = new();

    public List<bool> RunningChanges { get; } = [];

    public List<string?> RunRequests { get; } = [];

    public Func<Exception>? FailRunWith { get; set; }

    /// <summary>默认"走完了"；用例可改成超时/中断，验证页面不再无脑报成功。</summary>
    public LineRunResult RunResult { get; set; } = new(true, null, false, "托盘已走完全线");

    public event Action? Changed;

    public void RaiseChanged() => Changed?.Invoke();

    public void SetRunning(bool running) => RunningChanges.Add(running);

    public Task<LineRunResult> RunOnePalletAsync(string? palletCode = null, CancellationToken cancellationToken = default)
    {
        RunRequests.Add(palletCode);
        if (FailRunWith is { } factory)
        {
            throw factory();
        }

        return Task.FromResult(RunResult);
    }
}

/// <summary>MudBlazor 的提示条替身：ISnackbar 成员太多，用 mock 接住，只把消息收集出来。</summary>
public sealed class ToastSpy
{
    public List<(string Message, Severity Severity, Action<SnackbarOptions>? Configure, string? Key)> Shown { get; } = [];

    public Mock<ISnackbar> Mock { get; } = new();

    public ToastSpy()
    {
        Mock.Setup(s => s.Add(
                It.IsAny<string>(),
                It.IsAny<Severity>(),
                It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string?>()))
            .Callback<string, Severity, Action<SnackbarOptions>, string?>((m, severity, configure, key) => Shown.Add((m, severity, configure, key)));
    }

    public IReadOnlyList<string> Messages => Shown.Select(x => x.Message).ToList();

    public string? LastMessage => Shown.Count == 0 ? null : Shown[^1].Message;

    public Severity LastSeverity => Shown.Count == 0 ? Severity.Info : Shown[^1].Severity;

    /// <summary>回放 DtToast 挂上去的时长/交互配置，用来断言严重度分档。</summary>
    public SnackbarOptions OptionsOf(int index = 0)
    {
        var options = new SnackbarOptions(Shown[index].Severity, new SnackbarConfiguration());
        Shown[index].Configure?.Invoke(options);
        return options;
    }
}

/// <summary>确认框替身：记录弹了几次、什么文案，并按脚本返回是/否；同时记录编辑类对话框的下发参数。</summary>
public sealed class DialogSpy
{
    public List<(string Title, string Message)> MessageBoxes { get; } = [];

    /// <summary>打开过的编辑对话框：(标题, 参数)。用来断言页面下发了什么默认值。</summary>
    public List<(string Title, DialogParameters Parameters)> Shown { get; } = [];

    public bool? MessageBoxResult { get; set; } = true;

    /// <summary>对话框的返回结果；不设则视为用户取消（页面据此不做任何写入）。</summary>
    public DialogResult? DialogResult { get; set; }

    public Mock<IDialogService> Mock { get; } = new();

    public DialogSpy()
    {
        // IDialogService 有 string 与 MarkupString 两个同形重载，页面传字面量时绑定到前者，
        // 只桩一个会让 Moq 落到默认返回值上，确认框看起来就像"没弹"。
        Mock.Setup(d => d.ShowMessageBox(
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DialogOptions?>()))
            .Callback<string?, string, string, string?, string?, DialogOptions?>(Record)
            .Returns(() => Task.FromResult(MessageBoxResult));

        Mock.Setup(d => d.ShowMessageBox(
                It.IsAny<string?>(),
                It.IsAny<MarkupString>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DialogOptions?>()))
            .Callback<string?, MarkupString, string, string?, string?, DialogOptions?>(
                (title, message, yes, cancel, secondary, options) => Record(title, message.ToString(), yes, cancel, secondary, options))
            .Returns(() => Task.FromResult(MessageBoxResult));

        Mock.Setup(d => d.ShowAsync<PlcEditDialog>(
                It.IsAny<string>(),
                It.IsAny<DialogParameters>(),
                It.IsAny<DialogOptions>()))
            .Returns((string title, DialogParameters parameters, DialogOptions _) =>
            {
                lock (Shown)
                {
                    Shown.Add((title, parameters));
                }

                var reference = new Mock<IDialogReference>();
                reference.SetupGet(r => r.Result).Returns(() => Task.FromResult(DialogResult));
                return Task.FromResult(reference.Object);
            });

        // 点位编辑对话框：页面把它当"表单入口"，用例要断言下发过哪些参数、拿到结果后写了什么。
        Mock.Setup(d => d.ShowAsync<TagEditDialog>(
                It.IsAny<string>(),
                It.IsAny<DialogParameters>(),
                It.IsAny<DialogOptions>()))
            .Returns((string title, DialogParameters parameters, DialogOptions _) =>
            {
                lock (Shown)
                {
                    Shown.Add((title, parameters));
                }

                var reference = new Mock<IDialogReference>();
                reference.SetupGet(r => r.Result).Returns(() => Task.FromResult(DialogResult));
                return Task.FromResult(reference.Object);
            });

        // 曲线编辑对话框：同上。
        Mock.Setup(d => d.ShowAsync<CurveEditDialog>(
                It.IsAny<string>(),
                It.IsAny<DialogParameters>(),
                It.IsAny<DialogOptions>()))
            .Returns((string title, DialogParameters parameters, DialogOptions _) =>
            {
                lock (Shown)
                {
                    Shown.Add((title, parameters));
                }

                var reference = new Mock<IDialogReference>();
                reference.SetupGet(r => r.Result).Returns(() => Task.FromResult(DialogResult));
                return Task.FromResult(reference.Object);
            });

        // 波形判据对话框：不桩的话 ShowAsync 返回 null，页面解引用 Result 时会 NRE。
        Mock.Setup(d => d.ShowAsync<CurveCriterionDialog>(
                It.IsAny<string>(),
                It.IsAny<DialogParameters>(),
                It.IsAny<DialogOptions>()))
            .Returns((string title, DialogParameters parameters, DialogOptions _) =>
            {
                lock (Shown)
                {
                    Shown.Add((title, parameters));
                }

                var reference = new Mock<IDialogReference>();
                reference.SetupGet(r => r.Result).Returns(() => Task.FromResult(DialogResult));
                return Task.FromResult(reference.Object);
            });
    }

    private void Record(string? title, string message, string? yesText = null, string? cancelText = null, string? secondaryText = null, DialogOptions? options = null)
        => MessageBoxes.Add((title ?? "", message));
}

/// <summary>系统文件框替身：不弹真正的对话框，按用例给定的路径回填。</summary>
public sealed class FakeJsonFileDialog : IJsonFileDialog
{
    public string? Result { get; set; }

    public string? LastRequest { get; set; }

    public DataFileFormat LastFormat { get; set; }

    public Task<string?> PickAsync(string? currentPath, DataFileFormat format)
    {
        LastRequest = currentPath;
        LastFormat = format;
        return Task.FromResult(Result);
    }
}
