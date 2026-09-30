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
        var writtenFiles = new List<string>();
        var databaseCommitted = false;
        try
        {
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

            await using var db = _factory.Open(request.MonthKey);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

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

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            databaseCommitted = true;

            if (request.RemoveActiveSession)
            {
                await _activeSessions.RemoveByPalletAsync(request.Record.PalletCode, cancellationToken).ConfigureAwait(false);
            }
            else if (request.ActiveSession is not null)
            {
                request.ActiveSession.SessionId = request.Record.PalletSessionId;
                await _activeSessions.UpsertAsync(request.ActiveSession, cancellationToken).ConfigureAwait(false);
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

