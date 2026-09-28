using System.Diagnostics;
using DataTrace.Application.Alarms;
using DataTrace.Application.Configuration;
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
    string MonthKey);

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

    public async Task<PersistedCollect> PersistAsync(
        Station station,
        AppConfigurationSnapshot config,
        string palletCode,
        DateTime triggerTime,
        Stopwatch sw,
        FileSourceRead? source,
        CollectAssessment assessment,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IActiveSessionStore>();
        var serials = scope.ServiceProvider.GetRequiredService<ISerialNumberGenerator>();
        var writer = scope.ServiceProvider.GetRequiredService<ICollectWriter>();
        var query = scope.ServiceProvider.GetRequiredService<ICollectQuery>();
        var mes = scope.ServiceProvider.GetRequiredService<IMesPublisher>();

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
                Status = SessionStatus.Open
            };
            active = new ActiveSessionIndex
            {
                PalletCode = palletCode,
                SerialNo = serialNo,
                MonthKey = monthKey,
                StartTime = triggerTime,
                RecipeCode = config.ActiveRecipe?.Code ?? ""
            };
        }
        else
        {
            var existing = await sessions.FindByPalletAsync(palletCode, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                processAbnormal = true;
                processError = "未找到在制托盘会话（可能跳站）";
                serialNo = $"ORPHAN-{triggerTime:yyyyMMddHHmmss}";
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
                    RecipeCode = SessionRecipe.RecordCode(false, existing.RecipeCode, config.ActiveRecipe)
                };
                var previous = config.Stations
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
            RecipeCode = SessionRecipe.RecordCode(station.IsFirstStation, frozenRecipe, config.ActiveRecipe),
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

        var save = new CollectSaveRequest
        {
            MonthKey = monthKey,
            UpsertSession = upsertSession,
            CloseSession = close,
            Record = record,
            Curves = assessment.Curves,
            ActiveSession = active,
            RemoveActiveSession = removeActive
        };

        try
        {
            await writer.SaveAsync(save, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工站 {Station} 写库失败，转入本地缓存", station.Code);
            var spool = scope.ServiceProvider.GetRequiredService<ISpoolStore>();
            await spool.SaveAsync(save, cancellationToken).ConfigureAwait(false);
            record.ResultCode = ResultCodes.DatabaseWriteFailed;
            record.ErrorMessage = ex.Message;
            return new PersistedCollect(record, assessment.Curves, monthKey);
        }

        if (station.IsLastStation && config.Settings.MesEnabled
            && result is ResultCodes.Success or ResultCodes.QualityRejected)
        {
            var rejects = await RejectsAsync(query, monthKey, record, cancellationToken).ConfigureAwait(false);
            var payload = MesPayload.Build(
                serialNo,
                palletCode,
                station.Code,
                record.RecipeCode,
                recordJudgement.ToString(),
                record.CompleteTime,
                rejects);
            await mes.EnqueueSessionAsync(monthKey, record.PalletSessionId, serialNo, palletCode, payload, cancellationToken)
                .ConfigureAwait(false);
        }

        return new PersistedCollect(record, assessment.Curves, monthKey);
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
