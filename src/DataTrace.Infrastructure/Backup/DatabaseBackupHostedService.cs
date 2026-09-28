using DataTrace.Application.Backup;
using DataTrace.Application.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Infrastructure.Backup;

/// <summary>
/// 每日按 Backup:DailyTime 触发在线备份；启动时若距上次成功超过 24 小时则补做一次。
/// 失败只记日志，不拖垮宿主。
/// </summary>
public sealed class DatabaseBackupHostedService : BackgroundService
{
    private readonly IDatabaseBackupService _backup;
    private readonly IOptionsMonitor<BackupOptions> _options;
    private readonly ILogger<DatabaseBackupHostedService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public DatabaseBackupHostedService(
        IDatabaseBackupService backup,
        IOptionsMonitor<BackupOptions> options,
        ILogger<DatabaseBackupHostedService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _backup = backup;
        _options = options;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 稍等应用完成迁移/种子，避免抢启动
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_options.CurrentValue.Enabled && _backup.NeedsCatchUpBackup())
        {
            _logger.LogInformation("启动补备份：距上次成功备份已超过 24 小时（或尚无成功记录）");
            try
            {
                var result = await RunAuditedBackupAsync("startup-catch-up", stoppingToken).ConfigureAwait(false);
                if (!result.Success)
                {
                    _logger.LogWarning("启动补备份失败：{Error}", result.Error);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "启动补备份异常");
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opts = _options.CurrentValue;
                if (!opts.Enabled)
                {
                    await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var delay = DelayUntilDaily(opts.GetDailyTimeOrDefault());
                _logger.LogDebug("下一次数据库备份将在 {Delay} 后执行", delay);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);

                if (!_options.CurrentValue.Enabled)
                {
                    continue;
                }

                var run = await RunAuditedBackupAsync("scheduled", stoppingToken).ConfigureAwait(false);
                if (!run.Success)
                {
                    _logger.LogWarning("定时数据库备份失败：{Error}", run.Error);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数据库备份调度循环异常");
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<BackupRunResult> RunAuditedBackupAsync(string trigger, CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        try
        {
            var result = await _backup.RunBackupAsync(cancellationToken).ConfigureAwait(false);
            await WriteBackupAuditAsync(
                trigger,
                result.Success ? "Success" : "Failure",
                result.Success
                    ? $"success; path={result.BackupPath}; bytes={result.TotalBytes}"
                    : $"failure; {result.Error}",
                correlationId,
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteBackupAuditAsync(
                trigger, "Failure", $"exception; {ex.Message}", correlationId, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async Task WriteBackupAuditAsync(
        string trigger,
        string outcome,
        string details,
        string correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
            await audit.WriteAsync(
                "system",
                "Backup",
                "Backup",
                trigger,
                null,
                details,
                cancellationToken,
                outcome,
                "Automatic backup service",
                correlationId: correlationId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "自动备份已执行，但审计写入失败；trigger={Trigger}", trigger);
        }
    }

    private static TimeSpan DelayUntilDaily(TimeOnly daily)
    {
        var now = DateTime.Now;
        var next = new DateTime(now.Year, now.Month, now.Day, daily.Hour, daily.Minute, 0);
        if (next <= now)
        {
            next = next.AddDays(1);
        }

        var delay = next - now;
        return delay < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : delay;
    }
}
