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
    private readonly ICollectArchiveStore _archives;
    private readonly ILogger<RetentionHostedService> _logger;

    public RetentionHostedService(
        IServiceScopeFactory scopeFactory,
        RuntimeDbFactory factory,
        ICurveFileStore curves,
        ICollectArchiveStore archives,
        ILogger<RetentionHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _factory = factory;
        _curves = curves;
        _archives = archives;
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
                var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();

                foreach (var month in _factory.ListMonthKeys())
                {
                    if (string.CompareOrdinal(month, cutoffKey) < 0)
                    {
                        try
                        {
                            await runtime.DeleteMonthAsync(month, stoppingToken).ConfigureAwait(false);
                            if (month.Length == 6)
                            {
                                await _curves.DeleteMonthAsync(month[..4], month[4..], stoppingToken).ConfigureAwait(false);
                                // 归档与曲线同样是"记录之外的大对象"：记录被清理后它们再无引用方，
                                // 不一起删就会在数据盘上无声堆积。
                                await _archives.DeleteMonthAsync(month[..4], month[4..], stoppingToken).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            try
                            {
                                await audit.WriteAsync(
                                    "system",
                                    "DeleteRuntimeMonth",
                                    "RuntimeData",
                                    month,
                                    null,
                                    $"failure; {ex.Message}",
                                    stoppingToken,
                                    outcome: "Failure",
                                    source: "Runtime retention service",
                                    correlationId: Guid.NewGuid().ToString("N")).ConfigureAwait(false);
                            }
                            catch (Exception auditException)
                            {
                                _logger.LogError(auditException, "运行数据月份 {Month} 清理失败，且失败审计写入失败", month);
                            }

                            throw;
                        }

                        _logger.LogInformation("已按保留策略删除月份库 {Month}", month);
                        try
                        {
                            await audit.WriteAsync(
                                "system",
                                "DeleteRuntimeMonth",
                                "RuntimeData",
                                month,
                                null,
                                "runtime month, curves, and raw archive deleted",
                                stoppingToken,
                                source: "Runtime retention service",
                                correlationId: Guid.NewGuid().ToString("N")).ConfigureAwait(false);
                        }
                        catch (Exception auditException)
                        {
                            _logger.LogError(auditException, "运行数据月份 {Month} 已清理，但审计记录写入失败", month);
                        }
                    }
                }

                if (snapshot.Settings.AuditRetentionYears > 0)
                {
                    var removed = await scope.ServiceProvider
                        .GetRequiredService<AuditRetentionArchiveService>()
                        .ArchiveAndPurgeAsync(snapshot.Settings.AuditRetentionYears, stoppingToken)
                        .ConfigureAwait(false);
                    if (removed > 0)
                    {
                        _logger.LogInformation("审计日志归档清理完成，共清理 {Count} 条", removed);
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
