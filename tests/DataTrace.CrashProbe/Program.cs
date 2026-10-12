using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure;
using DataTrace.Infrastructure.Persistence;
using DataTrace.Infrastructure.Seeding;
using DataTrace.Infrastructure.Storage;
using DataTrace.Collector;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: DataTrace.CrashProbe <scenario> <isolated-data-root>");
    return 2;
}

var scenario = args[0];
var root = Path.GetFullPath(args[1]);
Directory.CreateDirectory(root);
using var services = new ServiceCollection()
    .AddLogging()
    .AddDataTraceInfrastructure(root)
    .BuildServiceProvider();

await using (var seedScope = services.CreateAsyncScope())
    await seedScope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();

var triggerTime = new DateTime(2026, 10, 12, 12, 34, 56, DateTimeKind.Utc);
var month = "202610";
var request = BuildRequest(month, triggerTime);

switch (scenario)
{
    case "crash-before-commit":
        await LeaveUncommittedTransactionAsync(services.GetRequiredService<RuntimeDbFactory>(), request, "before-runtime-commit");
        break;
    case "crash-after-commit":
        await SaveRuntimeAsync(services, request);
        await SignalAndWaitAsync("after-runtime-commit-with-pending");
        break;
    case "crash-after-spool":
        await services.GetRequiredService<ISpoolStore>().SaveAsync(request);
        await SignalAndWaitAsync("after-spool-file-durable");
        break;
    case "crash-postcommit-before-spool-delete":
        await services.GetRequiredService<ISpoolStore>().SaveAsync(request);
        await SaveRuntimeAsync(services, request);
        await SignalAndWaitAsync("after-runtime-commit-before-spool-delete");
        break;
    case "verify-empty":
        await ReportStateAsync(services, request, expected: 0, replay: false);
        break;
    case "verify-committed":
        await ReportStateAsync(services, request, expected: 1, replay: false);
        break;
    case "recover-spool":
        await ReportStateAsync(services, request, expected: 1, replay: true);
        break;
    default:
        Console.Error.WriteLine($"unknown scenario: {scenario}");
        return 2;
}

return 0;

static CollectSaveRequest BuildRequest(string monthKey, DateTime triggerTime)
{
    var serial = "CRASHPROBE-20261012-0001";
    var pallet = "CRASHPROBE-PALLET";
    return new CollectSaveRequest
    {
        MonthKey = monthKey,
        UpsertSession = new PalletSession { SerialNo = serial, PalletCode = pallet, StartTime = triggerTime },
        Record = new CollectRecord
        {
            SerialNo = serial,
            PalletCode = pallet,
            StationId = 17,
            StationCode = "ST017",
            TriggerTime = triggerTime,
            CompleteTime = triggerTime.AddSeconds(1),
            DurationMs = 1000,
            ResultCode = 0,
            Judgement = Judgement.Ok,
            RecipeCode = "CRASHPROBE"
        }
    };
}

static async Task LeaveUncommittedTransactionAsync(RuntimeDbFactory factory, CollectSaveRequest request, string phase)
{
    await using var db = factory.Open(request.MonthKey);
    await using var transaction = await db.Database.BeginTransactionAsync();
    var session = new PalletSession
    {
        SerialNo = request.Record.SerialNo,
        PalletCode = request.Record.PalletCode,
        StartTime = request.Record.TriggerTime
    };
    db.PalletSessions.Add(session);
    await db.SaveChangesAsync();
    db.CollectRecords.Add(new CollectRecord
    {
        PalletSessionId = session.Id,
        SerialNo = request.Record.SerialNo,
        PalletCode = request.Record.PalletCode,
        StationId = request.Record.StationId,
        StationCode = request.Record.StationCode,
        TriggerTime = request.Record.TriggerTime,
        CompleteTime = request.Record.CompleteTime,
        ResultCode = request.Record.ResultCode,
        Judgement = request.Record.Judgement
    });
    await db.SaveChangesAsync();
    await SignalAndWaitAsync(phase);
}

static async Task SaveRuntimeAsync(ServiceProvider provider, CollectSaveRequest request)
{
    await using var scope = provider.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ICollectWriter>().SaveAsync(request);
}

static async Task ReportStateAsync(ServiceProvider provider, CollectSaveRequest request, int expected, bool replay)
{
    var replayed = 0;
    if (replay)
    {
        var spool = provider.GetRequiredService<ISpoolStore>();
        var runner = new SpoolReplayRunner(provider.GetRequiredService<IServiceScopeFactory>(), spool,
            provider.GetRequiredService<ILogger<SpoolReplayRunner>>());
        replayed = await runner.ReplayAllAsync();
    }

    await using var scope = provider.CreateAsyncScope();
    var query = scope.ServiceProvider.GetRequiredService<ICollectQuery>();
    var result = await query.QueryAsync(new CollectQueryRequest
    {
        From = request.Record.TriggerTime.AddSeconds(-1),
        To = request.Record.TriggerTime.AddSeconds(2),
        SerialNo = request.Record.SerialNo,
        Take = 10
    });
    var pending = await scope.ServiceProvider.GetRequiredService<IPendingWriteBackStore>().ListUnconfirmedAsync();
    var spoolCount = (await provider.GetRequiredService<ISpoolStore>().ListAsync()).Count;
    if (result.Total != expected || pending.Count != expected || (replay && spoolCount != 0))
        throw new InvalidOperationException($"Recovery state mismatch: records={result.Total}, pending={pending.Count}, spool={spoolCount}, expected={expected}");

    Console.WriteLine($"VERIFIED|records={result.Total}|pending={pending.Count}|spool={spoolCount}|replayed={replayed}");
}

static async Task SignalAndWaitAsync(string phase)
{
    Console.WriteLine($"READY|{phase}");
    Console.Out.Flush();
    _ = await Console.In.ReadLineAsync();
}
