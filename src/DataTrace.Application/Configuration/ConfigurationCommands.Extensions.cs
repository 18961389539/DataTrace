using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Application.Configuration;

public static class ConfigurationWriteExtensions
{
    public static async Task SavePlcConnectionAsync(this IConfigRepository repository, PlcConnection connection, CancellationToken cancellationToken = default)
    {
        connection.Id = await repository.SavePlcConnectionAsync(SavePlcConnectionCommand.From(connection), cancellationToken).ConfigureAwait(false);
        connection.Name = connection.Name.Trim();
    }

    public static Task SaveHeartbeatAsync(this IConfigRepository repository, HeartbeatSettings heartbeat, CancellationToken cancellationToken = default)
        => repository.SaveHeartbeatAsync(SaveHeartbeatCommand.From(heartbeat), cancellationToken);

    public static async Task SaveStationAsync(this IConfigRepository repository, Station station, CancellationToken cancellationToken = default)
    {
        StationWriteNormalizer.Apply(station);
        station.Id = await repository.SaveStationAsync(SaveStationCommand.From(station), cancellationToken).ConfigureAwait(false);
    }

    public static async Task SaveTagAsync(this IConfigRepository repository, TagDefinition tag, CancellationToken cancellationToken = default)
    {
        tag.Id = await repository.SaveTagAsync(SaveTagCommand.From(tag), cancellationToken).ConfigureAwait(false);
        tag.PositionIndex = 1;
        tag.Name = (tag.Name ?? "").Trim();
    }

    public static async Task SaveCurveAsync(this IConfigRepository repository, CurveDefinition curve, CancellationToken cancellationToken = default)
    {
        curve.Id = await repository.SaveCurveAsync(SaveCurveCommand.From(curve), cancellationToken).ConfigureAwait(false);
        curve.PositionIndex = 1;
        curve.Code = curve.Code.Trim();
    }

    public static Task SaveCurveCriteriaAsync(this IConfigRepository repository, int curveId, IReadOnlyList<CurveCriterion> criteria, CancellationToken cancellationToken = default)
        => repository.SaveCurveCriteriaAsync(curveId, criteria.Select(SaveCurveCriterionCommand.From).ToList(), cancellationToken);

    public static async Task SaveRecipeAsync(this IConfigRepository repository, Recipe recipe, CancellationToken cancellationToken = default)
    {
        var saved = await repository.SaveRecipeAsync(SaveRecipeCommand.From(recipe), cancellationToken).ConfigureAwait(false);
        recipe.Id = saved.Id;
        recipe.Code = saved.Code;
        recipe.Name = saved.Name;
    }

    public static Task SaveRecipeLimitsAsync(this IConfigRepository repository, int recipeId, IReadOnlyList<RecipeLimit> limits, CancellationToken cancellationToken = default)
        => repository.SaveRecipeLimitsAsync(recipeId, limits.Select(SaveRecipeLimitCommand.From).ToList(), cancellationToken);

    public static Task SaveSettingsAsync(this IConfigRepository repository, SystemSettings settings, CancellationToken cancellationToken = default)
        => repository.SaveSettingsAsync(SaveSettingsCommand.From(settings), cancellationToken);
}

/// <summary>
/// 工站保存前的形状整理。仓储作用在命令物化出的副本上；实体重载再作用在调用方的编辑对象上，
/// 这样两边看到的工位数量和有料地址一致。
/// </summary>

public static class StationWriteNormalizer
{
    public static void Apply(Station station)
    {
        station.Code = station.Code.Trim();
        station.Name = station.Name.Trim();
        station.DataFilePath = (station.DataFilePath ?? "").Trim();
        station.PositionCount = 1;
        var keep = station.Positions.OrderBy(x => x.Index).FirstOrDefault(x => x.Index == 1)
                   ?? station.Positions.OrderBy(x => x.Index).FirstOrDefault();
        station.Positions.Clear();
        if (keep is null)
        {
            keep = new ProductPositionDefinition { Index = 1, Name = "产品" };
        }
        else
        {
            keep.Index = 1;
            // 有料地址要原样保留：采集端仍按它做空位判定。
            if (string.IsNullOrWhiteSpace(keep.Name) || keep.Name.StartsWith("产品位", StringComparison.Ordinal))
            {
                keep.Name = "产品";
            }
        }

        station.Positions.Add(keep);
    }
}
