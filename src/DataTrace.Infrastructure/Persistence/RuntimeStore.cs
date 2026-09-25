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
            // 曲线文件必须由写它的那个存储来删：只有它知道自己的根目录。
            // 以前这里自己拼 cwd + "data/curves"，Windows 服务的工作目录是 System32，
            // 而且 DataRoot 也可能指到别处，两种情况下回滚都静默失效、留下孤儿文件。
            // 删的必须是 WriteAsync 返回的路径：它撞名会让开一格，因此只会删掉本次写的那个文件，
            // 不会连带删掉之前记录在同名路径上的波形。
            foreach (var relative in writtenFiles)
            {
                try
                {
                    await _curves.DeleteFileAsync(relative, cancellationToken).ConfigureAwait(false);
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
            // 列表 / 导出不需要 Products 导航：界面只展示记录头字段，Include 会放大到万行级导出。
            // 明细页走 GetRecordAsync，仍会 Include Products / TagValues / Curves。
            var page = await Filter(db.CollectRecords.AsNoTracking(), request)
                .OrderByDescending(x => x.TriggerTime)
                // 同毫秒并列的记录要有稳定次序：只按时间排序时，翻页取到的是两批"并列中的任意几条"，
                // 结果就是某些行重复出现、另一些行一次都不出现。
                .ThenByDescending(x => x.Id)
                .Skip(skip)
                .Take(take)
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
        string? recipeCode = null,
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

            query = ApplyRecipeFilter(query, recipeCode);

            var part = await query
                .Select(x => new JudgementPoint
                {
                    Time = x.TriggerTime,
                    StationId = x.StationId,
                    Judgement = x.Judgement,
                    RecipeCode = x.RecipeCode
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            points.AddRange(part);
        }

        return points;
    }

    public async Task<IReadOnlyList<string>> ListRecipeCodesAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var month in RuntimeDbFactory.MonthsInRange(from, to).Where(_factory.Exists))
        {
            await using var db = _factory.Open(month);
            var part = await db.CollectRecords.AsNoTracking()
                .Where(x => x.TriggerTime >= from && x.TriggerTime <= to)
                .Select(x => x.RecipeCode)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var code in part)
            {
                codes.Add(code ?? "");
            }
        }

        return codes
            .OrderBy(c => string.IsNullOrEmpty(c) ? "~" : c, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<IReadOnlyList<TagIssuePoint>> QueryOutOfLimitTagsAsync(
        DateTime from,
        DateTime to,
        int? stationId,
        string? recipeCode = null,
        CancellationToken cancellationToken = default)
        => QueryTagIssuesAsync(from, to, stationId, warning: false, recipeCode, cancellationToken);

    public Task<IReadOnlyList<TagIssuePoint>> QueryWarningTagsAsync(
        DateTime from,
        DateTime to,
        int? stationId,
        string? recipeCode = null,
        CancellationToken cancellationToken = default)
        => QueryTagIssuesAsync(from, to, stationId, warning: true, recipeCode, cancellationToken);

    private async Task<IReadOnlyList<TagIssuePoint>> QueryTagIssuesAsync(
        DateTime from,
        DateTime to,
        int? stationId,
        bool warning,
        string? recipeCode,
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

            if (stationId is { } sid)
            {
                issues = issues.Where(x => x.record.StationId == sid);
            }

            if (recipeCode is not null)
            {
                issues = issues.Where(x => x.record.RecipeCode == recipeCode);
            }

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
        string? recipeCode = null,
        int take = 0,
        CancellationToken cancellationToken = default)
    {
        var points = new List<TagTrendPoint>();

        // take > 0 时从最新的月库往回取、凑够就停：每个月都取 take 条的话，
        // "单次最多 take 点"这个上限在跨年区间上会被放大十几倍（高频点位一年十几万条），
        // 而更早的月库不可能提供更新的点，取它们纯属白读。
        var months = RuntimeDbFactory.MonthsInRange(from, to).Where(_factory.Exists).ToList();
        if (take > 0)
        {
            months.Reverse();
        }

        foreach (var month in months)
        {
            await using var db = _factory.Open(month);

            // 别把这对 join 反过来写（以采集记录为驱动表）：SQLite 会自己重排内连接，
            // 两种写法的计划一模一样；而真去锁死循环顺序（CROSS JOIN）时，内层拿不到
            // 等值条件、只能按 IX_TagValues_TagId_NumericValue 每个记录全扫一遍，直接退化成嵌套扫描。
            // 这个计划的成本与"该点位当月有多少行"成正比、与区间取多窄无关：区间再窄也省不下来，
            // 但它已经是索引驱动的最优解（实测某点位 1.5 万行约 0.1~0.2 s），take 提前到也帮不上。
            var query = db.TagValues
                .AsNoTracking()
                .Join(
                    db.CollectRecords.AsNoTracking(),
                    tag => tag.CollectRecordId,
                    record => record.Id,
                    (tag, record) => new { tag, record })
                .Where(x => x.tag.TagId == tagId
                            && x.tag.NumericValue != null
                            && x.record.TriggerTime >= from
                            && x.record.TriggerTime <= to);

            if (recipeCode is not null)
            {
                query = query.Where(x => x.record.RecipeCode == recipeCode);
            }

            // 要"最新 take 点"就得先倒序取；take <= 0 时不加限制，按月库顺序升序返回。
            var ordered = take > 0
                ? query.OrderByDescending(x => x.record.TriggerTime).Take(take)
                : query.OrderBy(x => x.record.TriggerTime);

            var part = await ordered
                .Select(x => new TagTrendPoint
                {
                    Time = x.record.TriggerTime,
                    Value = x.tag.NumericValue!.Value,
                    PalletCode = x.record.PalletCode,
                    LowerLimit = x.tag.LowerLimit,
                    UpperLimit = x.tag.UpperLimit
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            points.AddRange(part);

            if (take > 0 && points.Count >= take)
            {
                break;
            }
        }

        if (take <= 0)
        {
            return points;
        }

        // 跨月合并后重新取全局最新 take 条：已取到的点比所有未读的月库都新，
        // 所以只要凑够 take 条，结果必然就是全局最新的那批。最后统一升序，交给画图与移动极差。
        return points
            .OrderByDescending(p => p.Time)
            .Take(take)
            .OrderBy(p => p.Time)
            .ToList();
    }

    public async Task<IReadOnlyList<CurveFeaturePoint>> QueryCurveFeaturesAsync(
        int curveDefinitionId,
        string? seriesName,
        DateTime from,
        DateTime to,
        int take,
        IReadOnlyCollection<string>? recipeCodes = null,
        CancellationToken cancellationToken = default)
    {
        var points = new List<CurveFeaturePoint>();
        if (take <= 0)
        {
            return points;
        }

        // 型号过滤下推到 SQL，且必须在 Take 之前：take 要的是"最新 N 条"，
        // 取回内存再筛的话，另一种型号最近产量大一点就会把本型号整段挤出去。
        var codes = recipeCodes?.Where(c => c is not null).Distinct(StringComparer.Ordinal).ToList();

        // 倒序走月库、取够即停：每个月都取 take 条的话，跨年区间的读取量会按月份数放大，
        // 而更早的月库不可能提供更新的点。
        var months = RuntimeDbFactory.MonthsInRange(from, to).Where(_factory.Exists).Reverse().ToList();

        foreach (var month in months)
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

            if (codes is { Count: > 0 })
            {
                query = query.Where(x => codes.Contains(x.record.RecipeCode));
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

            if (points.Count >= take)
            {
                break;
            }
        }

        // 跨月合并后重新取全局最新 take 条：已取到的点比所有未读的月库都新，
        // 所以只要凑够 take 条，结果必然就是全局最新的那批。最后统一升序。
        return points
            .OrderByDescending(p => p.Time)
            .Take(take)
            .OrderBy(p => p.Time)
            .ToList();
    }

    public async Task<IReadOnlyList<CurveRecipeSampleCount>> CountCurveFeaturesByRecipeAsync(
        int curveDefinitionId,
        string? seriesName,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default)
    {
        var counts = new Dictionary<string, (int Total, int Ng)>(StringComparer.Ordinal);
        foreach (var month in RuntimeDbFactory.MonthsInRange(from, to).Where(_factory.Exists))
        {
            await using var db = _factory.Open(month);
            var part = await db.CurveFeatures
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
                            && x.record.TriggerTime <= to
                            && (seriesName == null || x.feature.SeriesName == seriesName))
                .GroupBy(x => x.record.RecipeCode)
                .Select(g => new
                {
                    RecipeCode = g.Key,
                    Total = g.Count(),
                    Ng = g.Count(x => x.record.Judgement == Judgement.Ng)
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var item in part)
            {
                var code = item.RecipeCode ?? "";
                var current = counts.GetValueOrDefault(code);
                counts[code] = (current.Total + item.Total, current.Ng + item.Ng);
            }
        }

        return counts
            .OrderByDescending(kv => kv.Value.Total)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new CurveRecipeSampleCount(kv.Key, kv.Value.Total, kv.Value.Ng))
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

        return ApplyRecipeFilter(query, request.RecipeCode);
    }

    /// <summary>
    /// 型号过滤的唯一实现，查询页与报表页共用同一套语义：
    /// null = 不限；非 null（含空串）按精确匹配，空串表示「未选型号」记录。
    /// </summary>
    private static IQueryable<CollectRecord> ApplyRecipeFilter(IQueryable<CollectRecord> query, string? recipeCode)
        => recipeCode is null ? query : query.Where(x => x.RecipeCode == recipeCode);
}
