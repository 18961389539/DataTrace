using DataTrace.Application.Configuration;
using DataTrace.Application.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataTrace.Collector;

public sealed class SpoolReplayService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISpoolStore _spool;
    private readonly ILogger<SpoolReplayService> _logger;

    public SpoolReplayService(IServiceScopeFactory scopeFactory, ISpoolStore spool, ILogger<SpoolReplayService> logger)
    {
        _scopeFactory = scopeFactory;
        _spool = spool;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var items = await _spool.ListAsync(stoppingToken).ConfigureAwait(false);
                if (items.Count > 0)
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var runtime = scope.ServiceProvider.GetRequiredService<IRuntimeStore>();
                    foreach (var (file, request) in items)
                    {
                        try
                        {
                            await runtime.SaveAsync(request, stoppingToken).ConfigureAwait(false);
                            await _spool.DeleteAsync(file, stoppingToken).ConfigureAwait(false);
                            _logger.LogInformation("补传成功 {File}", file);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "补传失败 {File}，稍后重试", file);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "补传循环异常");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
        }
    }
}
