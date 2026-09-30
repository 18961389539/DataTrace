using DataTrace.Application.Realtime;
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
        // 运行时观测：采集循环写、诊断页读。单例，两边拿到的是同一份。
        services.AddSingleton<CollectorDiagnostics>();
        services.AddSingleton<ICollectorDiagnostics>(sp => sp.GetRequiredService<CollectorDiagnostics>());
        // 工站试读：只读读一次该工站的数据，复用采集队列与同一套读取/解码件。
        services.AddSingleton<IPlcQueueAccess>(sp => sp.GetRequiredService<CollectionHostedService>());
        services.AddSingleton<IStationTrialReader, StationTrialReader>();
        // 补传重放：后台循环与手动补传必须共用同一个执行器，否则同一条缓存会被并发写两遍。
        services.AddSingleton<SpoolReplayRunner>();
        services.AddHostedService<SpoolReplayService>();
        services.AddHostedService<MesOutboxProcessor>();
        services.AddHostedService<RetentionHostedService>();
        services.AddHostedService<CurveBaselineRefresher>();
        return services;
    }
}
