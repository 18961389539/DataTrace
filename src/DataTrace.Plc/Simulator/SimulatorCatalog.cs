using System.Collections.Concurrent;
using DataTrace.Plc.Simulator;

namespace DataTrace.Plc.Simulator;

public sealed class SimulatorCatalog
{
    private readonly ConcurrentDictionary<int, InMemoryPlcDriver> _drivers = new();

    public InMemoryPlcDriver Get(int connectionId) =>
        _drivers.GetOrAdd(connectionId, _ => new InMemoryPlcDriver());

    public bool TryGet(int connectionId, out InMemoryPlcDriver driver) =>
        _drivers.TryGetValue(connectionId, out driver!);

    public IReadOnlyCollection<InMemoryPlcDriver> All => _drivers.Values.ToList();
}
