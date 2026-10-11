using System.Text.Json;
using System.Text.Json.Serialization;
using DataTrace.Application.Configuration;
using DataTrace.Application.Evaluation;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Evaluation;
using DataTrace.Domain.Enums;

namespace DataTrace.Collector;

/// <summary>判定用的限值来源选择结果。<see cref="SessionRecipe.SelectFrozen"/> 的产物。</summary>
public sealed record FrozenSelection(
    /// <summary>溯源/显示用型号对象；Kind=Frozen 且 Kind=None 时可能为 null。</summary>
    Recipe? Recipe,

    /// <summary>冻结的生效限值；完整快照还冻结点位定义，后续新增点位不加入原会话判定。</summary>
    IReadOnlyDictionary<int, TagLimits>? FrozenLimits,

    /// <summary>本次选择的来源类别。</summary>
    SessionRecipe.SelectionKind Kind,

    /// <summary>Kind 为 Corrupted 时的人类可读原因。</summary>
    string? Error,

    /// <summary>首站完整配置；后续站点的采集地址、字段定义与判据都从这里读取。</summary>
    AppConfigurationSnapshot? FrozenConfiguration = null,

    /// <summary>首站时刻匹配型号的曲线基线，null 明确表示当时没有可用基线。</summary>
    CurveBaselineSnapshot? FrozenBaseline = null)
{
    /// <summary>首站：用当前配置判定（生效限值随落库冻结，供后续工站使用）。</summary>
    public static FrozenSelection ForFirstStation(AppConfigurationSnapshot config, CurveBaselineSnapshot? baseline = null)
        => new(config.ActiveRecipe, null, SessionRecipe.SelectionKind.FirstStation, null,
            config, SessionRecipe.FreezeBaseline(config, baseline));
}

/// <summary>
/// 首站记下当时的型号，这件后面的工站都用这一套。
/// 中途换成新型号，只作用于下一件。
/// </summary>
/// <remarks>
/// 冻结的是<b>生效限值表</b>（首站时刻每点位"默认限值 + 型号覆盖"解析后的最终值），
/// 不是覆盖行 —— 只冻覆盖行的话，改点位默认限值、中途换型、型号停用/改名都会让旧件改判。
/// "没选型号"本身也是一种冻结状态（全套默认值的当时值）。
/// </remarks>
public static class SessionRecipe
{
    public enum SelectionKind
    {
        /// <summary>首站：按当前配置判定，同时生成快照。</summary>
        FirstStation,

        /// <summary>后续工站使用冻结的生效限值表判定。</summary>
        Frozen,

        /// <summary>升级前开的旧会话：只有覆盖行快照（v1），按编码回落当前配置（旧行为，明确标注而非掩盖）。</summary>
        LegacyFallback,

        /// <summary>快照存在但解析不了：判定必须阻塞，由人工核查，绝不静默换规则。</summary>
        Corrupted
    }

    /// <summary>
    /// 兼容入口：旧调用点只需要"型号对象"（显示/溯源）。判定限值一律走 <see cref="SelectFrozen"/>。
    /// </summary>
    public static Recipe? Select(AppConfigurationSnapshot config, bool firstStation, string? frozenCode)
    {
        if (firstStation || string.IsNullOrWhiteSpace(frozenCode))
        {
            return config.ActiveRecipe;
        }

        return FindByCode(config, frozenCode);
    }

    /// <summary>
    /// 兼容入口（v1 快照重建的型号对象）：判定限值一律走 <see cref="SelectFrozen"/>。
    /// </summary>
    public static Recipe? Select(AppConfigurationSnapshot config, bool firstStation, string? frozenCode, Recipe? frozenSnapshot)
    {
        if (firstStation || string.IsNullOrWhiteSpace(frozenCode))
        {
            return config.ActiveRecipe;
        }

        if (frozenSnapshot is not null)
        {
            return frozenSnapshot;
        }

        return FindByCode(config, frozenCode);
    }

    public static string RecordCode(bool firstStation, string? frozenCode, Recipe? active)
    {
        if (!firstStation && !string.IsNullOrWhiteSpace(frozenCode))
        {
            return frozenCode.Trim();
        }

        return active?.Code ?? "";
    }

    public static string RecordCodeForSession(string? frozenCode, string? snapshotJson, Recipe? active)
    {
        if (!string.IsNullOrWhiteSpace(snapshotJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(snapshotJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("kind", out var kind)
                    && kind.ValueKind == JsonValueKind.String && kind.GetString() == "None")
                    return "";
            }
            catch (JsonException) { /* caller blocks corrupted snapshots before persisting */ }
        }
        return RecordCode(false, frozenCode, active);
    }

