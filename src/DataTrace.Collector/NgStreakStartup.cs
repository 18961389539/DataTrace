using DataTrace.Application.Alarms;
using DataTrace.Application.Realtime;
using DataTrace.Application.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DataTrace.Collector;

/// <summary>
/// 进程启动时从当月库重算连续 NG。最近记录和心跳不恢复：它们只描述这次进程活着的时候。
/// 只允许一个调用方真正读库，其余调用方等这一次完成，避免后写入把已经开始的计数盖掉。
/// </summary>
public static class NgStreakStartup
{
    public static async Task EnsureAsync(
        IServiceScopeFactory scopes,
        IRuntimeStatusHub status,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!status.TryClaimNgStreakRestore())
        {
            await status.NgStreaksReady.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var analytics = scope.ServiceProvider.GetRequiredService<IRuntimeAnalytics>();
            var month = DateTime.Now.ToString("yyyyMM");
            var marks = await analytics.ListMonthJudgementsAsync(month, cancellationToken).ConfigureAwait(false);
            status.CompleteNgStreakRestore(NgStreakRebuild.From(marks));
            try
            {
                var since = DateTime.Now.AddDays(-2);
                var observations = await analytics.ListRecentTagObservationsAsync(
                        month,
                        since,
                        TagWatchRules.DriftWindow,
                        cancellationToken)
                    .ConfigureAwait(false);
                status.CompleteTagWatchRestore(observations);
            }
            catch (Exception watchEx) when (watchEx is not OperationCanceledException)
            {
                logger.LogWarning(watchEx, "预警和过程漂移未能从当月记录恢复，本进程从 0 开始");
                status.CompleteTagWatchRestore([]);
            }
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "连续 NG 未能从当月记录恢复，本进程从 0 开始计数");
            }

            status.AbandonNgStreakRestore();
            if (ex is OperationCanceledException)
            {
                throw;
            }
        }
    }
}
