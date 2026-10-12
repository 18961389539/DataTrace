using System.Diagnostics;
using DataTrace.Application.Alarms;
using DataTrace.Application.Configuration;
using DataTrace.Application.Evaluation;
using DataTrace.Application.Mes;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DataTrace.Collector;

public sealed record PersistedCollect(
    CollectRecord Record,
    IReadOnlyList<CurvePayloadWrite> Curves,
    string MonthKey,
    short? ConfirmationResultCode = null);

public sealed class CollectPersistenceUnavailableException : IOException
{
    public CollectPersistenceUnavailableException(CollectSaveRequest request, Exception innerException)
        : base("月库和本地 spool 均未能持久化本次采集", innerException) => Request = request;

    public CollectSaveRequest Request { get; }
}

/// <summary>会话、落库、补传和 MES 出站。判定结果已经算完。</summary>
public sealed class CollectSessionCoordinator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    public CollectSessionCoordinator(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task<PersistedCollect> PersistAsync(
        Station station,
        AppConfigurationSnapshot config,
        string palletCode,
        DateTime triggerTime,
        Stopwatch sw,
        FileSourceRead? source,
        CollectAssessment assessment,
        CancellationToken cancellationToken)
        => PersistAsync(station, config, palletCode, triggerTime, sw, source, assessment, null, cancellationToken);

    public async Task<PersistedCollect> PersistAsync(
        Station station,
        AppConfigurationSnapshot config,
        string palletCode,
        DateTime triggerTime,
        Stopwatch sw,
        FileSourceRead? source,
        CollectAssessment assessment,
        CurveBaselineSnapshot? baseline,
        CancellationToken cancellationToken,
        Station? confirmationStation = null,
        PlcConnection? confirmationConnection = null,
        AppConfigurationSnapshot? sessionDefinition = null,
        long? deviceEventId = null)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IActiveSessionStore>();
        var serials = scope.ServiceProvider.GetRequiredService<ISerialNumberGenerator>();
        var writer = scope.ServiceProvider.GetRequiredService<ICollectWriter>();
        var query = scope.ServiceProvider.GetRequiredService<ICollectQuery>();

        string serialNo;
        string monthKey;
        PalletSession? upsertSession = null;
        ActiveSessionIndex? active = null;
        long existingSessionId = 0;
        var close = false;
        var removeActive = false;
        var processAbnormal = false;
        string? processError = null;
        string? frozenRecipe = null;

        if (station.IsFirstStation)
        {
            var existing = await sessions.FindByPalletAsync(palletCode, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await writer.MarkSessionAbnormalAsync(existing.MonthKey, existing.SessionId, DateTime.Now, cancellationToken)
                    .ConfigureAwait(false);
                await sessions.RemoveByPalletAsync(palletCode, cancellationToken).ConfigureAwait(false);
                await NoteSupersededAsync(scope.ServiceProvider, palletCode, existing, cancellationToken).ConfigureAwait(false);
            }

            serialNo = await serials.NextAsync(triggerTime, cancellationToken).ConfigureAwait(false);
            monthKey = PersistenceMonth(triggerTime);
            upsertSession = new PalletSession
            {
                SerialNo = serialNo,
                PalletCode = palletCode,
                StartTime = triggerTime,
                Status = SessionStatus.Open,
                RecipeSnapshot = SessionRecipe.Snapshot(config, baseline)
            };
            active = new ActiveSessionIndex
            {
                PalletCode = palletCode,
                SerialNo = serialNo,
                MonthKey = monthKey,
                StartTime = triggerTime,
                RecipeCode = config.ActiveRecipe?.Code ?? "",
                // 首站固化当时的判定依据：全线启用点位的生效限值（默认 + 型号覆盖解析结果）
                // 加型号身份。没选型号也是一套明确的冻结状态，中途选型/改限值都不影响已开件。
                RecipeSnapshot = upsertSession.RecipeSnapshot
            };
        }
        else
        {
            var existing = await sessions.FindByPalletAsync(palletCode, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                processAbnormal = true;
                processError = "未找到在制托盘会话（可能跳站）";
                // 孤儿流水号必须自带唯一性：SerialNo 有唯一索引，多个孤儿托盘同秒触发
                // 若只有秒级时间戳，第二条会撞库失败且重试永远撞同一个号，
                // 还可能阻塞随后正常件的会话落库。毫秒 + 工站 + 随机段三重区分，
                // ORPHAN 前缀保留给查询/展示的异常标识语义。
                serialNo = $"ORPHAN-{triggerTime:yyyyMMddHHmmssfff}-{station.Id}-{Guid.NewGuid().ToString("N")[..6]}";
                monthKey = PersistenceMonth(triggerTime);
                upsertSession = new PalletSession
                {
                    SerialNo = serialNo,
                    PalletCode = palletCode,
                    StartTime = triggerTime,
                    Status = SessionStatus.Abnormal
                };
                active = new ActiveSessionIndex
                {
                    PalletCode = palletCode,
                    SerialNo = serialNo,
                    MonthKey = monthKey,
                    StartTime = triggerTime
                };
            }
            else
            {
                serialNo = existing.SerialNo;
                monthKey = existing.MonthKey;
                existingSessionId = existing.SessionId;
                frozenRecipe = existing.RecipeCode;
                active = new ActiveSessionIndex
                {
                    PalletCode = existing.PalletCode,
                    SerialNo = existing.SerialNo,
                    SessionId = existing.SessionId,
                    MonthKey = existing.MonthKey,
                    StartTime = existing.StartTime,
                    RecipeCode = SessionRecipe.RecordCodeForSession(existing.RecipeCode, existing.RecipeSnapshot, config.ActiveRecipe),
                    RecipeSnapshot = existing.RecipeSnapshot
                };
                var previous = (sessionDefinition ?? config).Stations
                    .Where(s => s.Enabled && s.Sequence < station.Sequence)
                    .Select(s => s.Id)
                    .ToList();
                if (previous.Count > 0)
                {
                    var history = await query.GetSessionRecordsAsync(existing.MonthKey, existing.SessionId, cancellationToken)
                        .ConfigureAwait(false);
                    var visited = history.Select(h => h.StationId).ToHashSet();
                    if (previous.Any(id => !visited.Contains(id)))
                    {
                        processAbnormal = true;
                        processError = "检测到跳站";
                    }
                }
            }
        }

        if (station.IsLastStation)
        {
            close = true;
            removeActive = true;
        }

        var recordJudgement = LimitEvaluator.Combine(assessment.Products.Where(p => p.Occupied).Select(p => p.Judgement));
        short result = ResultCodes.Success;
        if (assessment.ValidationError)
        {
            result = ResultCodes.DataValidationFailed;
            recordJudgement = Judgement.Ng;
        }
        else if (processAbnormal)
        {
            result = ResultCodes.ProcessAbnormal;
            recordJudgement = Judgement.Ng;
        }
        else if (assessment.QualityRejected || recordJudgement == Judgement.Ng)
        {
            result = ResultCodes.QualityRejected;
        }

        var record = new CollectRecord
        {
            PalletSessionId = existingSessionId,
            SerialNo = serialNo,
            PalletCode = palletCode,
            StationId = station.Id,
            StationCode = station.Code,
            TriggerTime = triggerTime,
            CompleteTime = DateTime.Now,
            DurationMs = (int)sw.ElapsedMilliseconds,
            ResultCode = result,
            Judgement = recordJudgement,
            ErrorMessage = assessment.ValidationMessage ?? processError,
            RecipeCode = station.IsFirstStation
                ? SessionRecipe.RecordCode(true, frozenRecipe, config.ActiveRecipe)
                : SessionRecipe.RecordCodeForSession(frozenRecipe, active?.RecipeSnapshot, config.ActiveRecipe),
            ArchivePath = source?.ArchivePath ?? "",
            ArchiveFileSize = source?.ArchiveSize ?? 0,
            ArchiveCrc32 = source?.ArchiveCrc ?? 0,
            Products = assessment.Products,
            TagValues = assessment.TagValues
        };
        if (active is not null)
        {
            active.RecipeCode = record.RecipeCode;
            active.LastStationId = station.Id;
            active.LastStationCode = station.Code;
            active.LastActivityAt = record.CompleteTime;
        }

        MesOutboxIntent? mesOutbox = null;
        if (station.IsLastStation && config.Settings.MesEnabled
            && result is ResultCodes.Success or ResultCodes.QualityRejected)
        {
            var rejects = await RejectsAsync(query, monthKey, record, cancellationToken).ConfigureAwait(false);
            mesOutbox = new MesOutboxIntent
            {
                EventId = Guid.NewGuid().ToString("N"),
                MonthKey = monthKey,
                PalletSessionId = record.PalletSessionId,
                SerialNo = serialNo,
                PalletCode = palletCode,
                CreatedAt = record.CompleteTime,
                PayloadJson = MesPayload.Build(
                    serialNo,
                    palletCode,
                    station.Code,
                    record.RecipeCode,
                    recordJudgement.ToString(),
                    record.CompleteTime,
                    rejects)
            };
        }

        var save = new CollectSaveRequest
        {
            MonthKey = monthKey,
            UpsertSession = upsertSession,
            CloseSession = close,
            Record = record,
            Curves = assessment.Curves,
            MesOutbox = mesOutbox,
            ConfigurationRevisionId = (sessionDefinition ?? config).ConfigurationRevisionId,
            ActiveSession = active,
            RemoveActiveSession = removeActive,
            ConfirmationPlcConnectionId = (confirmationStation ?? station).PlcConnectionId,
            ConfirmationTriggerAddress = (confirmationStation ?? station).TriggerAddress,
            ConfirmationTriggerValue = (confirmationStation ?? station).TriggerValue,
            ConfirmationDeviceEventId = deviceEventId,
            ConfirmationEventIdAddress = (confirmationStation ?? station).EventIdAddress,
            ConfirmationRequestAddress = (confirmationStation ?? station).ConfirmationRequestAddress,
            ConfirmationAckAddress = (confirmationStation ?? station).ConfirmationAckAddress,
            ConfirmationConnectionFingerprint = confirmationConnection is { } exactPlc
                ? ConfirmationTargetFingerprint.Of(exactPlc)
                : config.PlcConnections.FirstOrDefault(x => x.Id == (confirmationStation ?? station).PlcConnectionId) is { } sourcePlc
                ? ConfirmationTargetFingerprint.Of(sourcePlc)
                : null
        };
        var confirmationResultCode = save.Record.ResultCode;

        // 流水号到这一刻才定下来（首站是刚生成的，其余站取自 在制会话），所以内层作用域从这里开始：
        // 落库与补传的每一行日志都带上它，"这件到底采上没采上、后来补上没有"就不用再靠时间戳猜了。
        // 外层作用域（工站 + 触发时刻）由 StationCollectPipeline 开着，两层会叠加渲染。
        using var pieceScope = _logger.BeginScope(new Dictionary<string, object>
        {
            [CollectionLogScope.Serial] = serialNo
        });

        try
        {
            await writer.SaveAsync(save, cancellationToken).ConfigureAwait(false);
        }
        catch (PostCommitStateUpdateException ex)
        {
            try
            {
                var spool = scope.ServiceProvider.GetRequiredService<ISpoolStore>();
                var spoolFile = await spool.SaveAsync(save, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(ex, "工站 {Station} 记录已入库（{Serial}），会话索引更新失败，已转存 {SpoolFile} 待补传", station.Code, serialNo, spoolFile);
            }
            catch (Exception spoolError) when (spoolError is not OperationCanceledException)
            {
                _logger.LogError(spoolError, "工站 {Station} 月库提交后修复及 spool 持久化均失败（{Serial}）", station.Code, serialNo);
                throw new CollectPersistenceUnavailableException(save, new AggregateException(ex, spoolError));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工站 {Station} 写库失败（{Serial}），转入本地缓存", station.Code, serialNo);
            string spoolFile;
            try
            {
                var spool = scope.ServiceProvider.GetRequiredService<ISpoolStore>();
                // 文件名同时记进日志：补传那边只有文件名，"写库失败"与"补传成功/失败"这两行
                // 除了它再没有别的共同字段，事后想对上只能靠时间戳。
                spoolFile = await spool.SaveAsync(save, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception spoolError) when (spoolError is not OperationCanceledException)
            {
                _logger.LogError(spoolError, "工站 {Station} 月库与 spool 持久化均失败（{Serial}）", station.Code, serialNo);
                throw new CollectPersistenceUnavailableException(save, new AggregateException(ex, spoolError));
            }
            _logger.LogWarning("已转存本地缓存 {SpoolFile}（{Serial}），补传循环会重试", spoolFile, serialNo);
            record.ResultCode = ResultCodes.DatabaseWriteFailed;
            record.ErrorMessage = ex.Message;
            return new PersistedCollect(record, assessment.Curves, monthKey, confirmationResultCode);
        }

        return new PersistedCollect(record, assessment.Curves, monthKey, confirmationResultCode);
    }

    /// <summary>
    /// Retry persistence for the exact request captured before a dual-storage failure.
    /// This method never reacquires PLC data and never writes a result code.
    /// </summary>
    public async Task<bool> PersistHeldRequestAsync(CollectSaveRequest request, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var barrier = scope.ServiceProvider.GetRequiredService<IDataMaintenanceBarrier>();
        await using var lease = await barrier.EnterReadAsync(cancellationToken).ConfigureAwait(false);
        var query = scope.ServiceProvider.GetRequiredService<ICollectQuery>();
        var writer = scope.ServiceProvider.GetRequiredService<ICollectWriter>();
        var spool = scope.ServiceProvider.GetRequiredService<ISpoolStore>();
        var pending = scope.ServiceProvider.GetRequiredService<IPendingWriteBackStore>();

        request.RequiresWriteBack = true;
        request.RequiresManualReviewOnPersistence = true;
        try
        {
            if (await query.ExistsCollectAsync(request.MonthKey, request.Record.SerialNo,
                    request.Record.StationId, request.Record.TriggerTime, cancellationToken).ConfigureAwait(false))
            {
                if (request.Record.Id <= 0)
                {
                    request.Record.Id = await query.FindCollectRecordIdAsync(request.MonthKey, request.Record.SerialNo,
                        request.Record.StationId, request.Record.TriggerTime, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("已检测到采集记录，但无法恢复其数据库 ID；保持保护并等待重试");
                }
                await writer.RepairPostCommitStateAsync(request, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await writer.SaveAsync(request, cancellationToken).ConfigureAwait(false);
            }

            await pending.RequireManualReviewByRecordIdAsync(request.MonthKey, request.Record.Id,
                "月库与本地缓存曾同时不可用；设备事件身份未经确认，等待现场人工核对",
                cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (Exception databaseError) when (databaseError is not OperationCanceledException)
        {
            try
            {
                await spool.SaveAsync(request, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception spoolError) when (spoolError is not OperationCanceledException)
            {
                _logger.LogError(new AggregateException(databaseError, spoolError),
                    "原始采集请求持久化重试仍失败，流水 {Serial} 保留在内存保护队列", request.Record.SerialNo);
                return false;
            }
        }
    }

    private static async Task<IReadOnlyList<MesRejectPoint>> RejectsAsync(
        ICollectQuery query,
        string monthKey,
        CollectRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            if (record.PalletSessionId > 0)
            {
                var history = await query.GetSessionRecordsAsync(monthKey, record.PalletSessionId, cancellationToken)
                    .ConfigureAwait(false);
                if (history.Count > 0)
                {
                    if (!history.Any(item => item.StationId == record.StationId && item.TriggerTime == record.TriggerTime))
                    {
                        history = history.Append(record).ToList();
                    }

                    return MesPayload.FromRecords(history);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 历史读不到时仍把这一站已经算好的判废点送出去。
        }

        return MesPayload.FromRecords([record]);
    }

    private async Task NoteSupersededAsync(
        IServiceProvider services,
        string palletCode,
        ActiveSessionIndex abandoned,
        CancellationToken cancellationToken)
    {
        try
        {
            var incidents = services.GetRequiredService<IAlarmIncidents>();
            var serial = string.IsNullOrWhiteSpace(abandoned.SerialNo) ? "（无序列号）" : abandoned.SerialNo.Trim();
            await incidents.RaiseAsync(
                    new LineAlarm(
                        LineAlarmKeys.SessionSuperseded(abandoned.SessionId),
                        LineAlarmKind.SessionSuperseded,
                        $"托盘 {palletCode} 还没走完末站，又从首站进入。上一件 {serial} 已标为异常。"),
                    DateTime.Now,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "托盘 {Pallet} 被下一件盖掉，报警没能记下", palletCode);
        }
    }

    private static string PersistenceMonth(DateTime time) => time.ToString("yyyyMM");
}
