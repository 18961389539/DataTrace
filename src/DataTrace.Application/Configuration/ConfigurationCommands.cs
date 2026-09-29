using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Application.Configuration;

public readonly record struct SavedRecipe(int Id, string Code, string Name);
public sealed record SavePlcConnectionCommand(
    int Id,
    string Name,
    PlcBrand Brand,
    string Host,
    int Port,
    int TimeoutMs,
    FloatWordOrder FloatWordOrder,
    bool StringHighByteFirst,
    int MergeGapWords,
    bool Enabled,
    string? Extra,
    SaveHeartbeatCommand? Heartbeat)
{
    public static SavePlcConnectionCommand From(PlcConnection connection) => new(
        connection.Id,
        connection.Name,
        connection.Brand,
        connection.Host,
        connection.Port,
        connection.TimeoutMs,
        connection.FloatWordOrder,
        connection.StringHighByteFirst,
        connection.MergeGapWords,
        connection.Enabled,
        connection.Extra,
        connection.Heartbeat is null ? null : SaveHeartbeatCommand.From(connection.Heartbeat));

    public PlcConnection ToEntity()
    {
        var connection = new PlcConnection
        {
            Id = Id,
            Name = Name,
            Brand = Brand,
            Host = Host,
            Port = Port,
            TimeoutMs = TimeoutMs,
            FloatWordOrder = FloatWordOrder,
            StringHighByteFirst = StringHighByteFirst,
            MergeGapWords = MergeGapWords,
            Enabled = Enabled,
            Extra = Extra,
            Heartbeat = Heartbeat?.ToEntity()
        };
        if (connection.Heartbeat is not null)
        {
            connection.Heartbeat.PlcConnectionId = Id;
        }

        return connection;
    }
}

public sealed record SaveHeartbeatCommand(
    int Id,
    int PlcConnectionId,
    string Address,
    int IntervalMs,
    HeartbeatMode Mode,
    bool Enabled)
{
    public static SaveHeartbeatCommand From(HeartbeatSettings heartbeat) => new(
        heartbeat.Id,
        heartbeat.PlcConnectionId,
        heartbeat.Address,
        heartbeat.IntervalMs,
        heartbeat.Mode,
        heartbeat.Enabled);

    public HeartbeatSettings ToEntity() => new()
    {
        Id = Id,
        PlcConnectionId = PlcConnectionId,
        Address = Address,
        IntervalMs = IntervalMs,
        Mode = Mode,
        Enabled = Enabled
    };
}

public sealed record SavePositionCommand(int Id, int Index, string Name, string? OccupiedAddress)
{
    public static SavePositionCommand From(ProductPositionDefinition position) => new(
        position.Id,
        position.Index,
        position.Name,
        position.OccupiedAddress);

    public ProductPositionDefinition ToEntity() => new()
    {
        Id = Id,
        Index = Index,
        Name = Name,
        OccupiedAddress = OccupiedAddress
    };
}

public sealed record SaveStationCommand(
    int Id,
    int PlcConnectionId,
    string Code,
    string Name,
    int Sequence,
    bool IsFirstStation,
    bool IsLastStation,
    string TriggerAddress,
    short TriggerValue,
    string PalletCodeAddress,
    int PalletCodeLength,
    PlcDataType PalletCodeDataType,
    int PositionCount,
    bool Enabled,
    string DataFilePath,
    DataFileFormat DataFileFormat,
    IReadOnlyList<SavePositionCommand> Positions)
{
    public static SaveStationCommand From(Station station) => new(
        station.Id,
        station.PlcConnectionId,
        station.Code,
        station.Name,
        station.Sequence,
        station.IsFirstStation,
        station.IsLastStation,
        station.TriggerAddress,
        station.TriggerValue,
        station.PalletCodeAddress,
        station.PalletCodeLength,
        station.PalletCodeDataType,
        station.PositionCount,
        station.Enabled,
        station.DataFilePath,
        station.DataFileFormat,
        station.Positions.Select(SavePositionCommand.From).ToList());

    public Station ToEntity() => new()
    {
        Id = Id,
        PlcConnectionId = PlcConnectionId,
        Code = Code,
        Name = Name,
        Sequence = Sequence,
        IsFirstStation = IsFirstStation,
        IsLastStation = IsLastStation,
        TriggerAddress = TriggerAddress,
        TriggerValue = TriggerValue,
        PalletCodeAddress = PalletCodeAddress,
        PalletCodeLength = PalletCodeLength,
        PalletCodeDataType = PalletCodeDataType,
        PositionCount = PositionCount,
        Enabled = Enabled,
        DataFilePath = DataFilePath,
        DataFileFormat = DataFileFormat,
        Positions = Positions.Select(x => x.ToEntity()).ToList()
    };
}

