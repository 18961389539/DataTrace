using DataTrace.Application.Configuration;
using DataTrace.Collector;
using DataTrace.Domain.Entities;
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
        => Recorded(nameof(SaveStationAsync));

    public Task DeleteStationAsync(int id, CancellationToken cancellationToken = default)
        => Recorded(nameof(DeleteStationAsync));

    public Task SaveTagAsync(TagDefinition tag, CancellationToken cancellationToken = default)
        => Recorded(nameof(SaveTagAsync));

    public Task DeleteTagAsync(int id, CancellationToken cancellationToken = default)
        => Recorded(nameof(DeleteTagAsync));

    public Task SaveCurveAsync(CurveDefinition curve, CancellationToken cancellationToken = default)
        => Recorded(nameof(SaveCurveAsync));

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

    public event Action? Changed;

    public void RaiseChanged() => Changed?.Invoke();

    public void SetRunning(bool running) => RunningChanges.Add(running);

    public Task RunOnePalletAsync(string? palletCode = null, CancellationToken cancellationToken = default)
    {
        RunRequests.Add(palletCode);
        if (FailRunWith is { } factory)
        {
            throw factory();
        }

        return Task.CompletedTask;
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

/// <summary>确认框替身：记录弹了几次、什么文案，并按脚本返回是/否。</summary>
public sealed class DialogSpy
{
    public List<(string Title, string Message)> MessageBoxes { get; } = [];

    public bool? MessageBoxResult { get; set; } = true;

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
    }

    private void Record(string? title, string message, string? yesText = null, string? cancelText = null, string? secondaryText = null, DialogOptions? options = null)
        => MessageBoxes.Add((title ?? "", message));
}
