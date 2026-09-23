using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Simulator;

namespace DataTrace.Plc.Factory;

public sealed class PlcDriverFactory : IPlcDriverFactory
{
    private readonly SimulatorCatalog _simulators;
    private readonly IPhysicalPlcDriverFactory[] _physical;

    public PlcDriverFactory(SimulatorCatalog simulators, IEnumerable<IPhysicalPlcDriverFactory> physical)
    {
        _simulators = simulators;
        _physical = physical.ToArray();
    }

    public IPlcDriver Create(PlcConnection connection)
    {
        if (connection.Brand == PlcBrand.Simulator)
        {
            return _simulators.Get(connection.Id);
        }

        var factory = _physical.FirstOrDefault(x => x.CanCreate(connection.Brand));
        if (factory is null)
        {
            throw new NotSupportedException($"未注册品牌 {connection.Brand} 的驱动。请引用 DataTrace.Plc.Drivers.IoTClient。");
        }

        return factory.Create(connection);
    }
}
