using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Simulator;

namespace DataTrace.Plc.Abstractions;

public interface IPhysicalPlcDriverFactory
{
    bool CanCreate(PlcBrand brand);
    IPlcDriver Create(PlcConnection connection);
}
