using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DataTrace.Application.Configuration;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Validation;
using DataTrace.Plc.Addresses;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

partial class ConfigRepository
{
    private static readonly JsonSerializerOptions RevisionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        WriteIndented = false
    };

    public async Task<IReadOnlyList<ConfigurationRevisionSummary>> GetRevisionHistoryAsync(
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        if (take is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(take));
        return await ReadAsync(async db => (IReadOnlyList<ConfigurationRevisionSummary>)await db.ConfigurationRevisions
            .AsNoTracking()
            .OrderByDescending(x => x.Id)
            .Skip(skip)
            .Take(take)
            .Select(x => new ConfigurationRevisionSummary(
                x.Id, x.ConfigVersion, x.CreatedAt, x.UserName, x.Reason, x.Action,
                x.EntityType, x.EntityKey, x.ValidationResult, x.ParentRevisionId, x.DiffJson))
            .ToListAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    public Task<ConfigurationRevision?> GetRevisionAsync(long revisionId, CancellationToken cancellationToken = default)
        => ReadAsync(db => db.ConfigurationRevisions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == revisionId, cancellationToken), cancellationToken);

    public async Task AppendRevisionAsync(ConfigAudit audit, CancellationToken cancellationToken = default)
    {
        var latest = await _db.ConfigurationRevisions.OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var counter = await _db.ConfigVersions.Select(x => (int?)x.Version)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        var revision = new ConfigurationRevision
        {
            ConfigVersion = counter,
            CreatedAt = DateTime.Now,
            UserName = audit.UserName,
            Reason = string.IsNullOrWhiteSpace(audit.Reason) ? "未提供原因" : audit.Reason.Trim(),
            Action = audit.Action,
            EntityType = audit.EntityType,
            EntityKey = audit.EntityKey,
            ValidationResult = "Passed: full snapshot, relational and address validation",
            // A rollback revision points at the selected immutable revision it restores.
            // The detailed diff still records the immediately preceding revision in its JSON.
            ParentRevisionId = string.Equals(audit.Action, "RestoreConfigurationRevision", StringComparison.Ordinal)
                && long.TryParse(audit.EntityKey, out var restoredRevisionId)
                    ? restoredRevisionId
                    : latest?.Id
        };
        _db.ConfigurationRevisions.Add(revision);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var current = await LoadSnapshotAsync(_db, counter, cancellationToken).ConfigureAwait(false);
        if (SettingsLimits.Error(current.Settings) is { } settingsError)
            throw new InvalidDataException($"配置修订设置校验失败：{settingsError}");
        ValidateRevisionSnapshot(latest?.Id ?? 0, current);
        var snapshot = new AppConfigurationSnapshot
        {
            Settings = current.Settings,
            PlcConnections = current.PlcConnections,
            Stations = current.Stations,
            Recipes = current.Recipes,
            ActiveRecipe = current.ActiveRecipe,
            Version = current.Version,
            ConfigurationRevisionId = revision.Id
        };
        revision.SnapshotJson = JsonSerializer.Serialize(snapshot, RevisionJsonOptions);
        revision.DiffJson = BuildDiff(latest?.SnapshotJson, revision.SnapshotJson, latest?.Id);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreRevisionAsync(long revisionId, CancellationToken cancellationToken = default)
    {
        var revision = await _db.ConfigurationRevisions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == revisionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"配置修订 {revisionId} 不存在");
        var snapshot = JsonSerializer.Deserialize<AppConfigurationSnapshot>(revision.SnapshotJson, RevisionJsonOptions)
            ?? throw new InvalidDataException($"配置修订 {revisionId} 的完整快照无效");
        if (snapshot.Settings is null)
            throw new InvalidDataException($"配置修订 {revisionId} 缺少系统设置");
        if (SettingsLimits.Error(snapshot.Settings) is { } settingsError)
            throw new InvalidDataException($"配置修订 {revisionId} 的系统设置无效：{settingsError}");
        ValidateRevisionSnapshot(revisionId, snapshot);

        var positions = snapshot.Stations.SelectMany(x => x.Positions).ToList();
        var tags = snapshot.Stations.SelectMany(x => x.Tags).ToList();
        var curves = snapshot.Stations.SelectMany(x => x.Curves).ToList();
        var series = curves.SelectMany(x => x.Series).ToList();
        var criteria = curves.SelectMany(x => x.Criteria).ToList();
        var limits = snapshot.Recipes.SelectMany(x => x.Limits).ToList();
        var heartbeats = snapshot.PlcConnections.Where(x => x.Heartbeat is not null).Select(x => x.Heartbeat!).ToList();

        // Clear dependent tables in FK order. The caller wraps this and the new rollback revision
        // in one config + audit transaction, so any failed insert restores the prior live state.
        await _db.CurveCriteria.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await _db.CurveSeries.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await _db.Curves.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await _db.RecipeLimits.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await _db.Tags.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await _db.ProductPositions.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await _db.Stations.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await _db.Heartbeats.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await _db.PlcConnections.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await _db.Recipes.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await _db.SystemSettings.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        _db.ChangeTracker.Clear();

        _db.PlcConnections.AddRange(snapshot.PlcConnections.Select(x =>
        {
            x.Stations = [];
            x.Heartbeat = null;
            return x;
        }));
        _db.Stations.AddRange(snapshot.Stations.Select(x =>
        {
            x.PlcConnection = null;
            x.Positions = [];
            x.Tags = [];
            x.Curves = [];
            return x;
        }));
        _db.ProductPositions.AddRange(positions.Select(x =>
        {
            x.Station = null;
            return x;
        }));
        _db.Tags.AddRange(tags.Select(x =>
        {
            x.Station = null;
            return x;
        }));
        _db.Curves.AddRange(curves.Select(x =>
        {
            x.Station = null;
            x.Series = [];
            x.Criteria = [];
            return x;
        }));
        _db.CurveSeries.AddRange(series.Select(x =>
        {
            x.CurveDefinition = null;
            return x;
        }));
        _db.CurveCriteria.AddRange(criteria.Select(x =>
        {
            x.CurveDefinition = null;
            return x;
        }));
        _db.Recipes.AddRange(snapshot.Recipes.Select(x =>
        {
            x.Limits = [];
            return x;
        }));
        _db.RecipeLimits.AddRange(limits.Select(x =>
        {
            x.Recipe = null;
            return x;
        }));
        _db.Heartbeats.AddRange(heartbeats.Select(x =>
        {
            x.PlcConnection = null;
            return x;
        }));
        snapshot.Settings.ActiveRecipeId = snapshot.Settings.ActiveRecipeId is { } activeId
            && snapshot.Recipes.Any(x => x.Id == activeId && x.Enabled) ? activeId : null;
        _db.SystemSettings.Add(snapshot.Settings);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateRevisionSnapshot(long revisionId, AppConfigurationSnapshot snapshot)
    {
        var plcs = snapshot.PlcConnections ?? throw new InvalidDataException($"配置修订 {revisionId} 缺少 PLC 列表");
        var stations = snapshot.Stations ?? throw new InvalidDataException($"配置修订 {revisionId} 缺少工站列表");
        var recipes = snapshot.Recipes ?? throw new InvalidDataException($"配置修订 {revisionId} 缺少型号列表");
        EnsureUnique(plcs.Select(x => x.Id), "PLC", revisionId);
        EnsureUnique(stations.Select(x => x.Id), "工站", revisionId);
        EnsureUnique(recipes.Select(x => x.Id), "型号", revisionId);
        EnsureUnique(plcs.Select(x => x.Name.Trim().ToUpperInvariant()), "PLC名称", revisionId);
        EnsureUnique(stations.Select(x => x.Code.Trim().ToUpperInvariant()), "工站编码", revisionId);
        EnsureUnique(recipes.Select(x => x.Code.Trim().ToUpperInvariant()), "型号编码", revisionId);

        var plcById = plcs.ToDictionary(x => x.Id);
        var tagIds = stations.SelectMany(x => x.Tags).Select(x => x.Id).ToHashSet();
        foreach (var station in stations)
        {
            if (!plcById.TryGetValue(station.PlcConnectionId, out var plc))
                throw new InvalidDataException($"修订 {revisionId} 的工站 {station.Code} 引用不存在的 PLC {station.PlcConnectionId}");
            if (StationConfigLimits.TriggerValueError(station.TriggerValue) is { } triggerError)
                throw new InvalidDataException($"修订 {revisionId} 的工站 {station.Code}：{triggerError}");
            if (StationConfigLimits.PalletCodeLengthError(station.PalletCodeLength) is { } lengthError)
                throw new InvalidDataException($"修订 {revisionId} 的工站 {station.Code}：{lengthError}");
            ValidateAddress(plc.Brand, station.TriggerAddress, $"工站 {station.Code} 触发地址", revisionId);
            ValidateAddress(plc.Brand, station.PalletCodeAddress, $"工站 {station.Code} 托盘码地址", revisionId);
            EnsureUnique(station.Tags.Select(x => x.Id), $"工站 {station.Code} 点位", revisionId);
            EnsureUnique(station.Curves.Select(x => x.Id), $"工站 {station.Code} 曲线", revisionId);
            EnsureUnique(station.Positions.Select(x => x.Id), $"工站 {station.Code} 产品位置", revisionId);
            if (station.Positions.Any(x => x.StationId != station.Id))
                throw new InvalidDataException($"修订 {revisionId} 的工站 {station.Code} 产品位置外键无效");
            foreach (var tag in station.Tags)
            {
                if (tag.StationId != station.Id)
                    throw new InvalidDataException($"修订 {revisionId} 的点位 {tag.Name} 外键与所属工站不一致");
                if (tag.Source == TagDataSource.Plc)
                    ValidateAddress(plc.Brand, tag.Address, $"点位 {station.Code}/{tag.Name}", revisionId);
            }
            foreach (var curve in station.Curves)
            {
                if (curve.StationId != station.Id)
                    throw new InvalidDataException($"修订 {revisionId} 的曲线 {curve.Code} 工站外键无效");
                if (StationConfigLimits.PointCountError(curve.PointCount) is { } pointError)
                    throw new InvalidDataException($"修订 {revisionId} 的曲线 {curve.Code} 外键或点数无效：{pointError}");
                EnsureUnique(curve.Series.Select(x => x.Id), $"曲线 {curve.Code} 序列", revisionId);
                EnsureUnique(curve.Criteria.Select(x => x.Id), $"曲线 {curve.Code} 判据", revisionId);
                foreach (var series in curve.Series)
                {
                    if (series.CurveDefinitionId != curve.Id)
                        throw new InvalidDataException($"修订 {revisionId} 的曲线序列 {series.Name} 外键无效");
                    ValidateAddress(plc.Brand, series.StartAddress, $"曲线 {curve.Code}/{series.Name}", revisionId);
                }
                if (curve.Criteria.Any(x => x.CurveDefinitionId != curve.Id))
                    throw new InvalidDataException($"修订 {revisionId} 的曲线 {curve.Code} 判据外键无效");
            }
        }

        foreach (var recipe in recipes)
        {
            EnsureUnique(recipe.Limits.Select(x => x.Id), $"型号 {recipe.Code} 限值", revisionId);
            EnsureUnique(recipe.Limits.Select(x => x.TagId), $"型号 {recipe.Code} 点位限值", revisionId);
            if (recipe.Limits.Any(x => x.RecipeId != recipe.Id || !tagIds.Contains(x.TagId)))
                throw new InvalidDataException($"修订 {revisionId} 的型号 {recipe.Code} 引用了不存在的点位");
        }

        if (snapshot.Settings.ActiveRecipeId is { } activeId && !recipes.Any(x => x.Id == activeId && x.Enabled))
            throw new InvalidDataException($"修订 {revisionId} 的当前型号引用不存在或已停用");
    }

    private static void EnsureUnique<T>(IEnumerable<T> values, string subject, long revisionId)
    {
        if (values.GroupBy(x => x).Any(group => group.Count() > 1))
            throw new InvalidDataException($"配置修订 {revisionId} 存在重复的{subject} ID/值");
    }

    private static void ValidateAddress(PlcBrand brand, string? address, string subject, long revisionId)
    {
        if (!PlcAddressValidator.TryValidate(brand, address, out var error))
            throw new InvalidDataException($"配置修订 {revisionId} 的{subject}无效：{error}");
    }

    private static string BuildDiff(string? previousJson, string currentJson, long? previousId)
    {
        var changes = new List<object>();
        var previous = previousJson is null ? null : JsonNode.Parse(previousJson);
        var current = JsonNode.Parse(currentJson);
        AddDiff(previous, current, "$", changes);
        return JsonSerializer.Serialize(new { previousRevisionId = previousId, changes }, RevisionJsonOptions);
    }

    private static void AddDiff(JsonNode? before, JsonNode? after, string path, List<object> changes)
    {
        if (JsonNode.DeepEquals(before, after)) return;
        if (before is JsonObject beforeObject && after is JsonObject afterObject)
        {
            foreach (var key in beforeObject.Select(x => x.Key).Union(afterObject.Select(x => x.Key), StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
                AddDiff(beforeObject[key], afterObject[key], $"{path}.{key}", changes);
            return;
        }
        if (before is JsonArray beforeArray && after is JsonArray afterArray)
        {
            var count = Math.Max(beforeArray.Count, afterArray.Count);
            for (var i = 0; i < count; i++)
                AddDiff(i < beforeArray.Count ? beforeArray[i] : null, i < afterArray.Count ? afterArray[i] : null, $"{path}[{i}]", changes);
            return;
        }

        changes.Add(new { path, before = before?.DeepClone(), after = after?.DeepClone() });
    }
}
