using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using DataTrace.Application.Alarms;
using DataTrace.Application.Reporting;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

partial class RuntimeStore
{
    private static readonly TimeSpan QueryCountCacheTtl = TimeSpan.FromSeconds(2);
    private const int QueryCountCacheCapacity = 512;
    private sealed record QueryCursor(int Version, CollectSortField SortBy, bool Descending, string SortValue, bool IsNull,
        long Id, string MonthKey, string FilterKey, string ScopeId);

    /// <summary>Matches SQLite's default BINARY text collation, which compares UTF-8 bytes.</summary>
    private sealed class SqliteBinaryStringComparer : IComparer<string?>
    {
        public static SqliteBinaryStringComparer Instance { get; } = new();
        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            return Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right));
        }
    }

    public async Task<CollectQueryResult> QueryAsync(CollectQueryRequest request, CancellationToken cancellationToken = default)
    {
        var take = Math.Max(0, request.Take);
        var skip = Math.Max(0, request.Skip);
        var months = ExistingMonthKeys();
        var desc = request.SortDescending;

        // 先对每个月库做两次索引扫描（TriggerTime 有单列索引）：
        // - COUNT 合成 Total（配合短 TTL 缓存，连续翻页不再每次重算全部月库的计数）；
        // - 边界触发时间（按排序方向取 MIN/MAX）给时间排序路径"这个库不可能影响全局窗口"的证明。
        // 月库按会话开始月归档：记录的触发时间可能落在别的月份的库里，
        // 所以边界必须查每个库自己的实际最小/最大触发时间，不能拿月份名字当时间范围。
        var generation = _factory.QueryGeneration;
        var cacheKey = QueryCountCacheKey(request, months);
        var cachedTotal = _factory.TryGetQueryTotal(cacheKey, generation, QueryCountCacheTtl, out var totalCount);
        var stats = new List<(string Month, int Count, DateTime Boundary)>();
        if (!cachedTotal) totalCount = 0;
        foreach (var month in months)
        {
            await using var db = _factory.Open(month);
            var filtered = Filter(db.CollectRecords.AsNoTracking(), request);
            var count = cachedTotal ? -1 : await filtered.CountAsync(cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                continue;
            }

            var boundary = desc
                ? await filtered.MaxAsync(x => (DateTime?)x.TriggerTime, cancellationToken).ConfigureAwait(false)
                : await filtered.MinAsync(x => (DateTime?)x.TriggerTime, cancellationToken).ConfigureAwait(false);
            if (boundary is null) continue;
            stats.Add((month, count, boundary.Value));
            if (!cachedTotal) totalCount += count;
        }
        if (!cachedTotal)
        {
            _factory.CacheQueryTotal(cacheKey, totalCount, generation, QueryCountCacheTtl, QueryCountCacheCapacity);
        }
        var total = totalCount;

        if (take == 0)
        {
            return new CollectQueryResult { Total = total, Items = [] };
        }

        var cursorFilterKey = QueryCursorFilterKey(request, months);
        var cursor = DecodeCursor(request.AfterCursor, request, cursorFilterKey);
        var cursorMode = cursor is not null || (string.IsNullOrEmpty(request.AfterCursor) && skip == 0);
        var perMonthTake = cursorMode ? (int)Math.Min(int.MaxValue, (long)take + 1) : (int)Math.Min(int.MaxValue, (long)skip + take);
        var candidates = new List<CollectRecordListItem>();
        if (request.SortBy == CollectSortField.TriggerTime)
        {
            // 时间排序（默认列）：全局次序与触发时间一致。按各库的实际边界触发时间排序逐库
            // 取局部前 N；候选够了之后，若下一个库的边界不可能进入当前窗口就停止 ——
            // 归并的库数从"全部"收敛到"窗口所在的少数几个"。36 库 × 跳过 10000 条时，
            // 旧的写法要物化 ~36 万条候选；这条路径通常只读 1-3 个库。
            // 剪枝只在"下一个库的边界严格晚于窗口末端"时生效，边界相等继续取：宁可多读，
            // 绝不裁掉可能进窗口的数据 —— 深分页错行比慢更不可接受。
            var ordered = desc
                ? stats.OrderByDescending(s => s.Boundary).ToList()
                : stats.OrderBy(s => s.Boundary).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                candidates.AddRange(await FetchLocalTopAsync(ordered[i].Month, request, perMonthTake, cancellationToken, cursorMode ? cursor : null).ConfigureAwait(false));
                if (candidates.Count < perMonthTake || i + 1 >= ordered.Count)
                {
                    continue;
                }

                var windowEnd = ApplySort(candidates, request)
                    .ElementAt(perMonthTake - 1)
                    .Record.TriggerTime;
                var nextBoundary = ordered[i + 1].Boundary;
                if (desc ? nextBoundary < windowEnd : nextBoundary > windowEnd)
                {
                    break;
                }
            }
        }
        else
        {
            // 非时间排序（流水号/耗时等）：每库按同一全序各取前 N 再归并；有游标时先在库侧
            // 应用复合边界后取局部前 N，因此深翻页工作量不随页码线性增加。
            foreach (var (month, _, _) in stats)
            {
                candidates.AddRange(await FetchLocalTopAsync(month, request, perMonthTake, cancellationToken, cursorMode ? cursor : null).ConfigureAwait(false));
            }
        }

        var orderedItems = ApplySort(candidates, request);
        var pageCandidates = orderedItems.Skip(cursorMode ? 0 : skip)
            .Take(cursorMode ? (int)Math.Min(int.MaxValue, (long)take + 1) : take)
            .ToList();
        var hasNext = cursorMode && pageCandidates.Count > take;
        var items = pageCandidates.Take(take).ToList();
        var next = hasNext && items.Count > 0 ? EncodeCursor(items[^1], request, cursorFilterKey) : null;
        return new CollectQueryResult { Total = total, Items = items, NextCursor = next };
    }

    /// <summary>
    /// 单个月库的局部前 N。必须按"和全局同一个次序"取：按耗时排序时某个月库若仍按时间取前 N，
    /// 会先把全局真正靠前的记录裁掉，合并结果就错了。
    /// </summary>
    private async Task<List<CollectRecordListItem>> FetchLocalTopAsync(
        string month,
        CollectQueryRequest request,
        int perMonthTake,
        CancellationToken cancellationToken,
        QueryCursor? cursor = null)
    {
        await using var db = _factory.Open(month);
        var filtered = Filter(db.CollectRecords.AsNoTracking(), request);
        if (cursor is not null) filtered = ApplyCursor(filtered, request, cursor, month);
        var page = await ApplySort(filtered, request)
            .Take(perMonthTake)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return page.Select(r => new CollectRecordListItem { MonthKey = month, Record = r }).ToList();
    }

    private string? EncodeCursor(CollectRecordListItem item, CollectQueryRequest request, string filterKey)
    {
        var (value, isNull) = SortValue(item.Record, request.SortBy);
        var cursor = new QueryCursor(1, request.SortBy, request.SortDescending, value, isNull,
            item.Record.Id, item.MonthKey, filterKey, _factory.QueryScopeId);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cursor)));
        return token.Length <= 4096 ? token : null;
    }

    private QueryCursor? DecodeCursor(string? token, CollectQueryRequest request, string filterKey)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) return null;
        try
        {
            var cursor = JsonSerializer.Deserialize<QueryCursor>(Encoding.UTF8.GetString(Convert.FromBase64String(token)));
            return cursor is not null && cursor.Version == 1 && cursor.SortBy == request.SortBy
                   && Enum.IsDefined(cursor.SortBy) && cursor.Descending == request.SortDescending
                   && cursor.FilterKey == filterKey && cursor.ScopeId == _factory.QueryScopeId
                   && cursor.SortValue.Length <= 2048 && RuntimeDbFactory.IsValidMonthKey(cursor.MonthKey)
                   && cursor.Id > 0 && IsValidCursorSortValue(cursor.SortValue, cursor.SortBy)
                ? cursor
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or OverflowException or NullReferenceException)
        {
            return null;
        }
    }

    private static (string Value, bool IsNull) SortValue(CollectRecord record, CollectSortField field)
        => field switch
        {
            CollectSortField.SerialNo => (record.SerialNo ?? "", record.SerialNo is null),
            CollectSortField.PalletCode => (record.PalletCode ?? "", record.PalletCode is null),
            CollectSortField.StationCode => (record.StationCode ?? "", record.StationCode is null),
            CollectSortField.RecipeCode => (record.RecipeCode ?? "", record.RecipeCode is null),
            CollectSortField.Judgement => (((int)record.Judgement).ToString(System.Globalization.CultureInfo.InvariantCulture), false),
            CollectSortField.ResultCode => (record.ResultCode.ToString(System.Globalization.CultureInfo.InvariantCulture), false),
            CollectSortField.DurationMs => (record.DurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture), false),
            _ => (record.TriggerTime.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), false)
        };

    private string QueryCursorFilterKey(CollectQueryRequest request, IReadOnlyList<string> months)
        => JsonSerializer.Serialize(new
        {
            Filters = QueryCountCacheKey(request, months),
            request.SortBy,
            request.SortDescending,
            RootScope = _factory.QueryScopeId
        });

    private bool IsValidCursorSortValue(string value, CollectSortField field)
    {
        try
        {
            _ = field switch
            {
                CollectSortField.TriggerTime => long.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
                CollectSortField.DurationMs => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
                CollectSortField.ResultCode => short.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
                CollectSortField.Judgement => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
                _ => 0
            };
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException) { return false; }
    }

    private static IQueryable<CollectRecord> ApplyCursor(
        IQueryable<CollectRecord> query, CollectQueryRequest request, QueryCursor cursor, string month)
    {
        var property = request.SortBy switch
        {
            CollectSortField.SerialNo => nameof(CollectRecord.SerialNo),
            CollectSortField.PalletCode => nameof(CollectRecord.PalletCode),
            CollectSortField.StationCode => nameof(CollectRecord.StationCode),
            CollectSortField.RecipeCode => nameof(CollectRecord.RecipeCode),
            CollectSortField.Judgement => nameof(CollectRecord.Judgement),
            CollectSortField.ResultCode => nameof(CollectRecord.ResultCode),
            CollectSortField.DurationMs => nameof(CollectRecord.DurationMs),
            _ => nameof(CollectRecord.TriggerTime)
        };
        var parameter = Expression.Parameter(typeof(CollectRecord), "record");
        var member = Expression.Property(parameter, property);
        Expression key = member.Type.IsEnum
            ? Expression.Convert(member, Enum.GetUnderlyingType(member.Type))
            : member;
        var tie = BuildCursorTie(parameter, cursor, month, request.SortDescending);
        Expression primaryAfter;
        Expression primaryEqual;
        if (key.Type == typeof(string))
        {
            var cursorValue = Expression.Constant(cursor.IsNull ? null : cursor.SortValue, typeof(string));
            primaryEqual = Expression.Equal(key, cursorValue);
            if (cursor.IsNull)
            {
                primaryAfter = request.SortDescending
                    ? Expression.Constant(false)
                    : Expression.NotEqual(key, Expression.Constant(null, typeof(string)));
            }
            else
            {
                var compare = Expression.Call(typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!, key, cursorValue);
                var comparison = Expression.GreaterThan(compare, Expression.Constant(0));
                if (request.SortDescending) comparison = Expression.LessThan(compare, Expression.Constant(0));
                primaryAfter = request.SortDescending
                    ? Expression.OrElse(Expression.Equal(key, Expression.Constant(null, typeof(string))), comparison)
                    : comparison;
            }
        }
        else
        {
            var value = ParseCursorSortValue(cursor.SortValue, key.Type, request.SortBy);
            var cursorValue = Expression.Constant(value, key.Type);
            primaryEqual = Expression.Equal(key, cursorValue);
            primaryAfter = Expression.MakeBinary(request.SortDescending ? ExpressionType.LessThan : ExpressionType.GreaterThan, key, cursorValue);
        }

        var predicate = Expression.Lambda<Func<CollectRecord, bool>>(
            Expression.OrElse(primaryAfter, Expression.AndAlso(primaryEqual, tie)), parameter);
        return query.Where(predicate);
    }

    private static Expression BuildCursorTie(ParameterExpression parameter, QueryCursor cursor, string month, bool descending)
    {
        var id = Expression.Property(parameter, nameof(CollectRecord.Id));
        var cursorId = Expression.Constant(cursor.Id);
        var idAfter = Expression.MakeBinary(descending ? ExpressionType.LessThan : ExpressionType.GreaterThan, id, cursorId);
        var sameId = Expression.Equal(id, cursorId);
        var monthAfter = string.CompareOrdinal(month, cursor.MonthKey) > 0;
        return Expression.OrElse(idAfter, Expression.AndAlso(sameId, Expression.Constant(monthAfter)));
    }

    private static object ParseCursorSortValue(string value, Type type, CollectSortField field)
    {
        if (field == CollectSortField.TriggerTime) return new DateTime(long.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
        if (type.IsEnum) return Enum.ToObject(type, int.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
        if (type == typeof(short)) return short.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        return int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string QueryCountCacheKey(CollectQueryRequest r, IReadOnlyList<string> months)
        => JsonSerializer.Serialize(new
        {
            From = r.From.Ticks,
            To = r.To.Ticks,
            Pallet = r.PalletCode?.Trim(),
            Serial = r.SerialNo?.Trim(),
            Stations = r.StationIds is { Count: > 0 } ids
                ? ids.OrderBy(x => x).ToArray()
                : r.StationId is { } one ? new[] { one } : null,
            r.Judgement,
            r.ResultCode,
            r.DurationMinMs,
            r.DurationMaxMs,
            r.OnlyOutOfLimit,
            OutTag = string.IsNullOrWhiteSpace(r.OutOfLimitTagName) ? null : r.OutOfLimitTagName.Trim(),
            Recipes = r.RecipeCodes is { Count: > 0 } codes
                ? codes.OrderBy(x => x, StringComparer.Ordinal).ToArray()
                : null,
            Recipe = r.RecipeCodes is { Count: > 0 } ? null : r.RecipeCode,
            Months = months
        });

    private void InvalidateQueryCountCache() => _factory.InvalidateQueryTotals();

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
            CollectSortField.SerialNo => desc ? items.OrderByDescending(x => x.Record.SerialNo, SqliteBinaryStringComparer.Instance) : items.OrderBy(x => x.Record.SerialNo, SqliteBinaryStringComparer.Instance),
            CollectSortField.PalletCode => desc ? items.OrderByDescending(x => x.Record.PalletCode, SqliteBinaryStringComparer.Instance) : items.OrderBy(x => x.Record.PalletCode, SqliteBinaryStringComparer.Instance),
            CollectSortField.StationCode => desc ? items.OrderByDescending(x => x.Record.StationCode, SqliteBinaryStringComparer.Instance) : items.OrderBy(x => x.Record.StationCode, SqliteBinaryStringComparer.Instance),
            CollectSortField.RecipeCode => desc ? items.OrderByDescending(x => x.Record.RecipeCode, SqliteBinaryStringComparer.Instance) : items.OrderBy(x => x.Record.RecipeCode, SqliteBinaryStringComparer.Instance),
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

    public async Task<bool> ExistsCollectAsync(string monthKey, string serialNo, int stationId, DateTime triggerTime, CancellationToken cancellationToken = default)
    {
        if (!_factory.Exists(monthKey))
        {
            return false;
        }

        await using var db = _factory.Open(monthKey);
        return await db.CollectRecords.AsNoTracking()
            .AnyAsync(x => x.SerialNo == serialNo && x.StationId == stationId && x.TriggerTime == triggerTime, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<long?> FindCollectRecordIdAsync(string monthKey, string serialNo, int stationId, DateTime triggerTime, CancellationToken cancellationToken = default)
    {
        if (!_factory.Exists(monthKey))
        {
            return null;
        }

        await using var db = _factory.Open(monthKey);
        return await db.CollectRecords.AsNoTracking()
            .Where(x => x.SerialNo == serialNo && x.StationId == stationId && x.TriggerTime == triggerTime)
            .Select(x => (long?)x.Id)
            .FirstOrDefaultAsync(cancellationToken)
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

    public Task<IReadOnlyList<CollectSessionTrace>> FindSessionTracesBySerialNoAsync(
        string serialNo,
        CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(serialNo)
            ? Task.FromResult<IReadOnlyList<CollectSessionTrace>>([])
            : FindSessionTracesAsync(serialNo, null, cancellationToken);

    public Task<IReadOnlyList<CollectSessionTrace>> FindSessionTracesBySessionIdAsync(
        long sessionId,
        CancellationToken cancellationToken = default)
        => sessionId <= 0
            ? Task.FromResult<IReadOnlyList<CollectSessionTrace>>([])
            : FindSessionTracesAsync(null, sessionId, cancellationToken);

    private async Task<IReadOnlyList<CollectSessionTrace>> FindSessionTracesAsync(
        string? serialNo,
        long? sessionId,
        CancellationToken cancellationToken)
    {
        var traces = new List<CollectSessionTrace>();
        foreach (var month in _factory.ListMonthKeys()
                     .Where(RuntimeDbFactory.IsValidMonthKey)
                     .OrderByDescending(x => x, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var db = _factory.Open(month);
            var records = await db.CollectRecords.AsNoTracking()
                .Where(x => sessionId != null ? x.PalletSessionId == sessionId : x.SerialNo == serialNo)
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

    
    public async Task<SessionQueryResult> QuerySessionsAsync(SessionQueryRequest request, CancellationToken cancellationToken = default)
    {
        var take = Math.Max(0, request.Take);
        var skip = Math.Max(0, request.Skip);
        var months = ExistingMonthKeys();
        var candidates = new List<SessionListItem>();
        var total = 0;
        var perMonthTake = take == 0 ? 0 : skip + take;

        foreach (var month in months)
        {
            await using var db = _factory.Open(month);
            var filtered = FilterSessions(db.PalletSessions.AsNoTracking(), request);
            var count = await filtered.CountAsync(cancellationToken).ConfigureAwait(false);
            if (count == 0) continue;
            total += count;
            if (take == 0) continue;

            // 列表需要站数与型号：按会话聚合记录，避免把整份 Records 导航物化进来。
            var page = await filtered
                .OrderByDescending(x => x.StartTime)
                .ThenByDescending(x => x.Id)
                .Take(Math.Max(perMonthTake, 1))
                .Select(x => new
                {
                    Session = x,
                    RecordCount = x.Records.Count(),
                    RecipeCode = x.Records
                        .OrderBy(r => r.TriggerTime)
                        .Select(r => r.RecipeCode)
                        .FirstOrDefault() ?? ""
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in page)
            {
                candidates.Add(new SessionListItem
                {
                    MonthKey = month,
                    Session = row.Session,
                    RecordCount = row.RecordCount,
                    RecipeCode = row.RecipeCode
                });
            }
        }

        var items = candidates
            .OrderByDescending(x => x.Session.StartTime)
            .ThenByDescending(x => x.Session.Id)
            .ThenBy(x => x.MonthKey, StringComparer.Ordinal)
            .Skip(skip)
            .Take(take)
            .ToList();

        return new SessionQueryResult { Total = total, Items = items };
    }

    private static IQueryable<PalletSession> FilterSessions(IQueryable<PalletSession> query, SessionQueryRequest request)
    {
        // 按开始时刻落在区间内：进行中的 Open 会话没有结束时刻，不能用 EndTime 筛。
        query = query.Where(x => x.StartTime >= request.From && x.StartTime <= request.To);
        if (!string.IsNullOrWhiteSpace(request.PalletCode))
        {
            query = query.Where(x => x.PalletCode.Contains(request.PalletCode));
        }

        if (!string.IsNullOrWhiteSpace(request.SerialNo))
        {
            query = query.Where(x => x.SerialNo.Contains(request.SerialNo));
        }

        if (request.Status is { } status)
        {
            query = query.Where(x => x.Status == status);
        }

        if (request.Judgement is { } judgement)
        {
            query = query.Where(x => x.Judgement == judgement);
        }

        if (request.RecipeCodes is { Count: > 0 } recipeCodes)
        {
            return query.Where(x => x.Records.Any(r => recipeCodes.Contains(r.RecipeCode)));
        }

        if (request.RecipeCode is not null)
        {
            return query.Where(x => x.Records.Any(r => r.RecipeCode == request.RecipeCode));
        }

        return query;
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

        if (request.StationIds is { Count: > 0 } stationIds)
        {
            query = query.Where(x => stationIds.Contains(x.StationId));
        }
        else if (request.StationId is { } stationId)
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

        if (request.DurationMinMs is { } durationMin)
        {
            query = query.Where(x => x.DurationMs >= durationMin);
        }

        if (request.DurationMaxMs is { } durationMax)
        {
            query = query.Where(x => x.DurationMs <= durationMax);
        }

        var outTag = string.IsNullOrWhiteSpace(request.OutOfLimitTagName) ? null : request.OutOfLimitTagName.Trim();
        if (outTag is not null)
        {
            query = query.Where(x => x.TagValues.Any(t => t.IsOutOfLimit && t.TagName == outTag));
        }
        else if (request.OnlyOutOfLimit)
        {
            query = query.Where(x => x.TagValues.Any(t => t.IsOutOfLimit));
        }

        if (request.RecipeCodes is { Count: > 0 } recipeCodes)
        {
            return query.Where(x => recipeCodes.Contains(x.RecipeCode));
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
