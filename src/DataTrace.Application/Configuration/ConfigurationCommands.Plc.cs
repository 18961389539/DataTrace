using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Application.Configuration;

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
