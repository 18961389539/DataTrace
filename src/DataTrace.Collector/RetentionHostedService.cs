using DataTrace.Application.Configuration;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataTrace.Collector;

public sealed class RetentionHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICurveFileStore _curves;
    private readonly ICollectArchiveStore _archives;
    private readonly IDataMaintenanceBarrier _maintenance;
    private readonly ILogger<RetentionHostedService> _logger;

    public RetentionHostedService(
        IServiceScopeFactory scopeFactory,
        ICurveFileStore curves,
        ICollectArchiveStore archives,
        IDataMaintenanceBarrier maintenance,
        ILogger<RetentionHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _curves = curves;
        _archives = archives;
        _maintenance = maintenance;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var configuration = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
                var snapshot = await configuration.GetSnapshotAsync(stoppingToken).ConfigureAwait(false);
                var years = Math.Max(1, snapshot.Settings.RetentionYears);
                var cutoff = DateTime.Now.AddYears(-years);
                var cutoffKey = cutoff.ToString("yyyyMM");
                var runtime = scope.ServiceProvider.GetRequiredService<IRuntimeRetention>();
                var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
                var activeSessions = scope.ServiceProvider.GetRequiredService<IActiveSessionStore>();
                var spool = scope.ServiceProvider.GetRequiredService<ISpoolStore>();

                foreach (var month in runtime.ListMonthKeys())
                {
                    if (string.CompareOrdinal(month, cutoffKey) < 0)
                    {
                        var retainedCurveReferences = 0;
                        var retainedArchiveReferences = 0;
                        var retainedSpoolFileMonths = 0;
                        try
                        {
                            await using var maintenanceLease = await _maintenance.EnterWriteAsync(stoppingToken).ConfigureAwait(false);
                            var blockers = (await runtime.GetDeletionBlockersAsync(month, cutoff, stoppingToken).ConfigureAwait(false)).ToList();
                            if ((await activeSessions.ListAsync(stoppingToken).ConfigureAwait(false)).Any(x => x.MonthKey == month))
                            {
                                blockers.Add("active session index still references this month");
                            }

                            if (await spool.HasMonthEntriesAsync(month, stoppingToken).ConfigureAwait(false))
                            {
                                blockers.Add("spool entry or unreadable spool file may reference this month");
                            }

                            if (await configuration.HasPendingMesOutboxForMonthAsync(month, stoppingToken).ConfigureAwait(false))
                            {
                                blockers.Add("pending MES delivery still references this month");
                            }

                            if (blockers.Count > 0)
                            {
                                var detail = string.Join("; ", blockers.Distinct(StringComparer.Ordinal));
                                await audit.WriteAsync(
                                    "system", "RetainRuntimeMonth", "RuntimeData", month,
                                    null, detail, stoppingToken, outcome: "Blocked", source: "Runtime retention service",
                                    correlationId: Guid.NewGuid().ToString("N")).ConfigureAwait(false);
                                _logger.LogWarning("保留月份库 {Month}：仍有数据或外部引用 {Blockers}", month, detail);
                                continue;
                            }

                            var referencedFileMonths = await runtime.GetExternalFileMonthKeysAsync(month, stoppingToken)
                                .ConfigureAwait(false);
                            if (month.Length == 6 && month.All(char.IsAsciiDigit))
                                referencedFileMonths = referencedFileMonths.Append(month).Distinct(StringComparer.Ordinal).ToList();
                            await runtime.DeleteMonthAsync(month, stoppingToken).ConfigureAwait(false);
                            foreach (var fileMonth in referencedFileMonths)
                            {
                                // 文件目录按采集触发月分区，而运行库按会话起始月分区：另一
                                // 个保留月库的跨月记录可能仍引用本目录，必须先做全库引用核验。
                                if (await spool.HasMonthEntriesAsync(fileMonth, stoppingToken).ConfigureAwait(false))
                                {
                                    retainedSpoolFileMonths++;
                                    _logger.LogWarning(
                                        "已清理运行库 {Month}，保留仍被 spool 引用的外置文件目录 {FileMonth}",
                                        month, fileMonth);
                                    continue;
                                }
                                var externalReferences = await runtime.GetExternalFileReferenceCountsAsync(fileMonth, month, stoppingToken)
                                    .ConfigureAwait(false);
                                retainedCurveReferences += externalReferences.Curves;
                                retainedArchiveReferences += externalReferences.Archives;
                                if (externalReferences.Curves == 0)
                                    await _curves.DeleteMonthAsync(fileMonth[..4], fileMonth[4..], stoppingToken).ConfigureAwait(false);
                                if (externalReferences.Archives == 0)
                                    await _archives.DeleteMonthAsync(fileMonth[..4], fileMonth[4..], stoppingToken).ConfigureAwait(false);
                                if (externalReferences.Curves > 0 || externalReferences.Archives > 0)
                                    _logger.LogWarning(
                                        "已清理运行库 {Month}，保留仍被其他月份引用的外置文件目录（曲线 {CurveRefs}，归档 {ArchiveRefs}）",
                                        month, externalReferences.Curves, externalReferences.Archives);
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
                                $"runtime month deleted; curve references in other months={retainedCurveReferences}; archive references in other months={retainedArchiveReferences}; spool-referenced file months={retainedSpoolFileMonths}",
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
                        .GetRequiredService<IAuditRetention>()
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
