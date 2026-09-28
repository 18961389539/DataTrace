using DataTrace.Domain.Entities;

namespace DataTrace.Application.Configuration;

/// <summary>一次配置写入附带的审计内容。保存成功之后才写；写审计失败不回滚已经下发的配置。</summary>
public sealed record ConfigAudit(
    string UserName,
    string Action,
    string EntityType,
    string? EntityKey,
    string? OldValue,
    string? NewValue);

/// <summary>配置已经落库。审计失败时把原因交回界面，由界面单独提示。</summary>
public sealed class ConfigurationChangeResult
{
    public string? AuditError { get; init; }

    public static ConfigurationChangeResult Ok { get; } = new();

    public static ConfigurationChangeResult AuditFailed(string message) => new() { AuditError = message };
}

/// <summary>
/// 配置写入用例：页面交给它编辑模型，它收成命令再落库，审计跟同一次保存走。
/// </summary>
public interface IConfigurationChanges
{
    Task<ConfigurationChangeResult> SaveStationAsync(Station station, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task<ConfigurationChangeResult> DeleteStationAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task<ConfigurationChangeResult> SaveTagAsync(TagDefinition tag, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task<ConfigurationChangeResult> DeleteTagAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task<ConfigurationChangeResult> SaveCurveAsync(CurveDefinition curve, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task<ConfigurationChangeResult> DeleteCurveAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task<ConfigurationChangeResult> SaveCurveCriteriaAsync(int curveId, IReadOnlyList<CurveCriterion> criteria, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task<ConfigurationChangeResult> SaveRecipeAsync(Recipe recipe, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task<ConfigurationChangeResult> SaveRecipeLimitsAsync(int recipeId, IReadOnlyList<RecipeLimit> limits, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task<ConfigurationChangeResult> DeleteRecipeAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task<ConfigurationChangeResult> SetActiveRecipeAsync(int? recipeId, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task<ConfigurationChangeResult> SavePlcConnectionAsync(PlcConnection connection, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task<ConfigurationChangeResult> DeletePlcConnectionAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default);
    Task<ConfigurationChangeResult> SaveSettingsAsync(SystemSettings settings, ConfigAudit audit, CancellationToken cancellationToken = default);
}
