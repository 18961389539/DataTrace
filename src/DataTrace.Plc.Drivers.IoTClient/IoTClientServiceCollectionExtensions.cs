using DataTrace.Plc.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Plc.Drivers.IoTClient;

public static class IoTClientServiceCollectionExtensions
{
    public static IServiceCollection AddIoTClientDrivers(this IServiceCollection services)
    {
        services.AddSingleton<IPhysicalPlcDriverFactory, IoTClientDriverFactory>();
        return services;
    }
}
