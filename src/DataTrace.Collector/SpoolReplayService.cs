using DataTrace.Domain.Constants;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataTrace.Collector;

/// <summary>周期性把写库失败的缓存补传进去。真正的重放逻辑在 <see cref="SpoolReplayRunner"/>（与手动补传共用）。</summary>
public sealed class SpoolReplayService : BackgroundService
{
    private readonly SpoolReplayRunner _runner;
    private readonly ILogger<SpoolReplayService> _logger;

    public SpoolReplayService(SpoolReplayRunner runner, ILogger<SpoolReplayService> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var done = await _runner.ReplayAllAsync(stoppingToken).ConfigureAwait(false);
                if (done > 0)
                {
                    _logger.LogInformation("本轮补传入库 {Count} 条", done);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "补传循环异常");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(SystemDefaults.SpoolReplaySeconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
