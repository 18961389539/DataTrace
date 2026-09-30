using System.Diagnostics;
using DataTrace.Application.Configuration;
using DataTrace.Application.Runtime;
using DataTrace.Collector;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Tests;

/// <summary>
/// 采集链路关联：一次采集的日志必须能串起来。
/// </summary>
/// <remarks>
/// 这组断言针对的是排查现场那句"某件丢了 / 某件慢了"。
/// 之前的问题不是日志少，而是日志之间没有共同字段：写库失败那行只有工站码，
/// 补传那行只有缓存文件名，而流水号只活在记录对象里 —— 事后只能靠时间戳手工比对。
/// 所以这里分两处断言：消息文本里要有身份（改模板也丢不掉），作用域里也要有（覆盖没手写消息的行）。
/// </remarks>
public class CollectionTraceTests
{
    /// <summary>永远写不进去的写入器：把采集逼进"转本地缓存"那条路。</summary>
    private sealed class FailingWriter : ICollectWriter
    {
        public Task SaveAsync(CollectSaveRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("disk full");

        public Task MarkSessionAbnormalAsync(string monthKey, long sessionId, DateTime endTime, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static CollectAssessment Assessment()
        => new() { TagValues = [], Products = [], Curves = [] };

    /// <summary>
    /// 写库失败那行必须同时给出"是哪一件"和"缓存在哪个文件"。
    /// </summary>
    /// <remarks>
    /// 缓存文件名是"写库失败"与之后那行"补传成功/失败"之间**唯一**的共同字段：
    /// 一边只有工站码，另一边只有文件名。不给出来，这两行就永远对不上。
    /// </remarks>
    [Fact]
    public async Task Write_failure_log_names_the_piece_and_the_spool_file()
    {
        await using var ctx = await InfrastructureContext.CreateAsync(configure: services =>
            services.AddSingleton<ICollectWriter>(new FailingWriter()));
        var coordinator = new CollectSessionCoordinator(ctx.ScopeFactory, ctx.Logger<CollectSessionCoordinator>());

        var saved = await coordinator.PersistAsync(
            FirstStation(),
            new AppConfigurationSnapshot(),
            "P900",
            new DateTime(2026, 9, 30, 10, 0, 0),
            Stopwatch.StartNew(),
            source: null,
            Assessment(),
            CancellationToken.None);

        Assert.Equal(ResultCodes.DatabaseWriteFailed, saved.Record.ResultCode);
        var serial = saved.Record.SerialNo;
        Assert.False(string.IsNullOrWhiteSpace(serial));

        var logs = ctx.Logs.Snapshot();
        Assert.Contains(logs, entry => entry.Message.Contains("写库失败") && entry.Message.Contains(serial));

        var spoolFile = Path.GetFileName(Assert.Single(Directory.GetFiles(ctx.Workspace.Path("spool"), "*.spool.json")));
        Assert.Contains(logs, entry => entry.Message.Contains(spoolFile));
    }

    /// <summary>流水号也要进作用域：库里、框架里那些没手写消息的日志同样要能被串起来。</summary>
    [Fact]
    public async Task Write_failure_log_carries_the_piece_identity_in_scope()
    {
        await using var ctx = await InfrastructureContext.CreateAsync(configure: services =>
            services.AddSingleton<ICollectWriter>(new FailingWriter()));
        var coordinator = new CollectSessionCoordinator(ctx.ScopeFactory, ctx.Logger<CollectSessionCoordinator>());

        var saved = await coordinator.PersistAsync(
            FirstStation(),
            new AppConfigurationSnapshot(),
            "P901",
            new DateTime(2026, 9, 30, 10, 1, 0),
            Stopwatch.StartNew(),
            source: null,
            Assessment(),
            CancellationToken.None);

        // 作用域内的两行（写库失败、已转存缓存）都该带上它。
        var scoped = ctx.Logs.Scoped;
        Assert.NotEmpty(scoped);
        Assert.All(
            scoped.Where(entry => entry.Facts.ContainsKey(CollectionLogScope.Serial)),
            entry => Assert.Equal(saved.Record.SerialNo, entry.Facts[CollectionLogScope.Serial]));
    }

    /// <summary>
    /// 跑一次真采集：作用域里要有工站与触发时刻，并有一行带流水号的"采集完成"锚点。
    /// </summary>
    /// <remarks>
    /// 作用域必须跨类别生效 —— 工站与触发时刻由流水线写入，而落库那几行是协调器记的。
    /// 只有整条链路的日志都挂上同一份事实，串起来才成立。
    /// </remarks>
    [Fact]
    public async Task One_collection_puts_station_and_trigger_in_scope()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);

        await harness.RunAsync(station);

        var scoped = harness.Logs.Scoped;
        Assert.Contains(
            scoped,
            entry => entry.Facts.GetValueOrDefault(CollectionLogScope.Station) as string == station.Code
                     && entry.Facts.ContainsKey(CollectionLogScope.Trigger));

        // 锚点行：正常件也要有，否则来查一件正常件时会发现根本没有可搜的东西。
        // 它要一次把"哪一件、什么结果、多久、回写成没成"说完 —— 少了任何一项，
        // 查的人还得再去翻别的行拼。
        var anchor = Assert.Single(harness.Logs.Snapshot(), entry => entry.Message.Contains("采集完成"));
        Assert.Contains("结果", anchor.Message);
        Assert.Contains("耗时", anchor.Message);
        Assert.Contains("回写", anchor.Message);
    }

    private static Station FirstStation() => new()
    {
        Id = 10,
        Code = "ST010",
        Name = "首站",
        Sequence = 1,
        IsFirstStation = true,
        PositionCount = 1,
        Enabled = true
    };
}
