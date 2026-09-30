using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Application.Configuration;

public readonly record struct SavedRecipe(int Id, string Code, string Name);

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