public sealed record SaveTagCommand(
    int Id,
    int StationId,
    string Name,
    string Address,
    PlcDataType DataType,
    int Length,
    double Scale,
    double Offset,
    string? Unit,
    double? LowerLimit,
    double? UpperLimit,
    double? WarningLowerLimit,
    double? WarningUpperLimit,
    double? TargetValue,
    bool IsRequired,
    int PositionIndex,
    bool Enabled,
    TagDataSource Source,
    int? SpcRuleMask,
    double? ControlCenterLine,
    double? ControlUpperLimit,
    double? ControlLowerLimit,
    int? ControlSampleCount,
    DateTime? ControlCapturedAt,
    string? ControlCapturedBy)
{
    public static SaveTagCommand From(TagDefinition tag) => new(
        tag.Id,
        tag.StationId,
        tag.Name,
        tag.Address,
        tag.DataType,
        tag.Length,
        tag.Scale,
        tag.Offset,
        tag.Unit,
        tag.LowerLimit,
        tag.UpperLimit,
        tag.WarningLowerLimit,
        tag.WarningUpperLimit,
        tag.TargetValue,
        tag.IsRequired,
        tag.PositionIndex,
        tag.Enabled,
        tag.Source,
        tag.SpcRuleMask,
        tag.ControlCenterLine,
        tag.ControlUpperLimit,
        tag.ControlLowerLimit,
        tag.ControlSampleCount,
        tag.ControlCapturedAt,
        tag.ControlCapturedBy);

    public TagDefinition ToEntity() => new()
    {
        Id = Id,
        StationId = StationId,
        Name = Name,
        Address = Address,
        DataType = DataType,
        Length = Length,
        Scale = Scale,
        Offset = Offset,
        Unit = Unit,
        LowerLimit = LowerLimit,
        UpperLimit = UpperLimit,
        WarningLowerLimit = WarningLowerLimit,
        WarningUpperLimit = WarningUpperLimit,
        TargetValue = TargetValue,
        IsRequired = IsRequired,
        PositionIndex = PositionIndex,
        Enabled = Enabled,
        Source = Source,
        SpcRuleMask = SpcRuleMask,
        ControlCenterLine = ControlCenterLine,
        ControlUpperLimit = ControlUpperLimit,
        ControlLowerLimit = ControlLowerLimit,
        ControlSampleCount = ControlSampleCount,
        ControlCapturedAt = ControlCapturedAt,
        ControlCapturedBy = ControlCapturedBy
    };
}

public sealed record SaveCurveSeriesCommand(
    int Id,
    string Name,
    SeriesRole Role,
    string StartAddress,
    PlcDataType DataType,
    int StrideWords,
    double Scale,
    double Offset,
    string? Unit)
{
    public static SaveCurveSeriesCommand From(CurveSeries series) => new(
        series.Id,
        series.Name,
        series.Role,
        series.StartAddress,
        series.DataType,
        series.StrideWords,
        series.Scale,
        series.Offset,
        series.Unit);

    public CurveSeries ToEntity() => new()
    {
        Id = Id,
        Name = Name,
        Role = Role,
        StartAddress = StartAddress,
        DataType = DataType,
        StrideWords = StrideWords,
        Scale = Scale,
        Offset = Offset,
        Unit = Unit
    };
}

public sealed record SaveCurveCommand(
    int Id,
    int StationId,
    string Code,
    string Name,
    int PointCount,
    int PositionIndex,
    bool Enabled,
    IReadOnlyList<SaveCurveSeriesCommand> Series)
{
    public static SaveCurveCommand From(CurveDefinition curve) => new(
        curve.Id,
        curve.StationId,
        curve.Code,
        curve.Name,
        curve.PointCount,
        curve.PositionIndex,
        curve.Enabled,
        curve.Series.Select(SaveCurveSeriesCommand.From).ToList());

    public CurveDefinition ToEntity() => new()
    {
        Id = Id,
        StationId = StationId,
        Code = Code,
        Name = Name,
        PointCount = PointCount,
        PositionIndex = PositionIndex,
        Enabled = Enabled,
        Series = Series.Select(x => x.ToEntity()).ToList()
    };
}

public sealed record SaveCurveCriterionCommand(
    int Id,
    string SeriesName,
    bool Enabled,
    double? PeakMin,
    double? PeakMax,
    double? MeanMin,
    double? MeanMax,
    double? AreaMin,
    double? AreaMax,
    double? RiseSlopeMin,
    double? RiseSlopeMax,
    double? HoldSlopeMin,
    double? HoldSlopeMax,
    double? FallRatioMax,
    double? MaxStepMax,
    double? StdDevMax,
    int? OscillationMax)
{
    public static SaveCurveCriterionCommand From(CurveCriterion criterion) => new(
        criterion.Id,
        criterion.SeriesName,
        criterion.Enabled,
        criterion.PeakMin,
        criterion.PeakMax,
        criterion.MeanMin,
        criterion.MeanMax,
        criterion.AreaMin,
        criterion.AreaMax,
        criterion.RiseSlopeMin,
        criterion.RiseSlopeMax,
        criterion.HoldSlopeMin,
        criterion.HoldSlopeMax,
        criterion.FallRatioMax,
        criterion.MaxStepMax,
        criterion.StdDevMax,
        criterion.OscillationMax);

    public CurveCriterion ToEntity() => new()
    {
        Id = Id,
        SeriesName = SeriesName,
        Enabled = Enabled,
        PeakMin = PeakMin,
        PeakMax = PeakMax,
        MeanMin = MeanMin,
        MeanMax = MeanMax,
        AreaMin = AreaMin,
        AreaMax = AreaMax,
        RiseSlopeMin = RiseSlopeMin,
        RiseSlopeMax = RiseSlopeMax,
        HoldSlopeMin = HoldSlopeMin,
        HoldSlopeMax = HoldSlopeMax,
        FallRatioMax = FallRatioMax,
        MaxStepMax = MaxStepMax,
        StdDevMax = StdDevMax,
        OscillationMax = OscillationMax
    };
}

