using System.Text.Json;
using DataTrace.Application.Alarms;
using DataTrace.Application.Reporting;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

// 按职责拆成 partial 多文件：写入本文件，查询见 RuntimeStore.Queries.cs，
// 报表/统计见 RuntimeStore.Analytics.cs，按月保留见 RuntimeStore.Retention.cs。
public sealed partial class RuntimeStore : IRuntimeStore
{
    private static readonly JsonSerializerOptions IndexIntentJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RuntimeDbFactory _factory;

    private readonly ICurveFileStore _curves;

    private readonly IActiveSessionStore _activeSessions;

    public RuntimeStore(RuntimeDbFactory factory, ICurveFileStore curves, IActiveSessionStore activeSessions)
    {
        _factory = factory;
        _curves = curves;
        _activeSessions = activeSessions;
    }

    public async Task SaveAsync(CollectSaveRequest request, CancellationToken cancellationToken = default)
    {
        request.Record.ConfigurationRevisionId ??= request.ConfigurationRevisionId;
        var writtenFiles = new List<string>();
        var databaseCommitted = false;
        try
        {
            var curveSw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var curve in request.Curves)
            {
                var written = await _curves.WriteAsync(
                    request.Record.TriggerTime,
                    request.Record.SerialNo,
                    request.Record.StationId,
                    curve.Record.PositionIndex,
                    curve.Record.CurveCode,
                    curve.Payload,
                    cancellationToken).ConfigureAwait(false);
                curve.Record.RelativePath = written.RelativePath;
                curve.Record.FileSize = written.FileSize;
                curve.Record.Crc32 = written.Crc32;
                writtenFiles.Add(written.RelativePath);
            }

            PersistenceMetrics.RecordCurveWrite(curveSw.Elapsed.TotalMilliseconds, request.Curves.Count);

            await using var db = _factory.Open(request.MonthKey);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var txSw = System.Diagnostics.Stopwatch.StartNew();

            request.Record.PalletSession = null;
            if (request.UpsertSession is not null)
            {
                if (request.UpsertSession.Id == 0)
                {
                    request.Record.PalletSession = request.UpsertSession;
                }
                else
                {
                    var existing = await db.PalletSessions.FindAsync([request.UpsertSession.Id], cancellationToken)
                        .ConfigureAwait(false);
                    if (existing is not null)
                    {
                        existing.Status = request.UpsertSession.Status;
                        existing.EndTime = request.UpsertSession.EndTime;
                        existing.Judgement = request.UpsertSession.Judgement;
                        existing.RecipeSnapshot ??= request.UpsertSession.RecipeSnapshot;
                    }

                    request.Record.PalletSessionId = request.UpsertSession.Id;
                }
            }

            request.Record.Curves = request.Curves.Select(c => c.Record).ToList();
            // 特征行随曲线记录级联写入，与曲线文件同属一次采集的产物。
            foreach (var write in request.Curves)
            {
                if (write.Features.Count > 0)
                {
                    write.Record.Features = write.Features.ToList();
                }
            }

            db.CollectRecords.Add(request.Record);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // 在制索引更新意图与业务数据同事务落库：commit 之后、config.db 索引更新之前
            // 硬退出（断电/杀进程）不会走任何 catch，索引会永久错位。有了这条意图，
            // 重启后的对账器能重放这次索引更新，窗口收敛为"索引晚一点追上"。
            // 放在首次 SaveChanges 之后：新会话（首站）的 PalletSessionId 要等它生成；
            // ActiveSession.SessionId 也要在这里填上 —— 它原本要到提交后的 UpdateActiveSessionAsync
            // 才赋值，序列化进载荷的就会是 0，重放时查不到会话、意图被误作废。
            SessionIndexIntent? indexIntent = null;
            if (request.RemoveActiveSession || request.ActiveSession is not null)
            {
                if (request.ActiveSession is not null)
                {
                    request.ActiveSession.SessionId = request.Record.PalletSessionId;
                }

                indexIntent = new SessionIndexIntent
                {
                    PalletCode = request.Record.PalletCode,
                    SerialNo = request.Record.SerialNo,
                    MonthKey = request.MonthKey,
                    Operation = request.RemoveActiveSession ? 1 : 0,
                    PayloadJson = request.ActiveSession is null
                        ? ""
                        : JsonSerializer.Serialize(request.ActiveSession, IndexIntentJsonOptions),
                    CreatedAt = DateTime.Now
                };
                db.SessionIndexIntents.Add(indexIntent);
            }

            // 提交即登记"待回写确认"（与业务数据同事务）：回写发生在落库之后，
            // 留证晚于提交的话，提交后到留证前的硬退出会让同一件被重新采集 ——
            // 首站把旧会话标异常、新建流水，其他站新增重复记录。登记后回写成功销账
            // （StationCollectPipeline.finally）；协议没有事件号时，后续会转持久人工核对，
            // 不会自动把旧结果重写到可能已变化的触发事件。
            if (request.RequiresWriteBack)
            {
                db.PendingWriteBacks.Add(new PendingWriteBack
                {
                    CollectRecordId = request.Record.Id,
                    PalletCode = request.Record.PalletCode,
                    SerialNo = request.Record.SerialNo,
                    StationId = request.Record.StationId,
                    StationCode = request.Record.StationCode,
                    MonthKey = request.MonthKey,
                    PalletSessionId = request.Record.PalletSessionId,
                    ResultCode = request.Record.ResultCode,
                    PlcConnectionId = request.ConfirmationPlcConnectionId,
                    TriggerAddress = request.ConfirmationTriggerAddress,
                    TriggerValue = request.ConfirmationTriggerValue,
                    ConnectionFingerprint = request.ConfirmationConnectionFingerprint,
                    CreatedAt = DateTime.Now,
                    Attempts = 0,
                    NextAttemptAt = DateTime.Now,
                    ManualReviewRequired = request.RequiresManualReviewOnPersistence,
                    ManualReviewRequiredAt = request.RequiresManualReviewOnPersistence ? DateTime.Now : null,
                    ManualReviewReason = request.RequiresManualReviewOnPersistence
                        ? "月库与本地缓存曾同时不可用；设备事件身份未经确认，等待现场人工核对"
                        : null
                });
            }

            if (request.MesOutbox is not null)
            {
                request.MesOutbox.PalletSessionId = request.Record.PalletSessionId;
                db.MesOutboxIntents.Add(request.MesOutbox);
            }

            if (request.CloseSession && request.Record.PalletSessionId != 0)
            {
                var session = await db.PalletSessions.FindAsync([request.Record.PalletSessionId], cancellationToken)
                    .ConfigureAwait(false);
                if (session is not null)
                {
                    session.Status = SessionStatus.Closed;
                    session.EndTime = request.Record.CompleteTime;
                    var records = await db.CollectRecords.AsNoTracking()
                        .Where(record => record.PalletSessionId == session.Id)
                        .OrderBy(record => record.TriggerTime)
                        .ThenBy(record => record.Id)
                        .Select(record => new
                        {
                            record.Id,
                            record.Judgement,
                            record.StationCode,
                            record.TriggerTime,
                            record.ResultCode,
                            record.ErrorMessage
                        })
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    session.Judgement = CombineSessionJudgements(records.Select(record => record.Judgement).ToList());

                    // 判定是"任一站 NG 即 NG"，光有这个判定，事后只知道废了、不知道从哪一站开始废。
                    // 首因钉在最前面那条 NG 记录上：先按触发时刻，再按 Id 定先后。
                    var firstNg = records.FirstOrDefault(record => record.Judgement == Judgement.Ng);
                    if (firstNg is null)
                    {
                        session.FirstNgStationCode = null;
                        session.FirstNgAt = null;
                        session.FirstNgResultCode = null;
                        session.FirstNgReason = null;
                    }
                    else
                    {
                        var reasons = await db.ProductRecords.AsNoTracking()
                            .Where(product => product.CollectRecordId == firstNg.Id
                                              && product.Judgement == Judgement.Ng
                                              && product.NgReason != null
                                              && product.NgReason != "")
                            .OrderBy(product => product.PositionIndex)
                            .Select(product => product.NgReason!)
                            .ToListAsync(cancellationToken)
                            .ConfigureAwait(false);
                        session.FirstNgStationCode = firstNg.StationCode;
                        session.FirstNgAt = firstNg.TriggerTime;
                        session.FirstNgResultCode = firstNg.ResultCode;
                        session.FirstNgReason = ComposeFirstNgReason(firstNg.ErrorMessage, reasons);
                    }
                }
            }

            // 合并提交（性能审查 P2）：意图、留证、MES、会话收尾都只依赖首次 SaveChanges
            // 生成的 Id（Record.Id / PalletSessionId），同事务里合成一次批量写入 ——
            // 最坏情况（末站 + 意图 + 留证 + MES）从 4 次往返降到 2 次。
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            PersistenceMetrics.RecordDbCommit(txSw.Elapsed.TotalMilliseconds);
            databaseCommitted = true;

            try
            {
                await UpdateActiveSessionAsync(request, replay: false, cancellationToken).ConfigureAwait(false);
                // 索引更新成功，意图完成使命。删除失败无害：重放时会按相同条件判定为已完成/作废。
                if (indexIntent is not null)
                {
                    await DeleteIndexIntentAsync(request.MonthKey, indexIntent.Id, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                throw new PostCommitStateUpdateException(ex);
            }
        }
        catch
        {
            if (databaseCommitted)
            {
                throw;
            }

            // 曲线文件必须由写它的那个存储来删：只有它知道自己的根目录。
            // 以前这里自己拼 cwd + "data/curves"，Windows 服务的工作目录是 System32，
            // 而且 DataRoot 也可能指到别处，两种情况下回滚都静默失效、留下孤儿文件。
            // 删的必须是 WriteAsync 返回的路径：它撞名会让开一格，因此只会删掉本次写的那个文件，
            // 不会连带删掉之前记录在同名路径上的波形。
            foreach (var relative in writtenFiles)
            {
                try
                {
                    await _curves.DeleteFileAsync(relative, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // 孤儿文件由清理任务处理
                }
            }

            throw;
        }
    }

    public Task RepairPostCommitStateAsync(CollectSaveRequest request, CancellationToken cancellationToken = default)
        => UpdateActiveSessionAsync(request, replay: true, cancellationToken);

    /// <summary>索引更新成功后删除已完成的意图；重放对账见 <c>SessionIndexRelay</c>。</summary>
    private async Task DeleteIndexIntentAsync(string monthKey, long intentId, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = _factory.Open(monthKey);
            var intent = await db.SessionIndexIntents.FirstOrDefaultAsync(x => x.Id == intentId, cancellationToken)
                .ConfigureAwait(false);
            if (intent is not null)
            {
                db.SessionIndexIntents.Remove(intent);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 意图删不掉只会让对账器多做一次幂等重放，不影响业务正确性，不值得让本次保存报错。
        }
    }

    private async Task UpdateActiveSessionAsync(CollectSaveRequest request, bool replay, CancellationToken cancellationToken)
    {
        if (!request.RemoveActiveSession && request.ActiveSession is null)
        {
            return;
        }

        if (replay)
        {
            var current = await _activeSessions.FindByPalletAsync(request.Record.PalletCode, cancellationToken).ConfigureAwait(false);
            if (current is not null && current.SerialNo != request.Record.SerialNo)
            {
                return;
            }

            if (request.RemoveActiveSession && current is null)
            {
                return;
            }

            if (!request.RemoveActiveSession)
            {
                await using var db = _factory.Open(request.MonthKey);
                var session = await db.PalletSessions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == request.Record.PalletSessionId, cancellationToken).ConfigureAwait(false);
                if (session is null || session.Status == SessionStatus.Closed || session.EndTime is not null
                    || current?.LastActivityAt > request.ActiveSession!.LastActivityAt)
                {
                    return;
                }
            }
        }

        if (request.RemoveActiveSession)
        {
            await _activeSessions.RemoveByPalletAsync(request.Record.PalletCode, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            request.ActiveSession!.SessionId = request.Record.PalletSessionId;
            await _activeSessions.UpsertAsync(request.ActiveSession, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task MarkSessionAbnormalAsync(string monthKey, long sessionId, DateTime endTime, CancellationToken cancellationToken = default)
    {
        if (!_factory.Exists(monthKey))
        {
            return;
        }

        await using var db = _factory.Open(monthKey);
        var session = await db.PalletSessions.FindAsync([sessionId], cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return;
        }

        session.Status = SessionStatus.Abnormal;
        session.EndTime = endTime;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Judgement CombineSessionJudgements(IReadOnlyCollection<Judgement> judgements)
    {
        if (judgements.Count == 0 || judgements.All(judgement => judgement == Judgement.None))
        {
            return Judgement.None;
        }

        return judgements.Any(judgement => judgement == Judgement.Ng)
            ? Judgement.Ng
            : Judgement.Ok;
    }

    /// <summary>
    /// 首因说明：先记采集/流程错误（跳站、校验失败都在这里），再接各不良品位的判定原因，
    /// 去重后拼成一句。不去重的话，一件上多个同样的不良原因会原样重复好几遍。
    /// 不做截断：这是判定时刻的审计事实，显示端要省略是显示端的事。
    /// </summary>
    private static string? ComposeFirstNgReason(string? errorMessage, IReadOnlyList<string> productReasons)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            parts.Add(errorMessage.Trim());
        }

        foreach (var reason in productReasons)
        {
            var text = reason.Trim();
            if (text.Length > 0 && !parts.Contains(text))
            {
                parts.Add(text);
            }
        }

        return parts.Count == 0 ? null : string.Join("；", parts);
    }
}

