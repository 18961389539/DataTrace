using DataTrace.Application.Backup;
using DataTrace.Application.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace DataTrace.Infrastructure.Backup;

[DisallowConcurrentExecution]
public sealed class DatabaseBackupJob : IJob
{
    public const string JobName = "database-backup";
    public const string TriggerKindKey = "trigger-kind";

    private readonly IDatabaseBackupService _backup;
    private readonly IOptionsMonitor<BackupOptions> _options;
    private readonly ILogger<DatabaseBackupJob> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public DatabaseBackupJob(
        IDatabaseBackupService backup,
        IOptionsMonitor<BackupOptions> options,
        ILogger<DatabaseBackupJob> logger,
        IServiceScopeFactory scopeFactory)
    {
        _backup = backup;
        _options = options;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return;
        }

        var trigger = context.MergedJobDataMap.GetString(TriggerKindKey) ?? "scheduled";
        try
        {
            var result = await RunAuditedBackupAsync(trigger, context.CancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                _logger.LogWarning("数据库备份失败：{Error}", result.Error);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quartz 数据库备份作业异常；trigger={Trigger}", trigger);
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
}