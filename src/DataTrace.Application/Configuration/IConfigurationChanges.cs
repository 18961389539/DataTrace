using DataTrace.Domain.Entities;

namespace DataTrace.Application.Configuration;

/// <summary>一次配置写入附带的审计内容。与配置写入在同一个事务里落库，见 <see cref="IConfigurationChanges"/>。</summary>
public sealed record ConfigAudit(
    string UserName,
    string Action,
    string EntityType,
    string? EntityKey,
    string? OldValue,
    string? NewValue);

/// <summary>
/// 配置写入整体失败：事务已回滚，库里没有这次改动，也没有缺一条审计。
/// </summary>
/// <remarks>
/// 以前这里走的是「返回值 + AuditError」：配置已经落了库，只是界面额外提示一句"审计记录失败"。
/// 那等于留下一次"改过但查不到是谁改的"变更，事后追溯无从查证 —— 体系审核过不去。
/// 现在改成抛异常：调用方按"失败"报告，成功提示不会发出去。
/// </remarks>
public sealed class ConfigurationChangeFailedException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// 配置写入用例：页面交给它编辑模型，它收成命令再落库。
/// </summary>
/// <remarks>
/// 配置写入与它的审计记录在同一个事务里提交：要么都落库，要么都不落。
/// 失败一律抛 <see cref="ConfigurationChangeFailedException"/>，不再有"改成了但没留痕"这种中间态。
/// </remarks>
public interface IConfigurationChanges
{
    Task SaveStationAsync(Station station, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task DeleteStationAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task SaveTagAsync(TagDefinition tag, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task DeleteTagAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task SaveCurveAsync(CurveDefinition curve, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task DeleteCurveAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task SaveCurveCriteriaAsync(int curveId, IReadOnlyList<CurveCriterion> criteria, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task SaveRecipeAsync(Recipe recipe, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task SaveRecipeLimitsAsync(int recipeId, IReadOnlyList<RecipeLimit> limits, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task DeleteRecipeAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task SetActiveRecipeAsync(int? recipeId, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task SavePlcConnectionAsync(PlcConnection connection, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task DeletePlcConnectionAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(SystemSettings settings, ConfigAudit audit, CancellationToken cancellationToken = default);
}