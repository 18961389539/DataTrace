using DataTrace.Application.Alarms;
using DataTrace.Application.Reporting;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

partial class RuntimeStore
{
    public async Task<CollectQueryResult> QueryAsync(CollectQueryRequest request, CancellationToken cancellationToken = default)
    {
        var months = ExistingMonthKeys();
        var total = 0;
        var candidates = new List<CollectRecordListItem>();
        var perMonthTake = (int)Math.Min(
            int.MaxValue,
            (long)Math.Max(0, request.Skip) + Math.Max(0, request.Take));

        foreach (var month in months)
        {
            await using var db = _factory.Open(month);
            var filtered = Filter(db.CollectRecords.AsNoTracking(), request);
            total += await filtered.CountAsync(cancellationToken).ConfigureAwait(false);
            if (perMonthTake == 0)
            {
                continue;
            }

            // Records can be stored in their session's start-month database even when their
            // trigger time is later. Fetch each database's local top N, then merge globally.
            // 局部前 N 必须按"和全局同一个次序"取：按耗时升序时某个月库若仍按时间取前 N，
            // 会先把全局真正靠前的记录裁掉，合并结果就错了。
            var page = await ApplySort(filtered, request)
                .Take(perMonthTake)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            candidates.AddRange(page.Select(r => new CollectRecordListItem { MonthKey = month, Record = r }));
        }

        var items = ApplySort(candidates, request)
            .Skip(Math.Max(0, request.Skip))
            .Take(Math.Max(0, request.Take))
            .ToList();

        return new CollectQueryResult { Total = total, Items = items };
    }

    /// <summary>
    /// 按请求的列排序（库侧）。末位永远用 Id 兜底：并列值之间没有稳定次序，
    /// 翻页会出现重复行与漏行。
    /// </summary>
    private static IOrderedQueryable<CollectRecord> ApplySort(IQueryable<CollectRecord> query, CollectQueryRequest request)
    {
        var desc = request.SortDescending;
        var ordered = request.SortBy switch
        {
            CollectSortField.SerialNo => desc ? query.OrderByDescending(x => x.SerialNo) : query.OrderBy(x => x.SerialNo),
            CollectSortField.PalletCode => desc ? query.OrderByDescending(x => x.PalletCode) : query.OrderBy(x => x.PalletCode),
            CollectSortField.StationCode => desc ? query.OrderByDescending(x => x.StationCode) : query.OrderBy(x => x.StationCode),
            CollectSortField.RecipeCode => desc ? query.OrderByDescending(x => x.RecipeCode) : query.OrderBy(x => x.RecipeCode),
            CollectSortField.Judgement => desc ? query.OrderByDescending(x => x.Judgement) : query.OrderBy(x => x.Judgement),
            CollectSortField.ResultCode => desc ? query.OrderByDescending(x => x.ResultCode) : query.OrderBy(x => x.ResultCode),
            CollectSortField.DurationMs => desc ? query.OrderByDescending(x => x.DurationMs) : query.OrderBy(x => x.DurationMs),
            _ => desc ? query.OrderByDescending(x => x.TriggerTime) : query.OrderBy(x => x.TriggerTime)
        };
        return desc ? ordered.ThenByDescending(x => x.Id) : ordered.ThenBy(x => x.Id);
    }

    /// <summary>
    /// 内存里的合并排序。必须与上面那个库侧 ApplySort 同一套次序 ——
    /// 两侧不一致时，局部裁剪与最终排序会各按一套标准，分页结果自相矛盾。
    /// 末尾补 MonthKey：Id 只在单个月库内唯一，跨库并列时它排不出全序。
    /// </summary>
    private static IOrderedEnumerable<CollectRecordListItem> ApplySort(IEnumerable<CollectRecordListItem> items, CollectQueryRequest request)
    {
        var desc = request.SortDescending;
        var ordered = request.SortBy switch
        {
            CollectSortField.SerialNo => desc ? items.OrderByDescending(x => x.Record.SerialNo) : items.OrderBy(x => x.Record.SerialNo),
            CollectSortField.PalletCode => desc ? items.OrderByDescending(x => x.Record.PalletCode) : items.OrderBy(x => x.Record.PalletCode),
            CollectSortField.StationCode => desc ? items.OrderByDescending(x => x.Record.StationCode) : items.OrderBy(x => x.Record.StationCode),
            CollectSortField.RecipeCode => desc ? items.OrderByDescending(x => x.Record.RecipeCode) : items.OrderBy(x => x.Record.RecipeCode),
            CollectSortField.Judgement => desc ? items.OrderByDescending(x => x.Record.Judgement) : items.OrderBy(x => x.Record.Judgement),
            CollectSortField.ResultCode => desc ? items.OrderByDescending(x => x.Record.ResultCode) : items.OrderBy(x => x.Record.ResultCode),
            CollectSortField.DurationMs => desc ? items.OrderByDescending(x => x.Record.DurationMs) : items.OrderBy(x => x.Record.DurationMs),
            _ => desc ? items.OrderByDescending(x => x.Record.TriggerTime) : items.OrderBy(x => x.Record.TriggerTime)
        };
        return (desc ? ordered.ThenByDescending(x => x.Record.Id) : ordered.ThenBy(x => x.Record.Id))
            .ThenBy(x => x.MonthKey, StringComparer.Ordinal);
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

    /// <summary>
    /// 补传重放前的幂等查重。库不存在就是"没有"，不去新建 —— 这条路径只做判断，
    /// 不该因为一次重放就在数据盘上凭空多出一个月份库。
    /// </summary>
    public async Task<bool> ExistsBySerialAsync(string monthKey, string serialNo, CancellationToken cancellationToken = default)
    {
        if (!_factory.Exists(monthKey))
        {
            return false;
        }

        await using var db = _factory.Open(monthKey);
        return await db.CollectRecords
            .AsNoTracking()
            .AnyAsync(x => x.SerialNo == serialNo, cancellationToken)
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

    public async Task<IReadOnlyList<CollectSessionTrace>> FindSessionTracesBySerialNoAsync(
        string serialNo,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serialNo))
        {
            return [];
        }

        var traces = new List<CollectSessionTrace>();
        foreach (var month in _factory.ListMonthKeys()
                     .Where(RuntimeDbFactory.IsValidMonthKey)
                     .OrderByDescending(x => x, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var db = _factory.Open(month);
            var records = await db.CollectRecords.AsNoTracking()
                .Where(x => x.SerialNo == serialNo)
                .Include(x => x.PalletSession)
                .Include(x => x.Products)
                .Include(x => x.TagValues)
                .Include(x => x.Curves)
                .AsSplitQuery()
                .OrderBy(x => x.TriggerTime)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var group in records
                         .Where(x => x.PalletSessionId > 0 && x.PalletSession is not null)
                         .GroupBy(x => x.PalletSessionId))
            {
                var ordered = group.ToList();
                traces.Add(new CollectSessionTrace
                {
                    MonthKey = month,
                    SessionId = group.Key,
                    Session = ordered[0].PalletSession,
                    Records = ordered
                });
            }

            foreach (var record in records.Where(x => x.PalletSessionId <= 0 || x.PalletSession is null))
            {
                traces.Add(new CollectSessionTrace
                {
                    MonthKey = month,
                    SessionId = null,
                    Session = null,
                    Records = [record]
                });
            }
        }

        return traces
            .OrderByDescending(x => x.Session?.StartTime ?? x.Records[0].TriggerTime)
            .ToList();
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

        if (request.ResultCode is { } resultCode)
        {
            query = query.Where(x => x.ResultCode == resultCode);
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
