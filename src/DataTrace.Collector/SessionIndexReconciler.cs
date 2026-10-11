using DataTrace.Application.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataTrace.Collector;

/// <summary>
/// 在制索引意图对账循环：启动先跑一轮（回收上一次硬退出留下的未决意图），之后周期兜底。
/// 正常路径上索引更新失败会立刻由 PostCommitStateUpdateException → spool 补传重试；
/// 本循环只管那些"连异常都没来得及抛"的窗口（断电、杀进程），以及补传也失效时的最后兜底。
/// </summary>
public sealed class SessionIndexReconciler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionIndexReconciler> _logger;

    public SessionIndexReconciler(IServiceScopeFactory scopeFactory, ILogger<SessionIndexReconciler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var relay = scope.ServiceProvider.GetRequiredService<ISessionIndexRelay>();
                var applied = await relay.RelayPendingAsync(200, stoppingToken).ConfigureAwait(false);
                if (applied > 0)
                {
                    _logger.LogWarning("在制索引对账追回 {Count} 条未决意图", applied);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "在制索引对账循环异常");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
        }
    }
}
