using DataTrace.Domain.Entities;
using DataTrace.Application.Evaluation;
using DataTrace.Application.Identity;
using DataTrace.Application.Realtime;
using DataTrace.Application.Runtime;

namespace DataTrace.Application.Configuration;

public sealed class ConfigurationChangeService : IConfigurationChanges
{
    private readonly IConfigRepository _repository;
    private readonly IAuditLogger _audit;
    private readonly IConfigurationAuthorizer? _authorizer;
    private readonly IDataMaintenanceBarrier? _maintenance;
    private readonly IRuntimeStatusHub? _status;
    private readonly ICurveBaselineCache? _baselines;

    public ConfigurationChangeService(IConfigRepository repository, IAuditLogger audit)
        : this(repository, audit, null, null, null, null)
    {
    }

    public ConfigurationChangeService(
        IConfigRepository repository,
        IAuditLogger audit,
        IConfigurationAuthorizer? authorizer,
        IDataMaintenanceBarrier? maintenance,
        IRuntimeStatusHub? status,
        ICurveBaselineCache? baselines)
    {
        _repository = repository;
        _audit = audit;
        _authorizer = authorizer;
        _maintenance = maintenance;
        _status = status;
        _baselines = baselines;
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

    public Task SaveWorkOrderAsync(WorkOrder workOrder, ConfigAudit audit, CancellationToken cancellationToken = default)
        => ChangeBusinessData(() => _repository.SaveWorkOrderAsync(workOrder, cancellationToken), audit, cancellationToken);

    public Task DeleteWorkOrderAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default)
        => ChangeBusinessData(() => _repository.DeleteWorkOrderAsync(id, cancellationToken), audit, cancellationToken);

    public Task SetCurrentWorkOrderAsync(int? id, ConfigAudit audit, CancellationToken cancellationToken = default)
        => ChangeBusinessData(() => _repository.SetCurrentWorkOrderAsync(id, cancellationToken), audit, cancellationToken);

    public Task RestoreRevisionAsync(long revisionId, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.RestoreRevisionAsync(revisionId, cancellationToken), audit with
        {
            Action = "RestoreConfigurationRevision",
            EntityType = "ConfigurationRevision",
            EntityKey = revisionId.ToString(),
            Reason = string.IsNullOrWhiteSpace(audit.Reason) ? $"回滚到修订 {revisionId}" : audit.Reason
        }, cancellationToken);

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
        IAsyncDisposable? maintenanceLease = null;
        AppConfigurationSnapshot? previous = null;
        var committed = false;
        try
        {
            await (_authorizer?.AuthorizeChangeAsync(audit.UserName, cancellationToken) ?? Task.CompletedTask).ConfigureAwait(false);
            var restoringRevision = string.Equals(audit.Action, "RestoreConfigurationRevision", StringComparison.Ordinal);
            if (_maintenance is not null)
            {
                // Restore replaces the entire configuration graph. Exclusive access drains any
                // in-flight collection and prevents another one starting during the replacement.
                maintenanceLease = restoringRevision
                    ? await _maintenance.EnterWriteAsync(cancellationToken).ConfigureAwait(false)
                    : await _maintenance.EnterReadAsync(cancellationToken).ConfigureAwait(false);
            }
            previous = await _repository.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (restoringRevision && previous.Settings.CollectEnabled)
                throw new InvalidOperationException("请先停用采集，再回滚完整配置修订；恢复期间不能继续采集。");
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
                await _repository.AppendRevisionAsync(audit, token).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            committed = true;

            var current = await _repository.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            PublishCommittedState(previous!, current);
        }
        catch (Exception ex)
        {
            if (committed)
                throw new ConfigurationChangeFailedException($"配置已提交为修订，运行状态刷新失败：{ex.Message}", ex);
            throw new ConfigurationChangeFailedException($"改动已回滚或未写入，配置未生效：{ex.Message}", ex);
        }
        finally
        {
            if (maintenanceLease is not null)
            {
                await maintenanceLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 业务档案写入（工单）：配置 + 审计同事务，但不生成配置修订、不重载采集器。
    /// </summary>
    /// <remarks>
    /// 工单不在 <see cref="AppConfigurationSnapshot"/> 里 —— 让它产生"空差分"修订会污染回滚链，
    /// 而回滚配置也不应该改变台账。
    /// </remarks>
    private async Task ChangeBusinessData(Func<Task> save, ConfigAudit audit, CancellationToken cancellationToken)
    {
        try
        {
            await (_authorizer?.AuthorizeChangeAsync(audit.UserName, cancellationToken) ?? Task.CompletedTask).ConfigureAwait(false);
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
            throw new ConfigurationChangeFailedException($"改动已回滚或未写入：{ex.Message}", ex);
        }
    }

    private void PublishCommittedState(AppConfigurationSnapshot previous, AppConfigurationSnapshot current)
    {
        if (_status is not null)
        {
            _status.SetActiveRecipe(current.ActiveRecipe?.Code, current.ActiveRecipe?.Name);
            _status.NotifyChanged();
        }

        if (_baselines is null) return;
        var before = previous.Recipes.ToDictionary(x => x.Id, x => x.Code);
        foreach (var recipe in current.Recipes)
        {
            if (before.TryGetValue(recipe.Id, out var oldCode)
                && !string.Equals(oldCode, recipe.Code, StringComparison.Ordinal))
            {
                _baselines.RetagRecipeCode(oldCode, recipe.Code);
            }
        }
    }
}
