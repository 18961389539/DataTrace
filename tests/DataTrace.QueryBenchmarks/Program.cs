using System.Diagnostics;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure;
using DataTrace.Infrastructure.Persistence;
using DataTrace.Infrastructure.Seeding;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

var rowsPerMonth = ReadInt(args, "rows-per-month", 10_000, 1, 100_000);
var pageSize = ReadInt(args, "page-size", 500, 50, 5_000);
var deepSkip = ReadInt(args, "deep-skip", 10_000, pageSize, 2_000_000);
var root = Path.Combine(Path.GetTempPath(), "datatrace-query-benchmark", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Console.WriteLine($"root={root}; months=36; rows_per_month={rowsPerMonth}; page_size={pageSize}; deep_skip={deepSkip}");

try
{
    using var services = new ServiceCollection()
        .AddLogging()
        .AddDataTraceInfrastructure(root)
        .BuildServiceProvider();
    await using (var seed = services.CreateAsyncScope())
        await seed.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();

    var factory = services.GetRequiredService<RuntimeDbFactory>();
    var query = services.CreateAsyncScope();
    await using (query)
    {
        var store = query.ServiceProvider.GetRequiredService<ICollectQuery>();
        var seedWatch = Stopwatch.StartNew();
        var globalIndex = 0;
        for (var year = 2024; year <= 2026; year++)
        for (var month = 1; month <= 12; month++)
        {
            var monthKey = $"{year:D4}{month:D2}";
            await using var db = factory.Open(monthKey);
            var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            var session = new PalletSession
            {
                SerialNo = $"BENCH-{monthKey}-SESSION",
                PalletCode = $"BENCH-{monthKey}",
                StartTime = first
            };
            db.PalletSessions.Add(session);
            await db.SaveChangesAsync();
            for (var offset = 0; offset < rowsPerMonth; offset += 1000)
            {
                var take = Math.Min(1000, rowsPerMonth - offset);
                db.CollectRecords.AddRange(Enumerable.Range(offset, take).Select(index =>
                {
                    var sequence = globalIndex + index;
                    var trigger = first.AddMinutes(index);
                    return new CollectRecord
                    {
                        PalletSessionId = session.Id,
                        SerialNo = $"BENCH-{sequence:D9}",
                        PalletCode = $"P-{sequence:D9}",
                        StationId = sequence % 8 + 1,
                        StationCode = $"ST{sequence % 8 + 1:D2}",
                        TriggerTime = trigger,
                        CompleteTime = trigger.AddSeconds(1),
                        DurationMs = sequence,
                        ResultCode = (short)(sequence % 4),
                        Judgement = sequence % 2 == 0 ? Judgement.Ok : Judgement.Ng,
                        RecipeCode = $"R{sequence % 12:D2}"
                    };
                }));
                await db.SaveChangesAsync();
            }
            globalIndex += rowsPerMonth;
        }
        seedWatch.Stop();
        PrintMemory("after-seed");

        var from = new DateTime(2024, 1, 1);
        var to = new DateTime(2026, 12, 31, 23, 59, 59);
        var timeRequest = new CollectQueryRequest
        {
            From = from, To = to, Take = pageSize,
            SortBy = CollectSortField.TriggerTime, SortDescending = true
        };
        var cold = Stopwatch.StartNew();
        var firstTimePage = await store.QueryAsync(timeRequest);
        cold.Stop();
        PrintMemory("after-time-first-page");

        var timeCursorWatch = Stopwatch.StartNew();
        var timeCursorPage = await PageAtOffsetAsync(store, timeRequest, deepSkip);
        timeCursorWatch.Stop();
        var timeOffsetWatch = Stopwatch.StartNew();
        var timeOffsetPage = await store.QueryAsync(new CollectQueryRequest
        {
            From = from, To = to, Skip = deepSkip, Take = pageSize,
            SortBy = CollectSortField.TriggerTime, SortDescending = true
        });
        timeOffsetWatch.Stop();
        AssertSameRows(timeOffsetPage, timeCursorPage, "TriggerTime");
        PrintMemory("after-time-deep-pages");

        var durationRequest = new CollectQueryRequest
        {
            From = from, To = to, Take = pageSize,
            SortBy = CollectSortField.DurationMs, SortDescending = true
        };
        var durationFirst = await store.QueryAsync(durationRequest);
        var durationCursorWatch = Stopwatch.StartNew();
        var durationCursorPage = await PageAtOffsetAsync(store, durationRequest, deepSkip);
        durationCursorWatch.Stop();
        var durationOffsetWatch = Stopwatch.StartNew();
        var durationOffsetPage = await store.QueryAsync(new CollectQueryRequest
        {
            From = from, To = to, Skip = deepSkip, Take = pageSize,
            SortBy = CollectSortField.DurationMs, SortDescending = true
        });
        durationOffsetWatch.Stop();
        AssertSameRows(durationOffsetPage, durationCursorPage, "DurationMs");
        PrintMemory("after-duration-deep-pages");

        var totalRows = 36L * rowsPerMonth;
        Console.WriteLine($"seed_rows={totalRows}; seed_ms={seedWatch.ElapsedMilliseconds}; seed_rows_per_second={(long)(totalRows / Math.Max(.001, seedWatch.Elapsed.TotalSeconds))}");
        Console.WriteLine($"time_first_cold_ms={cold.ElapsedMilliseconds}; first_page_count={firstTimePage.Items.Count}; total={firstTimePage.Total}");
        Console.WriteLine($"time_cursor_deep_ms={timeCursorWatch.ElapsedMilliseconds}; time_offset_deep_ms={timeOffsetWatch.ElapsedMilliseconds}; deep_page_count={timeCursorPage.Items.Count}; rows_match=true");
        Console.WriteLine($"duration_first_page_count={durationFirst.Items.Count}; duration_cursor_deep_ms={durationCursorWatch.ElapsedMilliseconds}; duration_offset_deep_ms={durationOffsetWatch.ElapsedMilliseconds}; deep_page_count={durationCursorPage.Items.Count}; rows_match=true");
        if (firstTimePage.Total != totalRows || firstTimePage.Items.Count != pageSize)
            throw new InvalidOperationException($"Unexpected benchmark row count/page size: {firstTimePage.Total}/{firstTimePage.Items.Count}");
    }
}
finally
{
    SqliteConnection.ClearAllPools();
    try { Directory.Delete(root, recursive: true); }
    catch { Console.Error.WriteLine($"temporary data cleanup deferred: {root}"); }
}

static int ReadInt(string[] arguments, string key, int fallback, int minimum, int maximum)
{
    var index = Array.IndexOf(arguments, $"--{key}");
    if (index < 0 || index + 1 >= arguments.Length) return fallback;
    return int.TryParse(arguments[index + 1], out var parsed) ? Math.Clamp(parsed, minimum, maximum) : fallback;
}

static async Task<CollectQueryResult> PageAtOffsetAsync(ICollectQuery store, CollectQueryRequest request, int skip)
{
    var cursor = (string?)null;
    var passed = 0;
    while (passed < skip)
    {
        var page = await store.QueryAsync(new CollectQueryRequest
        {
            From = request.From, To = request.To, Take = request.Take, SortBy = request.SortBy,
            SortDescending = request.SortDescending, AfterCursor = cursor, Skip = passed
        });
        if (page.Items.Count == 0 || page.NextCursor is null) throw new InvalidOperationException("Cursor ended before requested deep page");
        passed += page.Items.Count;
        cursor = page.NextCursor;
    }

    return await store.QueryAsync(new CollectQueryRequest
    {
        From = request.From, To = request.To, Take = request.Take, SortBy = request.SortBy,
        SortDescending = request.SortDescending, AfterCursor = cursor, Skip = passed
    });
}

static void AssertSameRows(CollectQueryResult expected, CollectQueryResult actual, string label)
{
    var left = expected.Items.Select(item => (item.MonthKey, item.Record.Id));
    var right = actual.Items.Select(item => (item.MonthKey, item.Record.Id));
    if (!left.SequenceEqual(right)) throw new InvalidOperationException($"Cursor and OFFSET returned different {label} rows");
}

static void PrintMemory(string phase)
{
    var process = Process.GetCurrentProcess();
    Console.WriteLine($"memory|phase={phase}|managed_mb={GC.GetTotalMemory(false) / 1024d / 1024d:F1}|working_set_mb={process.WorkingSet64 / 1024d / 1024d:F1}");
}
