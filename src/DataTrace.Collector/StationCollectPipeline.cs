using DataTrace.Application.Configuration;
using DataTrace.Application.Evaluation;
using DataTrace.Application.Mes;
using DataTrace.Application.Realtime;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Domain.Validation;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Addresses;
using DataTrace.Plc.Codec;
using DataTrace.Plc.Planning;
using DataTrace.Plc.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace DataTrace.Collector;

public sealed class StationCollectPipeline
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRuntimeStatusHub _status;
    private readonly ICollectEventBus _events;
    private readonly ICurveBaselineCache _baselines;
    private readonly ILogger<StationCollectPipeline> _logger;
    private readonly FileSourceReader _files;
    private readonly CollectSessionCoordinator _sessions;
    private readonly object _planGate = new();
    private int _planVersion = -1;
    private readonly Dictionary<int, StationAcquisitionPlan> _plans = new();

    public StationCollectPipeline(
        IServiceScopeFactory scopeFactory,
        IRuntimeStatusHub status,
        ICollectEventBus events,
        ICurveBaselineCache baselines,
        ICollectArchiveStore archives,
        ILogger<StationCollectPipeline> logger)
    {
        _scopeFactory = scopeFactory;
        _status = status;
        _events = events;
        _baselines = baselines;
        _logger = logger;
        _files = new FileSourceReader(archives, logger);
        _sessions = new CollectSessionCoordinator(scopeFactory, logger);
    }

    public async Task ExecuteAsync(
        Station station,
        PlcConnection connection,
        AppConfigurationSnapshot config,
        PlcRequestQueue queue,
        CancellationToken cancellationToken)
    {
        var triggerTime = DateTime.Now;
        var sw = Stopwatch.StartNew();
        short resultCode = ResultCodes.InternalError;
        string? error = null;
        CollectOutcome? outcome = null;
        CollectRecord? saved = null;

        SetStatus(station, StationRuntimeState.Busy, null, null, null, Judgement.None, null, null);

        try
        {
            outcome = await CollectAndSaveAsync(station, connection, config, queue, triggerTime, sw, cancellationToken)
                .ConfigureAwait(false);
            saved = outcome.Record;
            resultCode = saved.ResultCode;
            error = saved.ErrorMessage;
        }
        catch (PlcDriverException ex)
        {
            resultCode = ResultCodes.PlcReadFailed;
            error = ex.Message;
            _logger.LogError(ex, "工站 {Station} PLC 通讯失败", station.Code);
            await WriteFailureAuditAsync(station, triggerTime, resultCode, error).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            resultCode = ResultCodes.InternalError;
            error = ex.Message;
            _logger.LogError(ex, "工站 {Station} 采集内部异常", station.Code);
        }
        finally
        {
            var wrote = await WriteResultAsync(station, connection, queue, resultCode, config.Settings, cancellationToken)
                .ConfigureAwait(false);
            // DurationMs 不在这里赋值：之前在 finally 里改内存对象、库早已写完，等于从没落库。
            // 现在它在 CollectAndSaveAsync 的记录构造时就取好了，这里直接把同一个值报给看板 ——
            // 看板节拍与明细/查询列表显示的是同一个数。
            // 通讯失败、文件读不到、归档失败、回写失败都要人去处理，标成故障。
            // 托盘码非法和判废是业务结果，工站本身没问题。
            var fault = resultCode is ResultCodes.PlcReadFailed or ResultCodes.InternalError
                or ResultCodes.FileSourceFailed or ResultCodes.ArchiveFailed
                || !wrote;
            var statusError = error;
            if (!wrote)
            {
                statusError = string.IsNullOrWhiteSpace(error) ? "响应码写回失败" : $"{error}；响应码写回失败";
            }

            SetStatus(
                station,
                fault ? StationRuntimeState.Fault : StationRuntimeState.Idle,
                saved?.PalletCode,
                saved?.SerialNo,
                resultCode,
                saved?.Judgement ?? Judgement.None,
                statusError,
                saved?.DurationMs,
                outcome?.Tags,
                outcome?.Curves,
                outcome?.MonthKey,
                saved?.Id);
            if (saved is not null)
            {
                _events.Publish(saved);
            }
        }
    }

    private async Task<CollectOutcome> CollectAndSaveAsync(
        Station station,
        PlcConnection connection,
        AppConfigurationSnapshot config,
        PlcRequestQueue queue,
        DateTime triggerTime,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        var plan = AcquisitionPlan(config, station, connection, queue);
        var buffers = await StationBlockReader.ReadAsync(plan, queue, cancellationToken).ConfigureAwait(false);
        var palletCode = StationSampleDecoder.PalletCode(station, connection, plan.Plan, buffers);
        if (!PalletCodeValidator.IsValid(palletCode, out var palletError))
        {
            return Live(station, Unrecorded(station, config, palletCode, triggerTime, ResultCodes.InvalidPalletCode, palletError));
        }

        FileSourceRead? source = null;
        if (station.Tags.Any(t => t.Enabled && t.Source == TagDataSource.JsonFile))
        {
            source = await _files.ReadAsync(station, triggerTime, palletCode, cancellationToken).ConfigureAwait(false);
            if (source.Error is not null)
            {
                return Live(station, Unrecorded(station, config, palletCode, triggerTime, source.ErrorCode, source.Error));
            }
        }

        var frozen = station.IsFirstStation
            ? null
            : await LookupFrozenRecipeAsync(palletCode, cancellationToken).ConfigureAwait(false);
        var recipe = SessionRecipe.Select(config, station.IsFirstStation, frozen);
        var assessment = CollectEvaluator.Evaluate(station, connection, plan.Plan, buffers, source, _baselines, recipe);
        var saved = await _sessions.PersistAsync(
            station, config, palletCode, triggerTime, sw, source, assessment, cancellationToken).ConfigureAwait(false);
        return Live(station, saved.Record, saved.Curves, saved.MonthKey);
    }

    private StationAcquisitionPlan AcquisitionPlan(
        AppConfigurationSnapshot config,
        Station station,
        PlcConnection connection,
        PlcRequestQueue queue)
    {
        lock (_planGate)
        {
            if (_planVersion != config.Version)
            {
                _plans.Clear();
                _planVersion = config.Version;
            }

            if (_plans.TryGetValue(station.Id, out var cached))
            {
                return cached;
            }

            var compiled = StationAcquisitionPlanner.Compile(station, connection, queue.Driver);
            _plans[station.Id] = compiled;
            return compiled;
        }
    }

    private static CollectRecord Unrecorded(
        Station station,
        AppConfigurationSnapshot config,
        string palletCode,
        DateTime triggerTime,
        short resultCode,
        string error)
        => new()
        {
            PalletCode = palletCode,
            StationId = station.Id,
            StationCode = station.Code,
            TriggerTime = triggerTime,
            CompleteTime = DateTime.Now,
            ResultCode = resultCode,
            Judgement = Judgement.Ng,
            ErrorMessage = error,
            RecipeCode = RecipeCode(config)
        };

    private async Task<string?> LookupFrozenRecipeAsync(string palletCode, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sessions = scope.ServiceProvider.GetRequiredService<IActiveSessionStore>();
            var existing = await sessions.FindByPalletAsync(palletCode, cancellationToken).ConfigureAwait(false);
            return existing?.RecipeCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "读取在制型号失败，本站改用当前型号");
            return null;
        }
    }

    private async Task<bool> WriteResultAsync(
        Station station,
        PlcConnection connection,
        PlcRequestQueue queue,
        short resultCode,
        SystemSettings settings,
        CancellationToken cancellationToken)
    {
        if (!queue.Driver.TryParseAddress(station.TriggerAddress, out var address))
        {
            _logger.LogError("工站 {Station} 触发地址无法解析: {Address}", station.Code, station.TriggerAddress);
            return false;
        }

        if (address.IsBit)
        {
            _logger.LogError("工站 {Station} 触发地址是位地址，响应码无法写回: {Address}", station.Code, station.TriggerAddress);
            return false;
        }

        var retries = Math.Max(1, settings.WriteRetryCount);
        Exception? last = null;
        for (var i = 0; i < retries; i++)
        {
            try
            {
                await queue.WriteWordsAsync(address, ValueCodec.EncodeInt16(resultCode), cancellationToken)
                    .ConfigureAwait(false);
                var readBack = await queue.ReadWordsAsync(address, 1, cancellationToken).ConfigureAwait(false);
                if (readBack.Length > 0 && (short)readBack[0] == resultCode)
                {
                    return true;
                }

                last = new PlcDriverException("写回校验不一致");
            }
            catch (Exception ex)
            {
                last = ex;
            }

            // 等的是"下一次重试"：最后一次失败后没有下一次，再等就只是白拖工站的采集周期。
            if (i < retries - 1)
            {
                await Task.Delay(settings.WriteRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogError(last, "工站 {Station} 响应码写回失败", station.Code);
        return false;
    }

    private async Task WriteFailureAuditAsync(Station station, DateTime triggerTime, short resultCode, string error)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
            var correlationId = $"collect-{station.Id}-{triggerTime:yyyyMMddHHmmssfff}";
            await audit.WriteAsync(
                    "collector",
                    "CollectFailure",
                    "Station",
                    station.Code,
                    null,
                    $"resultCode={resultCode}; error={error}",
                    CancellationToken.None,
                    outcome: "Failure",
                    source: "Collector",
                    correlationId: correlationId)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工站 {Station} PLC 故障审计留痕失败", station.Code);
        }
    }

    private void SetStatus(
        Station station,
        StationRuntimeState state,
        string? pallet,
        string? serial,
        short? code,
        Judgement judgement,
        string? error,
        int? durationMs,
        IReadOnlyList<StationLiveTag>? tags = null,
        IReadOnlyList<StationLiveCurve>? curves = null,
        string? monthKey = null,
        long? recordId = null)
    {
        var previous = _status.Stations.FirstOrDefault(x => x.StationId == station.Id);
        var completed = code is not null;
        var completedAt = completed ? DateTime.Now : previous?.LastCompleteTime;
        _status.UpsertStation(new StationRuntimeStatus
        {
            StationId = station.Id,
            StationCode = station.Code,
            StationName = station.Name,
            Sequence = station.Sequence,
            State = state,
            LastPalletCode = pallet ?? previous?.LastPalletCode,
            LastSerialNo = serial ?? previous?.LastSerialNo,
            LastResultCode = code ?? previous?.LastResultCode,
            LastJudgement = judgement != Judgement.None ? judgement : previous?.LastJudgement ?? Judgement.None,
            LastCompleteTime = completedAt,
            LastPieceGap = PieceGap(previous?.LastCompleteTime, completedAt, completed, previous?.LastPieceGap),
            LastDurationMs = durationMs ?? previous?.LastDurationMs,
            LastError = error,
            LastMonthKey = monthKey ?? previous?.LastMonthKey,
            LastRecordId = recordId is > 0 ? recordId : previous?.LastRecordId,
            LastTags = tags ?? previous?.LastTags ?? [],
            LastCurves = curves ?? previous?.LastCurves ?? []
        });
    }

    /// <summary>这一件完成时刻减去上一件。没完成、或还没有上一件时，沿用上次的间隔。</summary>
    private static TimeSpan? PieceGap(DateTime? previousComplete, DateTime? completedAt, bool completed, TimeSpan? previousGap)
    {
        if (!completed || previousComplete is not DateTime prior || completedAt is not DateTime now || now <= prior)
        {
            return previousGap;
        }

        return now - prior;
    }

    private static CollectOutcome Live(
        Station station,
        CollectRecord record,
        IReadOnlyList<CurvePayloadWrite>? curves = null,
        string? monthKey = null)
        => new()
        {
            Record = record,
            Tags = MapTags(station, record.TagValues),
            Curves = MapCurves(curves),
            MonthKey = monthKey ?? PersistenceMonth(record.TriggerTime)
        };

    private static IReadOnlyList<StationLiveTag> MapTags(Station station, IEnumerable<TagValue>? values)
    {
        if (values is null)
        {
            return [];
        }

        var units = station.Tags.ToDictionary(t => t.Id, t => t.Unit);
        return values.Select(v => new StationLiveTag
        {
            Name = v.TagName,
            Display = v.TextValue ?? v.NumericValue?.ToString("0.###") ?? "-",
            Unit = units.GetValueOrDefault(v.TagId),
            NumericValue = v.NumericValue,
            LowerLimit = v.LowerLimit,
            UpperLimit = v.UpperLimit,
            WarningLowerLimit = v.WarningLowerLimit,
            WarningUpperLimit = v.WarningUpperLimit,
            OutOfLimit = v.IsOutOfLimit,
            Warning = v.IsWarning
        }).ToList();
    }

    private static IReadOnlyList<StationLiveCurve> MapCurves(IReadOnlyList<CurvePayloadWrite>? writes)
    {
        if (writes is null || writes.Count == 0)
        {
            return [];
        }

        return writes.Select(write =>
        {
            var y = write.Payload.Series.FirstOrDefault(s => s.Role == SeriesRole.Y)
                    ?? write.Payload.Series.FirstOrDefault();
            return new StationLiveCurve
            {
                Name = write.Record.CurveName,
                // 无效浮点保留在原始曲线文件用于追溯，但不能进入实时 JSON 状态，
                // 否则 SignalR 序列化会失败且看板也无法绘制可信曲线。
                Values = y is not null && y.Values.All(float.IsFinite) ? y.Values.ToArray() : []
            };
        }).ToList();
    }

    private sealed class CollectOutcome
    {
        public required CollectRecord Record { get; init; }
        public IReadOnlyList<StationLiveTag> Tags { get; init; } = [];
        public IReadOnlyList<StationLiveCurve> Curves { get; init; } = [];
        public string MonthKey { get; init; } = "";
    }

    private static string PersistenceMonth(DateTime time) => time.ToString("yyyyMM");

    /// <summary>本次判定所用的型号编码；未选择型号时为空串。</summary>
    private static string RecipeCode(AppConfigurationSnapshot config) => config.ActiveRecipe?.Code ?? "";

}
