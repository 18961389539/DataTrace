using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

public sealed class RuntimeStore : IRuntimeStore
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
                    session.Judgement = request.Record.Judgement;
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

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
            foreach (var relative in writtenFiles)
            {
                try
                {
                    var full = Path.Combine(
                        Directory.GetCurrentDirectory(),
                        "data",
                        "curves",
                        relative.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(full))
                    {
                        File.Delete(full);
                    }
                }
                catch
                {
                    // 孤儿文件由清理任务处理
                }
            }

            throw;
        }
    }

    public async Task<CollectQueryResult> QueryAsync(CollectQueryRequest request, CancellationToken cancellationToken = default)
    {
        var months = RuntimeDbFactory.MonthsInRange(request.From, request.To)
            .Where(_factory.Exists)
            .OrderByDescending(x => x)
            .ToList();

        var counts = new List<(string Month, int Count)>();
        foreach (var month in months)
        {
            await using var db = _factory.Open(month);
            var count = await Filter(db.CollectRecords.AsNoTracking(), request).CountAsync(cancellationToken).ConfigureAwait(false);
            counts.Add((month, count));
        }

        var total = counts.Sum(x => x.Count);
        var skip = request.Skip;
        var take = request.Take;
        var items = new List<CollectRecordListItem>();

        foreach (var (month, count) in counts)
        {
            if (take <= 0)
            {
                break;
            }

            if (skip >= count)
            {
                skip -= count;
                continue;
            }

            await using var db = _factory.Open(month);
            var page = await Filter(db.CollectRecords.AsNoTracking(), request)
                .OrderByDescending(x => x.TriggerTime)
                .Skip(skip)
                .Take(take)
                .Include(x => x.Products)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            skip = 0;
            take -= page.Count;
            items.AddRange(page.Select(r => new CollectRecordListItem { MonthKey = month, Record = r }));
        }

        return new CollectQueryResult { Total = total, Items = items };
    }

    public async Task<CollectRecord?> GetRecordAsync(string monthKey, long recordId, CancellationToken cancellationToken = default)
    {
        if (!_factory.Exists(monthKey))
        {
            return null;
        }

        await using var db = _factory.Open(monthKey);
        return await db.CollectRecords
            .AsNoTracking()
            .Include(x => x.Products)
            .Include(x => x.TagValues)
            .Include(x => x.Curves).ThenInclude(c => c.Features)
            .FirstOrDefaultAsync(x => x.Id == recordId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PalletSession?> GetSessionAsync(string monthKey, long sessionId, CancellationToken cancellationToken = default)
    {
        if (!_factory.Exists(monthKey))
        {
            return null;
        }

        await using var db = _factory.Open(monthKey);
        return await db.PalletSessions.AsNoTracking()
            .Include(x => x.Records).ThenInclude(r => r.Products)
            .FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CollectRecord>> GetSessionRecordsAsync(string monthKey, long sessionId, CancellationToken cancellationToken = default)
    {
        if (!_factory.Exists(monthKey))
        {
            return [];
        }

        await using var db = _factory.Open(monthKey);
        return await db.CollectRecords.AsNoTracking()
            .Where(x => x.PalletSessionId == sessionId)
            .Include(x => x.Products)
            .Include(x => x.TagValues)
            .Include(x => x.Curves)
            .OrderBy(x => x.TriggerTime)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CollectRecord>> QueryForReportAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        var list = new List<CollectRecord>();
        foreach (var month in RuntimeDbFactory.MonthsInRange(from, to).Where(_factory.Exists))
        {
            await using var db = _factory.Open(month);
            var part = await db.CollectRecords.AsNoTracking()
                .Where(x => x.TriggerTime >= from && x.TriggerTime <= to)
                .Include(x => x.Products)
                .Include(x => x.TagValues)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            list.AddRange(part);
        }

        return list;
    }

    public async Task<IReadOnlyList<JudgementPoint>> QueryJudgementPointsAsync(
        DateTime from,
        DateTime to,
        int? stationId,
        CancellationToken cancellationToken = default)
    {
        var points = new List<JudgementPoint>();
        foreach (var month in RuntimeDbFactory.MonthsInRange(from, to).Where(_factory.Exists))
        {
            await using var db = _factory.Open(month);
            var query = db.CollectRecords.AsNoTracking()
                .Where(x => x.TriggerTime >= from && x.TriggerTime <= to);
            if (stationId is { } sid)
            {
                query = query.Where(x => x.StationId == sid);
            }

            var part = await query
                .Select(x => new JudgementPoint
                {
                    Time = x.TriggerTime,
                    StationId = x.StationId,
                    Judgement = x.Judgement
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            points.AddRange(part);
        }

        return points;
    }

    public Task<IReadOnlyList<TagIssuePoint>> QueryOutOfLimitTagsAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default)
        => QueryTagIssuesAsync(from, to, warning: false, cancellationToken);

    public Task<IReadOnlyList<TagIssuePoint>> QueryWarningTagsAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default)
        => QueryTagIssuesAsync(from, to, warning: true, cancellationToken);

    private async Task<IReadOnlyList<TagIssuePoint>> QueryTagIssuesAsync(
        DateTime from,
        DateTime to,
        bool warning,
        CancellationToken cancellationToken)
    {
        var points = new List<TagIssuePoint>();
        foreach (var month in RuntimeDbFactory.MonthsInRange(from, to).Where(_factory.Exists))
        {
            await using var db = _factory.Open(month);
            // 显式 join 而不是靠导航属性，保证被翻译成单条带 WHERE 的 SQL，
            // 不在内存里展开任何一条记录的 TagValues 集合。
            var issues = db.TagValues
                .AsNoTracking()
                .Join(
                    db.CollectRecords.AsNoTracking(),
                    tag => tag.CollectRecordId,
                    record => record.Id,
                    (tag, record) => new { tag, record });

            // 超限与预警互斥（判定结果是单值），按哪个标记筛由调用方决定。
            issues = warning
                ? issues.Where(x => x.tag.IsWarning)
                : issues.Where(x => x.tag.IsOutOfLimit);

            var part = await issues
                .Where(x => x.record.TriggerTime >= from && x.record.TriggerTime <= to)
                .Select(x => new TagIssuePoint
                {
                    TagName = x.tag.TagName,
                    TagCode = x.tag.TagCode
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            points.AddRange(part);
        }

        return points;
    }

    public async Task<IReadOnlyList<TagTrendPoint>> QueryTagTrendAsync(
        DateTime from,
        DateTime to,
        int tagId,
        CancellationToken cancellationToken = default)
    {
        var points = new List<TagTrendPoint>();
        foreach (var month in RuntimeDbFactory.MonthsInRange(from, to).Where(_factory.Exists))
        {
            await using var db = _factory.Open(month);
            var part = await db.TagValues
                .AsNoTracking()
                .Join(
                    db.CollectRecords.AsNoTracking(),
                    tag => tag.CollectRecordId,
                    record => record.Id,
                    (tag, record) => new { tag, record })
                .Where(x => x.tag.TagId == tagId
                            && x.tag.NumericValue != null
                            && x.record.TriggerTime >= from
                            && x.record.TriggerTime <= to)
                .OrderBy(x => x.record.TriggerTime)
                .Select(x => new TagTrendPoint
                {
                    Time = x.record.TriggerTime,
                    Value = x.tag.NumericValue!.Value,
                    PalletCode = x.record.PalletCode
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            points.AddRange(part);
        }

        return points;
    }

    public async Task<IReadOnlyList<CurveFeaturePoint>> QueryCurveFeaturesAsync(
        int curveDefinitionId,
        string? seriesName,
        DateTime from,
        DateTime to,
        int take,
        CancellationToken cancellationToken = default)
    {
        var points = new List<CurveFeaturePoint>();
        if (take <= 0)
        {
            return points;
        }

        foreach (var month in RuntimeDbFactory.MonthsInRange(from, to).Where(_factory.Exists))
        {
            await using var db = _factory.Open(month);

            // 三层显式 join：特征 → 曲线记录（定义 Id）→ 采集记录（时间 / 判定 / 型号）。
            // 用 join 而不是导航属性，保证翻译成带 WHERE 的单条 SQL，不在内存里展开任何集合。
            var query = db.CurveFeatures
                .AsNoTracking()
                .Join(
                    db.CurveRecords.AsNoTracking(),
                    feature => feature.CurveRecordId,
                    curve => curve.Id,
                    (feature, curve) => new { feature, curve })
                .Join(
                    db.CollectRecords.AsNoTracking(),
                    x => x.curve.CollectRecordId,
                    record => record.Id,
                    (x, record) => new { x.feature, x.curve, record })
                .Where(x => x.curve.CurveDefinitionId == curveDefinitionId
                            && x.record.TriggerTime >= from
                            && x.record.TriggerTime <= to);

            if (!string.IsNullOrWhiteSpace(seriesName))
            {
                query = query.Where(x => x.feature.SeriesName == seriesName);
            }

            var part = await query
                .OrderByDescending(x => x.record.TriggerTime)
                .Take(take)
                .Select(x => new CurveFeaturePoint
                {
                    CurveRecordId = x.curve.Id,
                    Time = x.record.TriggerTime,
                    PalletCode = x.record.PalletCode,
                    IsNg = x.record.Judgement == Judgement.Ng,
                    RecipeCode = x.record.RecipeCode,
                    SeriesName = x.feature.SeriesName,
                    Role = x.feature.Role,
                    Feature = new CurveFeature
                    {
                        Id = x.feature.Id,
                        CurveRecordId = x.feature.CurveRecordId,
                        SeriesName = x.feature.SeriesName,
                        Role = x.feature.Role,
                        PointCount = x.feature.PointCount,
                        Min = x.feature.Min,
                        MinIndex = x.feature.MinIndex,
                        Peak = x.feature.Peak,
                        PeakIndex = x.feature.PeakIndex,
                        Mean = x.feature.Mean,
                        StdDev = x.feature.StdDev,
                        Area = x.feature.Area,
                        RiseSlope = x.feature.RiseSlope,
                        HoldSlope = x.feature.HoldSlope,
                        RiseIndex = x.feature.RiseIndex,
                        RiseSpan = x.feature.RiseSpan,
                        FallRatio = x.feature.FallRatio,
                        MaxStep = x.feature.MaxStep,
                        Oscillations = x.feature.Oscillations
                    }
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            points.AddRange(part);
        }

        // 跨月合并后重新取全局最新 take 条：每月各取 take 条，
        // 全局最新的 take 条必然落在并集里，不会漏样本。
        return points
            .OrderByDescending(p => p.Time)
            .Take(take)
            .OrderBy(p => p.Time)
            .ToList();
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

    public Task DeleteMonthAsync(string monthKey, CancellationToken cancellationToken = default)
    {
        _factory.DeleteMonth(monthKey);
        return Task.CompletedTask;
    }

    private static IQueryable<CollectRecord> Filter(IQueryable<CollectRecord> query, CollectQueryRequest request)
    {
        query = query.Where(x => x.TriggerTime >= request.From && x.TriggerTime <= request.To);
        if (!string.IsNullOrWhiteSpace(request.PalletCode))
        {
            query = query.Where(x => x.PalletCode.Contains(request.PalletCode));
        }

        if (!string.IsNullOrWhiteSpace(request.SerialNo))
        {
            query = query.Where(x => x.SerialNo.Contains(request.SerialNo));
        }

        if (request.StationId is { } stationId)
        {
            query = query.Where(x => x.StationId == stationId);
        }

        if (request.Judgement is { } judgement)
        {
            query = query.Where(x => x.Judgement == judgement);
        }

        return query;
    }
}
