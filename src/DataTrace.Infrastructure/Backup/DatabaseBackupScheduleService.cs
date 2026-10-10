using DataTrace.Application.Backup;
using DataTrace.Application.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace DataTrace.Infrastructure.Backup;

public sealed class DatabaseBackupScheduleService : BackgroundService
{
    private static readonly JobKey BackupJobKey = new(DatabaseBackupJob.JobName);
    private static readonly TriggerKey DailyTriggerKey = new("database-backup-daily");
    private static readonly TriggerKey CatchUpTriggerKey = new("database-backup-startup-catch-up");

    private readonly ISchedulerFactory _schedulerFactory;
    private readonly IDatabaseBackupService _backup;
    private readonly IOptionsMonitor<BackupOptions> _options;
    private readonly ILogger<DatabaseBackupScheduleService> _logger;
    private readonly SemaphoreSlim _scheduleGate = new(1, 1);

    private IScheduler? _scheduler;
    private IDisposable? _optionsSubscription;

    public DatabaseBackupScheduleService(
        ISchedulerFactory schedulerFactory,
        IDatabaseBackupService backup,
        IOptionsMonitor<BackupOptions> options,
        ILogger<DatabaseBackupScheduleService> logger)
    {
        _schedulerFactory = schedulerFactory;
        _backup = backup;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let database seeding and migration finish before checking for a catch-up backup.
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
            _scheduler = await _schedulerFactory.GetScheduler(stoppingToken).ConfigureAwait(false);
            var job = JobBuilder.Create<DatabaseBackupJob>()
                .WithIdentity(BackupJobKey)
                .StoreDurably()
                .Build();
            await _scheduler.AddJob(job, replace: true, stoppingToken).ConfigureAwait(false);

            _optionsSubscription = _options.OnChange((_, _) =>
                _ = RefreshFromCurrentOptionsAsync(stoppingToken));

            var current = _options.CurrentValue;
            if (current.Enabled && _backup.NeedsCatchUpBackup())
            {
                _logger.LogInformation("启动补备份：距上次成功备份已超过 24 小时（或尚无成功记录）");
                var trigger = TriggerBuilder.Create()
                    .WithIdentity(CatchUpTriggerKey)
                    .ForJob(BackupJobKey)
                    .UsingJobData(DatabaseBackupJob.TriggerKindKey, "startup-catch-up")
                    .StartNow()
                    .Build();
                await _scheduler.ScheduleJob(trigger, stoppingToken).ConfigureAwait(false);
            }

            await RefreshDailyScheduleAsync(current, stoppingToken).ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _optionsSubscription?.Dispose();
        }
    }

    public static ICronTrigger CreateDailyTrigger(TimeOnly dailyTime)
        => (ICronTrigger)TriggerBuilder.Create()
            .WithIdentity(DailyTriggerKey)
            .ForJob(BackupJobKey)
            .WithSchedule(CronScheduleBuilder
                .DailyAtHourAndMinute(dailyTime.Hour, dailyTime.Minute)
                .InTimeZone(TimeZoneInfo.Local)
                .WithMisfireHandlingInstructionDoNothing())
            .Build();

    private async Task RefreshFromCurrentOptionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshDailyScheduleAsync(_options.CurrentValue, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新数据库备份 Quartz 计划失败");
        }
    }

    private async Task RefreshDailyScheduleAsync(BackupOptions options, CancellationToken cancellationToken)
    {
        if (_scheduler is null)
        {
            return;
        }

        await _scheduleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _scheduler.GetTrigger(DailyTriggerKey, cancellationToken).ConfigureAwait(false);
            if (!options.Enabled)
            {
                if (current is not null)
                {
                    await _scheduler.UnscheduleJob(DailyTriggerKey, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("已禁用定时数据库备份");
                }

                return;
            }

            var desired = CreateDailyTrigger(options.GetDailyTimeOrDefault());
            if (current is ICronTrigger scheduled
                && scheduled.CronExpressionString == desired.CronExpressionString
                && scheduled.TimeZone.Id == desired.TimeZone.Id)
            {
                return;
            }

            if (current is null)
            {
                await _scheduler.ScheduleJob(desired, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _scheduler.RescheduleJob(DailyTriggerKey, desired, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "已设置每日数据库备份时间 {DailyTime}（本地时间）",
                options.GetDailyTimeOrDefault());
        }
        finally
        {
            _scheduleGate.Release();
        }
    }
}