    // ---------- 快照（v4：完整采集/判定/基线/版本；v1：明确的旧覆盖行兼容路径）----------

    private sealed record RecipeSnapshotDtoV4(
        int SchemaVersion,
        int ConfigVersion,
        long? ConfigurationRevisionId,
        string AlgorithmVersion,
        string Kind,
        string Code,
        string Name,
        IReadOnlyList<FrozenLimitDto>? Limits,
        AppConfigurationSnapshot? Configuration,
        FrozenBaselineDto? Baseline);

    private sealed record FrozenBaselineDto(string RecipeCode, DateTime RefreshedAt, IReadOnlyList<FrozenBaselineTemplateDto>? Templates);
    private sealed record FrozenBaselineTemplateDto(int CurveDefinitionId, string SeriesName, CurveTemplate Template);

    private sealed record FrozenLimitDto(
        int TagId,
        double? Lower,
        double? Upper,
        double? WarnLower,
        double? WarnUpper,
        double? Target);

    /// <summary>v1（升级前）快照：只冻结覆盖行，判定回落当前点位默认值。</summary>
    private sealed record RecipeSnapshotDtoV1(
        string Code,
        string Name,
        bool Enabled,
        IReadOnlyList<LimitDto> Limits);

    private sealed record LimitDto(
        int TagId,
        double? LowerLimit,
        double? UpperLimit,
        double? WarningLowerLimit,
        double? WarningUpperLimit,
        double? TargetValue);

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };

    /// <summary>
    /// 首站把完整配置、算法标识、曲线基线和全线启用点位的<b>生效限值</b>固化成 JSON。
    /// "没选型号"存为 Kind=None —— 它也是一套明确的判定依据（全套默认值的当时值），
    /// 不能留成空、让中途选型偷偷改掉旧件的规则。
    /// </summary>
    public static string Snapshot(AppConfigurationSnapshot config, CurveBaselineSnapshot? baseline = null)
    {
        var active = config.ActiveRecipe;
        var dto = new RecipeSnapshotDtoV4(
            SchemaVersion: 4,
            ConfigVersion: config.Version,
            ConfigurationRevisionId: config.ConfigurationRevisionId,
            AlgorithmVersion: "collect-evaluator-v2",
            Kind: active is null ? "None" : "Recipe",
            Code: active?.Code ?? "",
            Name: active?.Name ?? "",
            Limits: config.Stations
                .SelectMany(s => s.Tags)
                .Where(t => t.Enabled)
                .Select(tag =>
                {
                    var limits = RecipeLimitResolver.Resolve(tag, active);
                    return new FrozenLimitDto(tag.Id, limits.Lower, limits.Upper, limits.WarningLower, limits.WarningUpper, limits.Target);
                })
                .ToList(),
            Configuration: config,
            Baseline: FreezeBaseline(config, baseline) is { } frozenBaseline
                ? new FrozenBaselineDto(frozenBaseline.RecipeCode, frozenBaseline.RefreshedAt,
                    frozenBaseline.Templates.Select(x => new FrozenBaselineTemplateDto(x.Key.CurveDefinitionId, x.Key.SeriesName, x.Value)).ToList())
                : null);

        return JsonSerializer.Serialize(dto, SnapshotJsonOptions);
    }

    /// <summary>
    /// 后续工站的判定限值选择。
    /// </summary>
    /// <param name="snapshotJson">在制索引里冻结的快照；null 表示升级前旧会话根本没有这个字段。</param>
    public static FrozenSelection SelectFrozen(
        AppConfigurationSnapshot config,
        bool firstStation,
        string? frozenCode,
        string? snapshotJson)
    {
        if (firstStation)
        {
            return FrozenSelection.ForFirstStation(config);
        }

        // 快照存在就按快照走：frozenCode 为空串不是"没有冻结"，是"首站当时没选型号"的
        // 冻结状态（Kind=None）—— 用编码是否为空做入口判断，会让这类件被当前配置偷偷换规则
        // （复审 F05 探针命中过的实际缺陷）。
        if (string.IsNullOrWhiteSpace(snapshotJson))
        {
            // 旧会话（快照列引入前开的件）：没有冻结依据，只能按旧口径回落当前配置 ——
            // 这是明确标注的兼容路径，不是对损坏的掩盖。
            return new FrozenSelection(
                Recipe: FindByCode(config, frozenCode),
                FrozenLimits: null,
                Kind: SelectionKind.LegacyFallback,
                Error: null);
        }

        try
        {
            using var document = JsonDocument.Parse(snapshotJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Corrupted(frozenCode, "根节点必须是对象");
            var hasKind = document.RootElement.TryGetProperty("kind", out _);

            if (!hasKind)
            {
                // v1 覆盖行快照（升级前的会话）：冻结行叠当前默认值，旧行为原样保留。
                var legacy = JsonSerializer.Deserialize<RecipeSnapshotDtoV1>(snapshotJson, SnapshotJsonOptions);
                if (legacy is null)
                {
                    return Corrupted(frozenCode, "快照为空");
                }

                return new FrozenSelection(
                    Recipe: FindByCode(config, legacy.Code) ?? FindByCode(config, frozenCode),
                    FrozenLimits: null,
                    Kind: SelectionKind.LegacyFallback,
                    Error: null);
            }

            var dto = document.RootElement.Deserialize<RecipeSnapshotDtoV4>(SnapshotJsonOptions);
            if (dto is null || dto.SchemaVersion != 4)
            {
                return Corrupted(frozenCode, "schemaVersion 缺失或不支持");
            }
            if (!string.Equals(dto.AlgorithmVersion, "collect-evaluator-v2", StringComparison.Ordinal))
                return Corrupted(frozenCode, "判定算法版本缺失或不支持");

            if (dto.Kind is not ("None" or "Recipe"))
                return Corrupted(frozenCode, $"未知 kind「{dto.Kind}」");
            if (dto.Kind == "None" && (!string.IsNullOrEmpty(dto.Code) || !string.IsNullOrEmpty(dto.Name)))
                return Corrupted(frozenCode, "None 型号快照包含型号身份");
            if (dto.Kind == "Recipe" && (string.IsNullOrWhiteSpace(dto.Code) || string.IsNullOrWhiteSpace(dto.Name)))
                return Corrupted(frozenCode, "Recipe 型号快照缺少编码或名称");
            if (dto.Limits is null)
                return Corrupted(frozenCode, "limits 必须是数组");
            if (dto.Limits.Any(x => x is null || x.TagId <= 0))
                return Corrupted(frozenCode, "限值项为空或 TagId 非法");
            if (dto.Limits.Select(x => x.TagId).Distinct().Count() != dto.Limits.Count)
                return Corrupted(frozenCode, "限值表包含重复 TagId");
            foreach (var limit in dto.Limits)
            {
                if (!ValidLimit(limit.Lower) || !ValidLimit(limit.Upper) || !ValidLimit(limit.WarnLower)
                    || !ValidLimit(limit.WarnUpper) || !ValidLimit(limit.Target)
                    || (limit.Lower is not null && limit.Upper is not null && limit.Lower > limit.Upper)
                    || (limit.WarnLower is not null && limit.WarnUpper is not null && limit.WarnLower > limit.WarnUpper))
                    return Corrupted(frozenCode, $"TagId {limit.TagId} 的限值无效");
            }
            if (dto.Configuration is null || dto.Configuration.Version != dto.ConfigVersion
                || dto.Configuration.ConfigurationRevisionId != dto.ConfigurationRevisionId)
                return Corrupted(frozenCode, "完整配置修订缺失或版本不一致");
            if (!ValidateFrozenConfiguration(dto.Configuration, dto.Kind, dto.Code))
                return Corrupted(frozenCode, "完整配置修订结构无效");
            var requiredTagIds = dto.Configuration.Stations.SelectMany(s => s.Tags).Where(t => t.Enabled).Select(t => t.Id).ToHashSet();
            if (requiredTagIds.Any(id => !dto.Limits.Any(limit => limit.TagId == id)))
                return Corrupted(frozenCode, "快照缺少当时启用点位的限值");
            if (dto.Limits.Any(limit => !requiredTagIds.Contains(limit.TagId)))
                return Corrupted(frozenCode, "限值表包含完整配置之外的点位");
            var baseline = ThawBaseline(dto.Baseline);
            if (dto.Baseline is not null && baseline is null)
                return Corrupted(frozenCode, "冻结曲线基线结构无效");
            if (baseline is not null && !string.Equals(baseline.RecipeCode, dto.Code, StringComparison.Ordinal))
                return Corrupted(frozenCode, "冻结曲线基线型号与判定快照不一致");
            if (baseline is not null)
            {
                var validCurveSeries = dto.Configuration.Stations.SelectMany(s => s.Curves)
                    .SelectMany(curve => curve.Series.Select(series => new CurveBaselineKey(curve.Id, series.Name)))
                    .ToHashSet();
                if (baseline.Templates.Keys.Any(key => !validCurveSeries.Contains(key)))
                    return Corrupted(frozenCode, "冻结曲线基线引用配置中不存在的曲线序列");
            }

            var frozenLimits = dto.Limits.ToDictionary(
                limit => limit.TagId,
                limit => new TagLimits(limit.Lower, limit.Upper, limit.WarnLower, limit.WarnUpper, limit.Target));

            return new FrozenSelection(
                Recipe: dto.Kind == "None" ? null : dto.Configuration.ActiveRecipe is { } active
                    ? active
                    : new Recipe { Code = dto.Code, Name = dto.Name, Enabled = true },
                FrozenLimits: frozenLimits,
                Kind: SelectionKind.Frozen,
                Error: null,
                FrozenConfiguration: dto.Configuration,
                FrozenBaseline: baseline);
        }
        catch (JsonException ex)
        {
            return Corrupted(frozenCode, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException or OverflowException)
        {
            return Corrupted(frozenCode, ex.Message);
        }
    }

    private static bool ValidLimit(double? value) => value is null || double.IsFinite(value.Value);

    private static bool ValidateFrozenConfiguration(AppConfigurationSnapshot config, string kind, string code)
    {
        if (config.Settings is null || config.PlcConnections is null || config.Stations is null || config.Recipes is null
            || config.PlcConnections.Any(x => x is null || x.Id <= 0 || !Enum.IsDefined(typeof(PlcBrand), x.Brand))
            || config.Stations.Any(x => x is null || x.Id <= 0 || x.Tags is null || x.Curves is null)
            || config.Stations.Select(x => x.Id).Distinct().Count() != config.Stations.Count
            || config.PlcConnections.Select(x => x.Id).Distinct().Count() != config.PlcConnections.Count
            || config.Recipes.Any(x => x is null || string.IsNullOrWhiteSpace(x.Code))
            || config.Recipes.Select(x => x.Code).Distinct(StringComparer.Ordinal).Count() != config.Recipes.Count)
            return false;
        if (kind == "None")
            return config.ActiveRecipe is null;
        return config.ActiveRecipe is { Enabled: true } active
            && string.Equals(active.Code, code, StringComparison.Ordinal)
            && config.Recipes.Any(recipe => recipe.Id == active.Id && recipe.Code == active.Code);
    }

    internal static CurveBaselineSnapshot? FreezeBaseline(AppConfigurationSnapshot config, CurveBaselineSnapshot? baseline)
    {
        var recipeCode = config.ActiveRecipe?.Code ?? "";
        if (baseline is null || !baseline.IsFresh(DateTime.Now)
            || !string.Equals(baseline.RecipeCode, recipeCode, StringComparison.Ordinal)) return null;
        return baseline;
    }

    private static CurveBaselineSnapshot? ThawBaseline(FrozenBaselineDto? baseline)
    {
        if (baseline is null || baseline.Templates is null
            || baseline.Templates.Any(x => x is null || x.CurveDefinitionId <= 0 || string.IsNullOrWhiteSpace(x.SeriesName) || x.Template is null)
            || baseline.Templates.Select(x => new CurveBaselineKey(x.CurveDefinitionId, x.SeriesName)).Distinct().Count() != baseline.Templates.Count)
            return null;
        return new CurveBaselineSnapshot
        {
            RecipeCode = baseline.RecipeCode,
            RefreshedAt = baseline.RefreshedAt,
            Templates = baseline.Templates.ToDictionary(
                x => new CurveBaselineKey(x.CurveDefinitionId, x.SeriesName), x => x.Template)
        };
    }

    private static FrozenSelection Corrupted(string? frozenCode, string reason)
        => new(Recipe: null, FrozenLimits: null, Kind: SelectionKind.Corrupted,
            Error: $"在制配方快照损坏（{reason}），判定已阻塞：请人工核查在制索引的 RecipeSnapshot 后重试");

    private static Recipe? FindByCode(AppConfigurationSnapshot config, string? code)
        => config.Recipes.FirstOrDefault(recipe =>
            string.Equals(recipe.Code, code?.Trim(), StringComparison.Ordinal));
}
