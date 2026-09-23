using DataTrace.Application.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataTrace.Collector;

/// <summary>
/// 定期重建波形基线并写入缓存。
/// </summary>
/// <remarks>
/// 基线是**聚合量**，不能等采集线程来算 —— 那会把历史库查询压进采集主链路，
/// 而采集是有 PLC 握手时序要求的。这里把重活挪到后台，采集侧只做一次字典查找加一次纯计算。
/// <para>
/// 刷新频率不需要高：数据是一托盘一条积累起来的，5 分钟足够覆盖一次写入量。
/// 首次启动时缓存为空，此时采集照常落库、只是不带偏离分 —— 这是刻意的降级，
/// 而不是"样本不足就当正常"。
/// </para>
/// </remarks>
public sealed class CurveBaselineRefresher : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICurveBaselineCache _cache;
    private readonly ILogger<CurveBaselineRefresher> _logger;

    public CurveBaselineRefresher(
        IServiceScopeFactory scopeFactory,
        ICurveBaselineCache cache,
        ILogger<CurveBaselineRefresher> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("波形基线刷新服务已启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 刷新失败不能让采集受影响：采集只会在没有基线时退化成"不打分"。
                _logger.LogError(ex, "刷新波形基线失败");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<CurveBaselineFactory>();

        var snapshot = await factory.BuildAsync(DateTime.Now, cancellationToken).ConfigureAwait(false);
        _cache.Replace(snapshot);

        _logger.LogInformation(
            "波形基线已刷新：型号 {Recipe}，建立 {Built} 条序列的基线",
            string.IsNullOrEmpty(snapshot.RecipeCode) ? "(未选)" : snapshot.RecipeCode,
            snapshot.Count);
    }
}
