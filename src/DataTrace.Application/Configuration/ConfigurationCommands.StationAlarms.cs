using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Application.Configuration;

public sealed record SaveStationAlarmCommand(
    int Id,
    int StationId,
    string Name,
    bool Enabled,
    int? TagDefinitionId,
    StationAlarmCompare Compare,
    double? Limit,
    int SampleIntervalMs,
    string? Remark)
{
    public static SaveStationAlarmCommand From(StationAlarmDefinition alarm) => new(
        alarm.Id,
        alarm.StationId,
        alarm.Name,
        alarm.Enabled,
        alarm.TagDefinitionId,
        alarm.Compare,
        alarm.Limit,
        alarm.SampleIntervalMs,
        alarm.Remark);

    public StationAlarmDefinition ToEntity() => new()
    {
        Id = Id,
        StationId = StationId,
        Name = Name,
        Enabled = Enabled,
        TagDefinitionId = TagDefinitionId,
        Compare = Compare,
        Limit = Limit,
        SampleIntervalMs = SampleIntervalMs,
        Remark = Remark
    };
}