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
using System.Collections.Concurrent;

namespace DataTrace.Collector;

public sealed class StationCollectPipeline
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRuntimeStatusHub _status;
    private readonly ICollectEventBus _events;
    private readonly ICurveBaselineCache _baselines;
    private readonly IPendingWriteBackStore _pending;
    private readonly ILogger<StationCollectPipeline> _logger;
    private readonly FileSourceReader _files;
    private readonly CollectSessionCoordinator _sessions;
    private readonly ConcurrentDictionary<int, HeldPersistence> _heldPersistence = new();
    private readonly object _planGate = new();
    private readonly Dictionary<(int Version, long? RevisionId, int StationId, int ConnectionId), StationAcquisitionPlan> _plans = new();

    public StationCollectPipeline(
        IServiceScopeFactory scopeFactory,
        IRuntimeStatusHub status,
        ICollectEventBus events,
        ICurveBaselineCache baselines,
        ICollectArchiveStore archives,
        IPendingWriteBackStore pending,
        ILogger<StationCollectPipeline> logger)
    {
        _scopeFactory = scopeFactory;
        _status = status;
        _events = events;
        _baselines = baselines;
        _pending = pending;
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
        await using var operationScope = _scopeFactory.CreateAsyncScope();
        var barrier = operationScope.ServiceProvider.GetRequiredService<IDataMaintenanceBarrier>();
        await using var maintenanceLease = await barrier.EnterReadAsync(cancellationToken).ConfigureAwait(false);
        var triggerTime = DateTime.Now;
        var sw = Stopwatch.StartNew();

        // 一次采集一个日志作用域：读取、判定、落库、故障、回写这几段散在四个类里，
        // 以前各自的日志只带工站码，同一工站连续两件就分不清哪行属于哪一件。
        // 作用域是 AsyncLocal 的，各工站的采集任务并发跑也互不串味。
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            [CollectionLogScope.Station] = station.Code,
            [CollectionLogScope.Trigger] = triggerTime.ToString("HH:mm:ss.fff")
        });

        short resultCode = ResultCodes.InternalError;
        string? error = null;
        CollectOutcome? outcome = null;
        CollectRecord? saved = null;
        var storageHeld = false;
        var suppressWriteBack = false;
        CollectSaveRequest? unpersistedRequest = null;

        SetStatus(station, StationRuntimeState.Busy, null, null, null, Judgement.None, null, null);

        if (_heldPersistence.TryGetValue(station.Id, out var existingHold))
        {
            SetStatus(station, StationRuntimeState.Fault, existingHold.Request.Record.PalletCode,
                existingHold.Request.Record.SerialNo, ResultCodes.DatabaseWriteFailed,
                existingHold.Request.Record.Judgement,
                "该工站仍有未持久化的原始采集请求；禁止重读设备或分配新流水。",
                existingHold.Request.Record.DurationMs,
                monthKey: existingHold.Request.MonthKey,
                recordId: existingHold.Request.Record.Id > 0 ? existingHold.Request.Record.Id : null,
                writeBackOk: false);
            return;
        }

        try
        {
            outcome = await CollectAndSaveAsync(station, connection, config, queue, triggerTime, sw, cancellationToken)
                .ConfigureAwait(false);
            saved = outcome.Record;
            resultCode = saved.ResultCode;
            error = saved.ErrorMessage;
        }
        catch (CollectPersistenceUnavailableException ex)
        {
            resultCode = ResultCodes.DatabaseWriteFailed;
            error = "月库与本地 spool 均无法保存本次采集；触发位保持不变，原始采集请求已留在有界内存保护队列，恢复后只重试保存，不重读设备、不分配新流水、不回写响应码。";
            ex.Request.RequiresWriteBack = true;
            ex.Request.RequiresManualReviewOnPersistence = true;
            unpersistedRequest = ex.Request;
            var retained = _heldPersistence.TryAdd(station.Id, new HeldPersistence(ex.Request));
            storageHeld = true;
            suppressWriteBack = true;
            if (!retained)
                error = "月库与本地 spool 均无法保存本次采集，且工站已有内存保护请求；本件未确认，需立即检查。";
            _logger.LogCritical(ex, "工站 {Station} 双持久化失败，触发位保持，流水 {Serial} 原始请求受保护={Protected}",
                station.Code, ex.Request.Record.SerialNo, retained);
        }
        catch (PlcDriverException ex)
        {
            resultCode = ResultCodes.PlcReadFailed;
            error = ex.Message;
            _logger.LogError(ex, "工站 {Station} PLC 通讯失败", station.Code);
            await WriteFailureAuditAsync(station, triggerTime, resultCode, error).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            resultCode = ResultCodes.InternalError;
            error = "采集在设备响应码确认前取消；触发位保持，不写响应码。";
            suppressWriteBack = true;
            _logger.LogWarning("工站 {Station} 采集取消，按安全策略不写 PLC 响应码", station.Code);
        }
        catch (InvalidDataException ex)
        {
            resultCode = ResultCodes.InternalError;
            error = ex.Message;
            _logger.LogError(ex, "工站 {Station} 判定依据无效，采集已阻塞", station.Code);
        }
        catch (Exception ex)
        {
            resultCode = ResultCodes.InternalError;
            error = ex.Message;
            _logger.LogError(ex, "工站 {Station} 采集内部异常", station.Code);
        }
        finally
        {
            var writeBack = suppressWriteBack
                ? new WriteBackOutcome(0, false, storageHeld ? "双持久化失败，按安全策略不写 PLC" : "采集取消，按安全策略不写 PLC")
                : await WriteResultAsync(station, queue, resultCode)
                    .ConfigureAwait(false);
            // DurationMs 不在这里赋值：之前在 finally 里改内存对象、库早已写完，等于从没落库。
            // 现在它在 CollectAndSaveAsync 的记录构造时就取好了，这里直接把同一个值报给看板 ——
            // 看板节拍与明细/查询列表显示的是同一个数。
            // 通讯失败、文件读不到、归档失败、回写失败都要人去处理，标成故障。
            // 托盘码非法和判废是业务结果，工站本身没问题。
            var fault = resultCode is ResultCodes.PlcReadFailed or ResultCodes.InternalError or ResultCodes.DatabaseWriteFailed
                or ResultCodes.FileSourceFailed or ResultCodes.ArchiveFailed
                || !writeBack.Ok;
            var statusError = error;
            if (!writeBack.Ok)
            {
                // 尝试 0 次说明连地址都没解析出来（位地址/解析失败），说"尝试 0 次"只会让人以为没重试够。
                var detail = suppressWriteBack
                    ? (storageHeld ? "双持久化失败，触发位保持且禁止响应码写回" : "采集取消，触发位保持且禁止响应码写回")
                    : (writeBack.Attempts == 0
                        ? "响应码写回失败"
                        : $"响应码写回/核对失败（单次写入，尝试 {writeBack.Attempts} 次）");
                statusError = string.IsNullOrWhiteSpace(error) ? detail : $"{error}；{detail}";
            }

            SetStatus(
                station,
                fault ? StationRuntimeState.Fault : StationRuntimeState.Idle,
                saved?.PalletCode ?? unpersistedRequest?.Record.PalletCode,
                saved?.SerialNo ?? unpersistedRequest?.Record.SerialNo,
                resultCode,
                saved?.Judgement ?? Judgement.None,
                statusError,
                saved?.DurationMs,
                outcome?.Tags,
                outcome?.Curves,
                outcome?.MonthKey,
                saved?.Id ?? (unpersistedRequest?.Record.Id > 0 ? unpersistedRequest.Record.Id : null),
                writeBack.Attempts,
                writeBack.Ok);
            if (saved is not null)
            {
                _events.Publish(saved);

                // "待确认"留证在落库事务里就已登记（见 RuntimeStore.SaveAsync，F04）：
                // 精确回读成功才按实际月库销账；任何失败都把留证转为人工核对，禁止自动重写。
                // 之前"finally 里才留证"的写法，提交后到留证前的断电仍会让同一件被重新采集。
                if (writeBack.Ok)
                {
                    if (saved.Id > 0 && !string.IsNullOrWhiteSpace(outcome?.MonthKey))
                    {
                        await _pending.DeleteByRecordIdAsync(outcome.MonthKey, saved.Id, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    if (saved.Id > 0 && !string.IsNullOrWhiteSpace(outcome?.MonthKey))
                    {
                        try
                        {
                            await _pending.RequireManualReviewByRecordIdAsync(
                                outcome.MonthKey,
                                saved.Id,
                                "响应码单次写入或读回核对失败；设备事件身份未知，禁止自动重写并等待现场人工核对",
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogCritical(ex, "工站 {Station} 回写不确定且未能持久标记人工核对；原待确认保护保留", station.Code);
                        }
                    }
                    _logger.LogWarning(
                        "工站 {Station} 回写未确认，留证保持人工核对阻塞：流水 {Serial} 结果 {Result}",
                        station.Code,
                        saved.SerialNo,
                        ResultCodes.Describe(resultCode));
                }
            }

            // 这一行是整条链路的锚点：现场报"某件丢了 / 某件慢了"时，先用流水号搜到它，
            // 再顺着同一个作用域看它前后那些行（读取、落库、补传、回写）。
            // 正常件也要记：只记异常的话，来查一件正常件时会发现根本没有锚点可搜。
            // 代价是每件一行 —— 而这一轮顺带消掉了每轮好几条 EF Core SQL 行，净日志量是下降的。
            _logger.LogInformation(
                "采集完成 {Serial} 结果 {Result} 判定 {Judgement} 耗时 {DurationMs}ms 回写 {WriteBack}",
                string.IsNullOrWhiteSpace(saved?.SerialNo) ? "（未落库）" : saved.SerialNo,
                ResultCodes.Describe(resultCode),
                saved?.Judgement ?? Judgement.None,
                sw.ElapsedMilliseconds,
                writeBack.Ok ? "单次写入并核对成功" : suppressWriteBack ? "未写 PLC（采集/存储阻塞）" : $"单次写入/核对失败 {writeBack.Attempts}");
        }
    }

    public bool HasHeldPersistence(int stationId) => _heldPersistence.ContainsKey(stationId);

    /// <summary>
    /// A trigger read from the poll round is supplied as current device-state evidence. The original
    /// request is retried without any PLC acquisition or acknowledgement, regardless of trigger value.
    /// </summary>
    public async Task RetryHeldPersistenceAsync(int stationId, short observedTriggerValue, CancellationToken cancellationToken)
    {
        if (!_heldPersistence.TryGetValue(stationId, out var held) || DateTime.UtcNow < held.NextAttemptAtUtc)
            return;

        held.Attempts++;
        var persisted = await _sessions.PersistHeldRequestAsync(held.Request, cancellationToken).ConfigureAwait(false);
        if (persisted)
        {
            _heldPersistence.TryRemove(stationId, out _);
            _status.UpsertStation(new StationRuntimeStatus
            {
                StationId = held.Request.Record.StationId,
                StationCode = held.Request.Record.StationCode,
                State = StationRuntimeState.Fault,
                LastPalletCode = held.Request.Record.PalletCode,
                LastSerialNo = held.Request.Record.SerialNo,
                LastResultCode = ResultCodes.DatabaseWriteFailed,
                LastJudgement = held.Request.Record.Judgement,
                LastCompleteTime = held.Request.Record.CompleteTime,
                LastDurationMs = held.Request.Record.DurationMs,
                LastError = observedTriggerValue == held.Request.ConfirmationTriggerValue
                    ? "原始采集请求已持久化；设备触发位仍处于触发值。已建立人工核对保护，未写响应码。"
                    : $"原始采集请求已持久化；当前触发位为 {observedTriggerValue}，与原触发值不一致。已建立人工核对保护，未写响应码。",
                LastMonthKey = held.Request.MonthKey,
                LastRecordId = held.Request.Record.Id,
                LastWriteBackOk = false
            });
            _logger.LogCritical("工站 {Station} 原始请求已持久化；当前 trigger={Trigger}；未向 PLC 写回，等待人工核对", stationId, observedTriggerValue);
            return;
        }

        var seconds = Math.Min(60, 2 << Math.Min(held.Attempts - 1, 5));
        held.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(seconds);
        var previous = _status.Stations.FirstOrDefault(x => x.StationId == stationId);
        if (previous is not null)
        {
            previous.State = StationRuntimeState.Fault;
            previous.LastError = $"月库与 spool 仍不可写；原流水 {held.Request.Record.SerialNo} 保存在内存保护队列，当前触发位读值 {observedTriggerValue}；{seconds} 秒后只重试持久化。";
            _status.UpsertStation(previous);
        }
    }

    private sealed class HeldPersistence(CollectSaveRequest request)
    {
        public CollectSaveRequest Request { get; } = request;
        public int Attempts { get; set; }
        public DateTime NextAttemptAtUtc { get; set; } = DateTime.UtcNow.AddSeconds(2);
    }

    /// <summary>
    /// 触发命中且留证尚未进入人工核对时，将它持久转为人工阻塞。
    /// 当前 PLC 协议缺少稳定事件号，软件不能证明触发位仍属于原件；这里绝不写旧结果或销账。
    /// </summary>
    public async Task<bool> RetryPendingConfirmationAsync(
        Station station,
        PlcConnection connection,
        AppConfigurationSnapshot config,
        PlcRequestQueue queue,
        PendingWriteBack pending,
        CancellationToken cancellationToken)
    {
        await using var operationScope = _scopeFactory.CreateAsyncScope();
        var barrier = operationScope.ServiceProvider.GetRequiredService<IDataMaintenanceBarrier>();
        await using var maintenanceLease = await barrier.EnterReadAsync(cancellationToken).ConfigureAwait(false);
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            [CollectionLogScope.Station] = station.Code,
            [CollectionLogScope.Trigger] = pending.SerialNo
        });

        // 当前驱动契约没有稳定、递增的设备事件号。会话是否 Open、托盘码是否相同、回写寄存器读回，
        // 都不能证明触发位仍对应这次采集（同托盘复用也无法区分）。因此所有未确认留证都进入
        // 持久人工核对状态，绝不把旧结果码写给未知的新件。
        if (!pending.ManualReviewRequired)
        {
            const string reason = "设备协议未提供可校验的稳定事件身份；请现场核对托盘/事件并处理触发位后人工解除";
            await _pending.RequireManualReviewAsync(pending.MonthKey, pending.Id, reason, CancellationToken.None)
                .ConfigureAwait(false);
            pending.ManualReviewRequired = true;
            pending.ManualReviewReason = reason;
            _logger.LogWarning(
                "工站 {Station} 留证 {Serial} 持久阻塞：设备事件身份不可验证，等待人工核对",
                station.Code, pending.SerialNo);
        }
        SetStatus(station, StationRuntimeState.Fault, pending.PalletCode, pending.SerialNo, pending.ResultCode,
            Judgement.None, pending.ManualReviewReason ?? "待人工核对设备事件身份", null,
            monthKey: pending.MonthKey, recordId: pending.CollectRecordId, writeBackOk: false);
        return false;
    }

    private static string DescribeSessionState(PalletSession? session)
        => session is null
            ? "不存在"
            : session.Status == SessionStatus.Closed
                ? $"已关闭（{session.EndTime:HH:mm:ss}）"
                : session.EndTime is not null
                    ? $"已结束（{session.EndTime:HH:mm:ss}）"
                    : session.Status.ToString();

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

        var frozen = station.IsFirstStation
            ? FrozenSelection.ForFirstStation(config, _baselines.Current)
            : await LookupFrozenSelectionAsync(config, palletCode, cancellationToken).ConfigureAwait(false);

        // 快照损坏不能静默换规则：宁可本站故障留证，由人核查后恢复。
        if (frozen.Kind == SessionRecipe.SelectionKind.Corrupted)
        {
            _logger.LogError("工站 {Station} {Error}", station.Code, frozen.Error);
            throw new InvalidDataException(frozen.Error!);
        }

        var evaluatedStation = station;
        var evaluatedConnection = connection;
        var evaluatedPlan = plan;
        var evaluatedBuffers = buffers;
        if (frozen.Kind == SessionRecipe.SelectionKind.Frozen && frozen.FrozenConfiguration is { } frozenConfig)
        {
            evaluatedStation = frozenConfig.Stations.FirstOrDefault(x => x.Id == station.Id)
                ?? throw new InvalidDataException("冻结配置中不存在当前工站，判定已阻塞");
            evaluatedConnection = frozenConfig.PlcConnections.FirstOrDefault(x => x.Id == evaluatedStation.PlcConnectionId)
                ?? throw new InvalidDataException("冻结配置中不存在工站 PLC，判定已阻塞");
            if (!SameEndpoint(connection, evaluatedConnection))
                throw new InvalidDataException("工站 PLC 品牌或连接端点已与在制快照不一致，无法安全读取历史点位，判定已阻塞");

            var historical = AcquisitionPlan(frozenConfig, evaluatedStation, evaluatedConnection, queue);
            evaluatedBuffers = await StationBlockReader.ReadAsync(historical, queue, cancellationToken).ConfigureAwait(false);
            var historicalPallet = StationSampleDecoder.PalletCode(evaluatedStation, evaluatedConnection, historical.Plan, evaluatedBuffers);
            if (!string.Equals(historicalPallet, palletCode, StringComparison.Ordinal))
                throw new InvalidDataException("当前 PLC 配置与在制快照解码出的托盘码不一致，判定已阻塞");
            evaluatedPlan = historical;
        }

        FileSourceRead? source = null;
        if (evaluatedStation.Tags.Any(t => t.Enabled && t.Source == TagDataSource.JsonFile))
        {
            source = await _files.ReadAsync(evaluatedStation, triggerTime, palletCode, cancellationToken).ConfigureAwait(false);
            if (source.Error is not null)
            {
                return Live(station, Unrecorded(station, config, palletCode, triggerTime, source.ErrorCode, source.Error));
            }
        }

        var assessment = CollectEvaluator.Evaluate(
            evaluatedStation, evaluatedConnection, evaluatedPlan.Plan, evaluatedBuffers, source, _baselines,
            frozen.Recipe, frozen.FrozenLimits, frozen.FrozenBaseline,
            baselineIsFrozen: frozen.Kind != SessionRecipe.SelectionKind.LegacyFallback);
        var saved = await _sessions.PersistAsync(
            evaluatedStation, config, palletCode, triggerTime, sw, source, assessment, frozen.FrozenBaseline,
            cancellationToken, station, connection, frozen.FrozenConfiguration).ConfigureAwait(false);
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
            var key = (config.Version, config.ConfigurationRevisionId, station.Id, connection.Id);
            if (_plans.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var compiled = StationAcquisitionPlanner.Compile(station, connection, queue.Driver);
            if (_plans.Count >= 256)
                _plans.Clear();
            _plans[key] = compiled;
            return compiled;
        }
    }

    private static bool SameEndpoint(PlcConnection current, PlcConnection frozen)
        => current.Id == frozen.Id
            && current.Brand == frozen.Brand
            && string.Equals(current.Host, frozen.Host, StringComparison.OrdinalIgnoreCase)
            && current.Port == frozen.Port;

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

    /// <summary>后续工站的判定限值来源：读在制索引的冻结快照并解析成选择结果。</summary>
    private async Task<FrozenSelection> LookupFrozenSelectionAsync(
        AppConfigurationSnapshot config,
        string palletCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sessions = scope.ServiceProvider.GetRequiredService<IActiveSessionStore>();
            var existing = await sessions.FindByPalletAsync(palletCode, cancellationToken).ConfigureAwait(false);
            return existing is null
                ? FrozenSelection.ForFirstStation(config)
                : SessionRecipe.SelectFrozen(config, firstStation: false, existing.RecipeCode, existing.RecipeSnapshot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 读取在制型号失败绝不能静默改用当前型号：那等于在配置库故障时
            // 用一套没人知道的规则给在制件判定、落库。宁可本站故障留证，
            // 由人核对后重采/补录 —— 配置库恢复后下一轮自然恢复。
            _logger.LogError(ex, "读取在制型号失败（托盘 {Pallet}），本件判定阻塞", palletCode);
            throw new InvalidOperationException("读取在制型号失败，判定已阻塞：请检查配置数据库后重试", ex);
        }
    }

    /// <summary>响应码只做一次写入及读回核对；失败后保留留证，等待人工处理。</summary>
    private readonly record struct WriteBackOutcome(int Attempts, bool Ok, string? Error);

    private async Task<WriteBackOutcome> WriteResultAsync(
        Station station,
        PlcRequestQueue queue,
        short resultCode)
    {
        if (!queue.Driver.TryParseAddress(station.TriggerAddress, out var address))
        {
            _logger.LogError("工站 {Station} 触发地址无法解析: {Address}", station.Code, station.TriggerAddress);
            return new WriteBackOutcome(0, false, "触发地址无法解析");
        }

        if (address.IsBit)
        {
            _logger.LogError("工站 {Station} 触发地址是位地址，响应码无法写回: {Address}", station.Code, station.TriggerAddress);
            return new WriteBackOutcome(0, false, "触发地址是位地址");
        }

        try
        {
            // A lost acknowledgement can mean the write took effect on the old part. Never resend
            // a result code: a later trigger may belong to a different event even on the same pallet.
            await queue.WriteWordsAsync(address, ValueCodec.EncodeInt16(resultCode), CancellationToken.None)
                .ConfigureAwait(false);
            var readBack = await queue.ReadWordsAsync(address, 1, CancellationToken.None).ConfigureAwait(false);
            if (readBack.Length > 0 && (short)readBack[0] == resultCode)
                return new WriteBackOutcome(1, true, null);

            var mismatch = new PlcDriverException("单次写入后读回不一致；为避免覆盖新事件，不自动重写");
            _logger.LogError(mismatch, "工站 {Station} 响应码单次写入后核对失败", station.Code);
            return new WriteBackOutcome(1, false, mismatch.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工站 {Station} 响应码单次写入/核对失败；不自动重写", station.Code);
            return new WriteBackOutcome(1, false, ex.Message);
        }
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
        long? recordId = null,
        int? writeBackAttempts = null,
        bool? writeBackOk = null)
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
            // 开跑时（Busy 那次调用）不传，于是沿用上一件的回写结果：
            // 采集途中把"上次回写重试 3 次"抹成 0，等于让人以为链路一直很健康。
            LastWriteBackAttempts = writeBackAttempts ?? previous?.LastWriteBackAttempts ?? 0,
            LastWriteBackOk = writeBackOk ?? previous?.LastWriteBackOk ?? false,
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