public sealed record SaveRecipeLimitCommand(
    int Id,
    int TagId,
    double? LowerLimit,
    double? UpperLimit,
    double? WarningLowerLimit,
    double? WarningUpperLimit,
    double? TargetValue)
{
    public static SaveRecipeLimitCommand From(RecipeLimit limit) => new(
        limit.Id,
        limit.TagId,
        limit.LowerLimit,
        limit.UpperLimit,
        limit.WarningLowerLimit,
        limit.WarningUpperLimit,
        limit.TargetValue);

    public RecipeLimit ToEntity() => new()
    {
        Id = Id,
        TagId = TagId,
        LowerLimit = LowerLimit,
        UpperLimit = UpperLimit,
        WarningLowerLimit = WarningLowerLimit,
        WarningUpperLimit = WarningUpperLimit,
        TargetValue = TargetValue
    };
}

public sealed record SaveRecipeCommand(
    int Id,
    string Code,
    string Name,
    bool Enabled,
    string? Remark,
    string? PreviousCodes,
    IReadOnlyList<SaveRecipeLimitCommand> Limits)
{
    public static SaveRecipeCommand From(Recipe recipe) => new(
        recipe.Id,
        recipe.Code,
        recipe.Name,
        recipe.Enabled,
        recipe.Remark,
        recipe.PreviousCodes,
        recipe.Limits.Select(SaveRecipeLimitCommand.From).ToList());

    public Recipe ToEntity() => new()
    {
        Id = Id,
        Code = Code,
        Name = Name,
        Enabled = Enabled,
        Remark = Remark,
        PreviousCodes = PreviousCodes,
        Limits = Limits.Select(x => x.ToEntity()).ToList()
    };
}

public sealed record SaveSettingsCommand(
    int Id,
    int ScanIntervalMs,
    int WriteRetryCount,
    int WriteRetryDelayMs,
    int RetentionYears,
    int AuditRetentionYears,
    string CurveRootPath,
    string SpoolPath,
    string RuntimeDbPath,
    bool CollectEnabled,
    bool MesEnabled,
    string? MesEndpoint,
    int MesTimeoutSeconds,
    bool SimulatorAutoRun,
    int SimulatorIntervalMs,
    int SimulatorNgPercent,
    int SimulatorPalletPool,
    int? ActiveRecipeId,
    string? AlarmWebhookUrl)
{
    public static SaveSettingsCommand From(SystemSettings settings) => new(
        settings.Id,
        settings.ScanIntervalMs,
        settings.WriteRetryCount,
        settings.WriteRetryDelayMs,
        settings.RetentionYears,
        settings.AuditRetentionYears,
        settings.CurveRootPath,
        settings.SpoolPath,
        settings.RuntimeDbPath,
        settings.CollectEnabled,
        settings.MesEnabled,
        settings.MesEndpoint,
        settings.MesTimeoutSeconds,
        settings.SimulatorAutoRun,
        settings.SimulatorIntervalMs,
        settings.SimulatorNgPercent,
        settings.SimulatorPalletPool,
        settings.ActiveRecipeId,
        settings.AlarmWebhookUrl);

    public SystemSettings ToEntity() => new()
    {
        Id = Id,
        ScanIntervalMs = ScanIntervalMs,
        WriteRetryCount = WriteRetryCount,
        WriteRetryDelayMs = WriteRetryDelayMs,
        RetentionYears = RetentionYears,
        AuditRetentionYears = AuditRetentionYears,
        CurveRootPath = CurveRootPath,
        SpoolPath = SpoolPath,
        RuntimeDbPath = RuntimeDbPath,
        CollectEnabled = CollectEnabled,
        MesEnabled = MesEnabled,
        MesEndpoint = MesEndpoint,
        MesTimeoutSeconds = MesTimeoutSeconds,
        SimulatorAutoRun = SimulatorAutoRun,
        SimulatorIntervalMs = SimulatorIntervalMs,
        SimulatorNgPercent = SimulatorNgPercent,
        SimulatorPalletPool = SimulatorPalletPool,
        ActiveRecipeId = ActiveRecipeId,
        AlarmWebhookUrl = AlarmWebhookUrl
    };
}

/// <summary>
/// 实体重载：测试和仍拿着编辑模型的调用方可以继续传实体，仓储只接收命令。
/// </summary>
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
