using System.Net.Http.Json;
using System.Text.Json;
using DataTrace.Application.Alarms;
using DataTrace.Application.Configuration;
using DataTrace.Application.Realtime;
using DataTrace.Application.Runtime;
using DataTrace.Collector;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Infrastructure.Backup;
using DataTrace.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataTrace.Web.Services;

/// <summary>
/// 不依赖当前打开的是哪一页：周期性判断采集心跳、工站故障、补传积压、MES 积压和连续不合格，
/// 写到看板上让所有页面响铃，直到有人接手；配置了呼叫地址时，把尚未接手的异常 POST 出去。
/// </summary>
public sealed class LineAlarmHostedService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRuntimeStatusHub _status;
    private readonly ILineAlarmBoard _board;
    private readonly IHttpClientFactory _http;
    private readonly DataRootPaths _paths;
    private readonly ILogger<LineAlarmHostedService> _logger;
    private readonly Dictionary<string, DateTime> _lastSent = new(StringComparer.Ordinal);
    private string? _rejectedUrl;

    public LineAlarmHostedService(
        IServiceScopeFactory scopeFactory,
        IRuntimeStatusHub status,
        ILineAlarmBoard board,
        IHttpClientFactory http,
        DataRootPaths paths,
        ILogger<LineAlarmHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _status = status;
        _board = board;
        _http = http;
        _paths = paths;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await NgStreakStartup.EnsureAsync(_scopeFactory, _status, _logger, stoppingToken).ConfigureAwait(false);
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "异常呼叫检查失败");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(SystemDefaults.AlarmPollSeconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var snapshot = await config.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var settings = snapshot.Settings;
        var mes = settings.MesEnabled
            ? await config.GetMesOutboxStatusAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var now = DateTime.Now;
        var faults = _status.Stations
            .Where(station => station.State == StationRuntimeState.Fault)
            .Select(station => new StationFaultNotice(station.StationId, station.StationCode, FaultDetail(station)))
            .ToList();
        var spoolStore = scope.ServiceProvider.GetRequiredService<ISpoolStore>();
        var spool = await spoolStore.DescribeAsync(cancellationToken).ConfigureAwait(false);
        var sessions = scope.ServiceProvider.GetRequiredService<IActiveSessionStore>();
        var listed = await sessions.ListAsync(cancellationToken).ConfigureAwait(false);
        var openSessions = OpenSessionRules.Due(listed, now);
        _status.ReplaceOpenSessions(openSessions);
        _status.ReplaceInProcessRecipes(InProcessRecipes.From(listed));
        var active = LineAlarmRules.Evaluate(
            settings.CollectEnabled,
            _status.LastCollectorAt,
            settings.MesEnabled,
            mes?.PendingCount ?? 0,
            mes?.OldestPendingAt,
            _status.NgStreaks,
            now,
            faults,
            spool.Count,
            spool.OldestAt,
            _status.WarningStreaks,
            TagWatchRules.ForEnabledRules(
                _status.DriftNotices,
                snapshot.Stations.SelectMany(station => station.Tags)
                    .ToDictionary(tag => (tag.StationId, tag.Id), tag => tag.SpcRuleMask)),
            openSessions,
            DiskSpace.FreeMegabytesOf(_paths.Root));
        var incidents = scope.ServiceProvider.GetRequiredService<IAlarmIncidents>();
        var attention = await incidents.ApplyAsync(active.Alarms, now, cancellationToken).ConfigureAwait(false);
        var calling = attention
            .Where(item => item.NeedsOwner)
            .Select(item => new LineAlarm(
                item.Key,
                item.Kind,
                item.StillActive ? item.Message : item.Message + "（已恢复，仍待接手）"))
            .ToList();
        _board.Replace(new LineAlarmSnapshot(calling));

        var activeKeys = calling.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _lastSent.Keys.Where(key => !activeKeys.Contains(key)).ToList())
        {
            _lastSent.Remove(key);
        }

        var url = settings.AlarmWebhookUrl?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            if (!string.Equals(_rejectedUrl, url, StringComparison.Ordinal))
            {
                _rejectedUrl = url;
                _logger.LogWarning("异常呼叫地址不是 http(s)，已跳过发送");
            }

            return;
        }

        _rejectedUrl = null;

        var repeat = TimeSpan.FromMinutes(SystemDefaults.AlarmRepeatMinutes);
        var due = LineAlarmRules.Due(calling, _lastSent, now, repeat);
        if (due.Count == 0)
        {
            return;
        }

        var sent = await PostAsync(uri, due, now, cancellationToken).ConfigureAwait(false);
        var mark = sent ? now : now - repeat + TimeSpan.FromMinutes(1);
        foreach (var alarm in due)
        {
            _lastSent[alarm.Key] = mark;
        }
    }

    private static string FaultDetail(StationRuntimeStatus station)
    {
        if (!string.IsNullOrWhiteSpace(station.LastError))
        {
            return station.LastError.Trim();
        }

        return station.LastResultCode is short code ? ResultCodes.Describe(code) : "原因未记下";
    }

    private async Task<bool> PostAsync(Uri url, IReadOnlyList<LineAlarm> alarms, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            var client = _http.CreateClient("alarm");
            using var response = await client.PostAsJsonAsync(url, new
            {
                sentAt = now.ToString("yyyy-MM-dd HH:mm:ss"),
                alarms = alarms.Select(a => new { key = a.Key, kind = a.Kind.ToString(), message = a.Message })
            }, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("异常呼叫发送失败，HTTP {StatusCode}", (int)response.StatusCode);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "异常呼叫发送失败");
            return false;
        }
    }
}
