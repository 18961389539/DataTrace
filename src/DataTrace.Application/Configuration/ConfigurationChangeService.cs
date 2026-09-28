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

    public Task<ConfigurationChangeResult> SaveStationAsync(Station station, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.SaveStationAsync(SaveStationCommand.From(station), cancellationToken), audit, cancellationToken);

    public Task<ConfigurationChangeResult> DeleteStationAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.DeleteStationAsync(id, cancellationToken), audit, cancellationToken);

    public Task<ConfigurationChangeResult> SaveTagAsync(TagDefinition tag, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.SaveTagAsync(SaveTagCommand.From(tag), cancellationToken), audit, cancellationToken);

    public Task<ConfigurationChangeResult> DeleteTagAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.DeleteTagAsync(id, cancellationToken), audit, cancellationToken);

    public Task<ConfigurationChangeResult> SaveCurveAsync(CurveDefinition curve, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.SaveCurveAsync(SaveCurveCommand.From(curve), cancellationToken), audit, cancellationToken);

    public Task<ConfigurationChangeResult> DeleteCurveAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.DeleteCurveAsync(id, cancellationToken), audit, cancellationToken);

    public Task<ConfigurationChangeResult> SaveCurveCriteriaAsync(int curveId, IReadOnlyList<CurveCriterion> criteria, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(
            () => _repository.SaveCurveCriteriaAsync(curveId, criteria.Select(SaveCurveCriterionCommand.From).ToList(), cancellationToken),
            audit,
            cancellationToken);

    public Task<ConfigurationChangeResult> SaveRecipeAsync(Recipe recipe, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.SaveRecipeAsync(SaveRecipeCommand.From(recipe), cancellationToken), audit, cancellationToken);

    public Task<ConfigurationChangeResult> SaveRecipeLimitsAsync(int recipeId, IReadOnlyList<RecipeLimit> limits, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(
            () => _repository.SaveRecipeLimitsAsync(recipeId, limits.Select(SaveRecipeLimitCommand.From).ToList(), cancellationToken),
            audit,
            cancellationToken);

    public Task<ConfigurationChangeResult> DeleteRecipeAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.DeleteRecipeAsync(id, cancellationToken), audit, cancellationToken);

    public Task<ConfigurationChangeResult> SetActiveRecipeAsync(int? recipeId, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.SetActiveRecipeAsync(recipeId, cancellationToken), audit, cancellationToken);

    public Task<ConfigurationChangeResult> SavePlcConnectionAsync(PlcConnection connection, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.SavePlcConnectionAsync(SavePlcConnectionCommand.From(connection), cancellationToken), audit, cancellationToken);

    public Task<ConfigurationChangeResult> DeletePlcConnectionAsync(int id, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.DeletePlcConnectionAsync(id, cancellationToken), audit, cancellationToken);

    public Task<ConfigurationChangeResult> SaveSettingsAsync(SystemSettings settings, ConfigAudit audit, CancellationToken cancellationToken = default)
        => Change(() => _repository.SaveSettingsAsync(SaveSettingsCommand.From(settings), cancellationToken), audit, cancellationToken);

    private async Task<ConfigurationChangeResult> Change(Func<Task> save, ConfigAudit audit, CancellationToken cancellationToken)
    {
        await save().ConfigureAwait(false);
        try
        {
            await _audit.WriteAsync(
                    audit.UserName,
                    audit.Action,
                    audit.EntityType,
                    audit.EntityKey,
                    audit.OldValue,
                    audit.NewValue,
                    cancellationToken)
                .ConfigureAwait(false);
            return ConfigurationChangeResult.Ok;
        }
        catch (Exception ex)
        {
            return ConfigurationChangeResult.AuditFailed(ex.Message);
        }
    }
}
