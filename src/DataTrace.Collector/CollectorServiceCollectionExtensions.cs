using DataTrace.Plc;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Collector;

public static class CollectorServiceCollectionExtensions
{
    public static IServiceCollection AddDataTraceCollector(this IServiceCollection services)
    {
        services.AddSingleton<StationCollectPipeline>();
        services.AddScoped<CurveBaselineFactory>();
        services.AddSingleton<CollectionHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<CollectionHostedService>());
        services.AddSingleton<LineSimulatorHostedService>();
        services.AddSingleton<ILineSimulator>(sp => sp.GetRequiredService<LineSimulatorHostedService>());
        services.AddHostedService(sp => sp.GetRequiredService<LineSimulatorHostedService>());
        services.AddHostedService<SpoolReplayService>();
        services.AddHostedService<MesOutboxProcessor>();
        services.AddHostedService<RetentionHostedService>();
        services.AddHostedService<CurveBaselineRefresher>();
        return services;
    }
}
