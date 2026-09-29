using DataTrace.Domain.Entities;

namespace DataTrace.Application.Configuration;

public sealed class ConfigurationChangeService : IConfigurationChanges
{
    private readonly IConfigRepository _repository;
    private readonly IAuditLogger _audit;

    public ConfigurationChangeService(IConfigRepository repository, IAuditLogger audit)
    {
        _repository = repository;
        _audit = audit;
    }

    public Task SaveStationAsync(Station station, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.SaveStationAsync(SaveStationCommand.From(station), cancellationToken), audit, cancellationToken);

    public Task DeleteStationAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.DeleteStationAsync(id, cancellationToken), audit, cancellationToken);

    public Task SaveTagAsync(TagDefinition tag, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.SaveTagAsync(SaveTagCommand.From(tag), cancellationToken), audit, cancellationToken);

    public Task DeleteTagAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.DeleteTagAsync(id, cancellationToken), audit, cancellationToken);

    public Task SaveCurveAsync(CurveDefinition curve, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.SaveCurveAsync(SaveCurveCommand.From(curve), cancellationToken), audit, cancellationToken);

    public Task DeleteCurveAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.DeleteCurveAsync(id, cancellationToken), audit, cancellationToken);

    public Task SaveCurveCriteriaAsync(int curveId, IReadOnlyList<CurveCriterion> criteria, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(
            () => _repository.SaveCurveCriteriaAsync(curveId, criteria.Select(SaveCurveCriterionCommand.From).ToList(), cancellationToken),
            audit,
            cancellationToken);

    public Task SaveRecipeAsync(Recipe recipe, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.SaveRecipeAsync(SaveRecipeCommand.From(recipe), cancellationToken), audit, cancellationToken);

    public Task SaveRecipeLimitsAsync(int recipeId, IReadOnlyList<RecipeLimit> limits, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(
            () => _repository.SaveRecipeLimitsAsync(recipeId, limits.Select(SaveRecipeLimitCommand.From).ToList(), cancellationToken),
            audit,
            cancellationToken);

    public Task DeleteRecipeAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.DeleteRecipeAsync(id, cancellationToken), audit, cancellationToken);

    public Task SetActiveRecipeAsync(int? recipeId, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.SetActiveRecipeAsync(recipeId, cancellationToken), audit, cancellationToken);

    public Task SavePlcConnectionAsync(PlcConnection connection, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.SavePlcConnectionAsync(SavePlcConnectionCommand.From(connection), cancellationToken), audit, cancellationToken);

    public Task DeletePlcConnectionAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.DeletePlcConnectionAsync(id, cancellationToken), audit, cancellationToken);

    public Task SaveSettingsAsync(SystemSettings settings, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.SaveSettingsAsync(SaveSettingsCommand.From(settings), cancellationToken), audit, cancellationToken);

    /// <summary>
    /// 配置写入 + 审计写入，同一个事务提交：要么都落库，要么都不落。
    /// </summary>
    /// <remarks>
    /// 顺序仍是"先配置、后审计"：反过来的话审计会记下一次并未发生的变更。
    /// 审计失败时抛 <see cref="ConfigurationChangeFailedException"/>（事务已回滚），
    /// 调用方的 catch 会按"失败"报告 —— 不会再出现"保存成功提示 + 审计缺失"并存。
    /// </remarks>
    private async Task Change(Func<Task> save, ConfigAudit audit, CancellationToken cancellationToken)
    {
        try
        {
            await _repository.InTransactionAsync(async token =>
            {
                await save().ConfigureAwait(false);
                await _audit.WriteAsync(
                        audit.UserName,
                        audit.Action,
                        audit.EntityType,
                        audit.EntityKey,
                        audit.OldValue,
                        audit.NewValue,
                        token)
                    .ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new ConfigurationChangeFailedException($"改动已回滚，未生效：{ex.Message}", ex);
        }
    }
}