using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Application.Configuration;

public sealed class AppConfigurationSnapshot
{
    public SystemSettings Settings { get; init; } = new();
    public IReadOnlyList<PlcConnection> PlcConnections { get; init; } = [];
    public IReadOnlyList<Station> Stations { get; init; } = [];

    /// <summary>全部产品型号（含已停用的，界面需要显示）。</summary>
    public IReadOnlyList<Recipe> Recipes { get; init; } = [];

    /// <summary>
    /// 当前生效的型号，由 <see cref="SystemSettings.ActiveRecipeId"/> 解析而来。
    /// 引用的型号不存在或已停用时为 null，此时一律按点位默认限值判定。
    /// </summary>
    public Recipe? ActiveRecipe { get; init; }

    public int Version { get; init; }
}

/// <summary>
/// MES 推送积压状态：有多少条还没推出去、最近一次成功/失败是什么时候。
/// </summary>
public sealed class MesOutboxSnapshot
{
    /// <summary>待推送条数。</summary>
    public int PendingCount { get; init; }

    /// <summary>最久未推出去的那条进入队列的时间；没有积压时为 null。</summary>
    public DateTime? OldestPendingAt { get; init; }

    /// <summary>最近一次推送尝试的时间与结果。</summary>
    public DateTime? LastAttemptAt { get; init; }

    public bool? LastAttemptSucceeded { get; init; }

    public string? LastError { get; init; }

    /// <summary>最近一次成功推送的时间。</summary>
    public DateTime? LastSuccessAt { get; init; }
}

public interface IConfigRepository
{
    Task<AppConfigurationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<int> GetVersionAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlcConnection>> GetPlcConnectionsAsync(CancellationToken cancellationToken = default);
    Task<PlcConnection?> GetPlcConnectionAsync(int id, CancellationToken cancellationToken = default);
    Task SavePlcConnectionAsync(PlcConnection connection, CancellationToken cancellationToken = default);
    Task DeletePlcConnectionAsync(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Station>> GetStationsAsync(CancellationToken cancellationToken = default);
    Task<Station?> GetStationAsync(int id, CancellationToken cancellationToken = default);
    Task SaveStationAsync(Station station, CancellationToken cancellationToken = default);
    Task DeleteStationAsync(int id, CancellationToken cancellationToken = default);

    Task SaveTagAsync(TagDefinition tag, CancellationToken cancellationToken = default);
    Task DeleteTagAsync(int id, CancellationToken cancellationToken = default);
    Task SaveCurveAsync(CurveDefinition curve, CancellationToken cancellationToken = default);
    Task DeleteCurveAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 单独保存某条曲线的波形判据：传入的集合即最终状态（按主键增量增删改）。
    /// 与 <see cref="SaveCurveAsync"/> 分开，是为了让编辑器不必改动被跟踪实体的导航集合。
    /// </summary>
    Task SaveCurveCriteriaAsync(int curveId, IReadOnlyList<CurveCriterion> criteria, CancellationToken cancellationToken = default);

    Task SaveHeartbeatAsync(HeartbeatSettings heartbeat, CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存系统设置。越界的取值（如保留年数为 0）会被拒绝并抛出 <see cref="InvalidOperationException"/>：
    /// 界面上的 Min/Max 只是输入框行为，脚本与历史脏数据可以直接写库，而这类值会删数据或压垮 PLC 通讯。
    /// </summary>
    Task SaveSettingsAsync(SystemSettings settings, CancellationToken cancellationToken = default);

    /// <summary>MES 推送积压与最近一次推送结果，供设置页显示对接健康状态。</summary>
    Task<MesOutboxSnapshot> GetMesOutboxStatusAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Recipe>> GetRecipesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存型号：先按主键增量同步限值覆盖行，再写入型号本身。
    /// 传入的 <see cref="Recipe.Limits"/> 即最终状态（空字段 = 沿用点位默认值）。
    /// </summary>
    Task SaveRecipeAsync(Recipe recipe, CancellationToken cancellationToken = default);

    /// <summary>
    /// 只保存型号的限值覆盖行：传入的集合即最终状态，名称/启用状态/备注一律不动。
    /// 限值编辑器用它，免得把可能已过期的整份型号写回去。
    /// </summary>
    Task SaveRecipeLimitsAsync(int recipeId, IReadOnlyList<RecipeLimit> limits, CancellationToken cancellationToken = default);

    Task DeleteRecipeAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 切换当前生效型号。传 null 表示回到"全部使用点位默认限值"。
    /// 型号不存在或已停用时抛 <see cref="InvalidOperationException"/>，避免静默切错。
    /// </summary>
    Task SetActiveRecipeAsync(int? recipeId, CancellationToken cancellationToken = default);

    Task BumpVersionAsync(CancellationToken cancellationToken = default);
}

public interface IAuditLogger
{
    Task WriteAsync(string userName, string action, string entityType, string? entityKey, string? oldValue, string? newValue, CancellationToken cancellationToken = default);

    /// <summary>
    /// 服务端筛选 + 分页。keyword 在用户/动作码/对象码/键/变更内容上做 Contains；
    /// 中文标签匹配请由调用方把命中的动作码/对象码传入 keywordMatched*。
    /// </summary>
    Task<(IReadOnlyList<AuditLog> Items, int Total)> QueryAsync(
        string? keyword = null,
        string? action = null,
        DateTime? fromInclusive = null,
        int skip = 0,
        int take = 50,
        IReadOnlyList<string>? keywordMatchedActions = null,
        IReadOnlyList<string>? keywordMatchedEntityTypes = null,
        CancellationToken cancellationToken = default);

    /// <summary>库中已出现过的动作码（下拉用），按字母序。</summary>
    Task<IReadOnlyList<string>> ListActionsAsync(CancellationToken cancellationToken = default);
}
