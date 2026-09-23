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
    Task SaveSettingsAsync(SystemSettings settings, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Recipe>> GetRecipesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存型号：先按主键增量同步限值覆盖行，再写入型号本身。
    /// 传入的 <see cref="Recipe.Limits"/> 即最终状态（空字段 = 沿用点位默认值）。
    /// </summary>
    Task SaveRecipeAsync(Recipe recipe, CancellationToken cancellationToken = default);

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
    Task<IReadOnlyList<AuditLog>> QueryAsync(int take = 200, CancellationToken cancellationToken = default);
}
