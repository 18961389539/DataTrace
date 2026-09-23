using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Entities;

public class CurveSeries
{
    public int Id { get; set; }
    public int CurveDefinitionId { get; set; }
    public CurveDefinition? CurveDefinition { get; set; }

    public string Name { get; set; } = "";
    public SeriesRole Role { get; set; }
    public string StartAddress { get; set; } = "";
    public PlcDataType DataType { get; set; } = PlcDataType.Float;
    public int StrideWords { get; set; } = 2;
    public double Scale { get; set; } = 1;
    public double Offset { get; set; }
    public string? Unit { get; set; }
}
