using DataTrace.Application.Mes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;

namespace DataTrace.Collector;

public sealed class MesOutboxProcessor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MesOutboxProcessor> _logger;

    public MesOutboxProcessor(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        ILogger<MesOutboxProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var config = await scope.ServiceProvider.GetRequiredService<Application.Configuration.IConfigRepository>()
                    .GetSnapshotAsync(stoppingToken).ConfigureAwait(false);
                if (!config.Settings.MesEnabled || string.IsNullOrWhiteSpace(config.Settings.MesEndpoint))
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var queue = scope.ServiceProvider.GetRequiredService<IMesOutboxQueue>();
                // 单轮多取一些：MES 停一段时间再恢复时积压是按托盘数累积的，
                // 一轮 20 条、5 秒一轮只能排 4 条/秒，积压几万条要几小时才追平。
                var pending = await queue.TakePendingAsync(200, stoppingToken).ConfigureAwait(false);

                var client = _httpFactory.CreateClient("mes");
                client.Timeout = TimeSpan.FromSeconds(Math.Max(3, config.Settings.MesTimeoutSeconds));
                var attempts = new List<MesOutboxAttempt>();

                foreach (var item in pending)
                {
                    var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, item.AttemptCount)));
                    if (item.LastAttemptAt is { } last && DateTime.Now - last < delay)
                    {
                        continue;
                    }

                    var attemptCount = item.AttemptCount + 1;
                    var attemptedAt = DateTime.Now;
                    try
                    {
                        using var content = JsonContent.Create(new
                        {
                            item.SerialNo,
                            item.PalletCode,
                            item.PalletSessionId,
                            payload = item.PayloadJson
                        });
                        var response = await client.PostAsync(config.Settings.MesEndpoint, content, stoppingToken)
                            .ConfigureAwait(false);
                        attempts.Add(new MesOutboxAttempt
                        {
                            Id = item.Id,
                            AttemptCount = attemptCount,
                            AttemptedAt = attemptedAt,
                            Succeeded = response.IsSuccessStatusCode,
                            Error = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}"
                        });
                    }
                    catch (Exception ex)
                    {
                        attempts.Add(new MesOutboxAttempt
                        {
                            Id = item.Id,
                            AttemptCount = attemptCount,
                            AttemptedAt = attemptedAt,
                            Succeeded = false,
                            Error = ex.Message
                        });
                        _logger.LogWarning(ex, "MES 推送失败 {Serial}", item.SerialNo);
                    }
                }

                await queue.SaveAttemptsAsync(attempts, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MES Outbox 循环异常");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }
}
