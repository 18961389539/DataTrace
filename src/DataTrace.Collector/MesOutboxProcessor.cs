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
    private readonly IMesOutboxRelay _relay;
    private readonly ILogger<MesOutboxProcessor> _logger;

    public MesOutboxProcessor(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        IMesOutboxRelay relay,
        ILogger<MesOutboxProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _relay = relay;
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

                await _relay.RelayPendingAsync(200, stoppingToken).ConfigureAwait(false);
                var queue = scope.ServiceProvider.GetRequiredService<IMesOutboxQueue>();
                // 单轮多取一些：MES 停一段时间再恢复时积压是按托盘数累积的，
                // 一轮 20 条、5 秒一轮只能排 4 条/秒，积压几万条要几小时才追平。
                var pending = await queue.TakePendingAsync(200, stoppingToken).ConfigureAwait(false);

                var client = _httpFactory.CreateClient("mes");
                client.Timeout = TimeSpan.FromSeconds(Math.Max(3, config.Settings.MesTimeoutSeconds));
                var attempts = new List<MesOutboxAttempt>();

                foreach (var item in pending)
                {
                    var attemptCount = item.AttemptCount + 1;
                    var attemptedAt = DateTime.Now;
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post, config.Settings.MesEndpoint)
                        {
                            Content = JsonContent.Create(new
                            {
                                item.SerialNo,
                                item.PalletCode,
                                item.PalletSessionId,
                                payload = item.PayloadJson
                            })
                        };
                        if (!string.IsNullOrWhiteSpace(item.EventId))
                        {
                            request.Headers.TryAddWithoutValidation("Idempotency-Key", item.EventId);
                        }

                        var response = await client.SendAsync(request, stoppingToken)
                            .ConfigureAwait(false);
                        attempts.Add(new MesOutboxAttempt
                        {
                            Id = item.Id,
                            AttemptCount = attemptCount,
                            AttemptedAt = attemptedAt,
                            Succeeded = response.IsSuccessStatusCode,
                            Error = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}",
                            NextAttemptAt = response.IsSuccessStatusCode ? null : NextAttemptAt(attemptedAt, attemptCount)
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
                            Error = ex.Message,
                            NextAttemptAt = NextAttemptAt(attemptedAt, attemptCount)
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

    private static DateTime NextAttemptAt(DateTime attemptedAt, int attemptCount)
        => attemptedAt.AddSeconds(Math.Min(300, Math.Pow(2, attemptCount)));
}
