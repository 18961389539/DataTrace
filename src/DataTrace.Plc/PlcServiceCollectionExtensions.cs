using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Factory;
using DataTrace.Plc.Simulator;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Plc;

public static class PlcServiceCollectionExtensions
{
    public static IServiceCollection AddDataTracePlc(this IServiceCollection services)
    {
        services.AddSingleton<SimulatorCatalog>();
        services.AddSingleton<IPlcDriverFactory, PlcDriverFactory>();
        return services;
    }
}
