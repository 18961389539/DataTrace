using DataTrace.Application.Configuration;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Constants;
using DataTrace.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataTrace.Collector;

public sealed class RetentionHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RuntimeDbFactory _factory;
    private readonly ICurveFileStore _curves;
    private readonly ILogger<RetentionHostedService> _logger;

    public RetentionHostedService(
        IServiceScopeFactory scopeFactory,
        RuntimeDbFactory factory,
        ICurveFileStore curves,
        ILogger<RetentionHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _factory = factory;
        _curves = curves;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var snapshot = await scope.ServiceProvider.GetRequiredService<IConfigRepository>()
                    .GetSnapshotAsync(stoppingToken).ConfigureAwait(false);
                var years = Math.Max(1, snapshot.Settings.RetentionYears);
                var cutoff = DateTime.Now.AddYears(-years);
                var cutoffKey = cutoff.ToString("yyyyMM");
                var runtime = scope.ServiceProvider.GetRequiredService<IRuntimeStore>();

                foreach (var month in _factory.ListMonthKeys())
                {
                    if (string.CompareOrdinal(month, cutoffKey) < 0)
                    {
                        await runtime.DeleteMonthAsync(month, stoppingToken).ConfigureAwait(false);
                        if (month.Length == 6)
                        {
                            await _curves.DeleteMonthAsync(month[..4], month[4..], stoppingToken).ConfigureAwait(false);
                        }

                        _logger.LogInformation("已按保留策略删除月份库 {Month}", month);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数据保留任务异常");
            }

            await Task.Delay(TimeSpan.FromHours(SystemDefaults.RetentionCheckHours), stoppingToken).ConfigureAwait(false);
        }
    }
}
