using DataTrace.Domain.Constants;
using DataTrace.Plc.Queue;

namespace DataTrace.Tests;

/// <summary>
/// PLC 请求流水的环形缓冲。
/// </summary>
/// <remarks>
/// 容量与顺序是诊断页"最近发生了什么"的读法基础：读反了会让现场把最老的一条当成刚才那一条。
/// </remarks>
public class PlcTrafficTests
{
    private static PlcExchange Exchange(int index, bool ok = true)
        => new(
            new DateTime(2026, 9, 30, 10, 0, 0).AddMilliseconds(index),
            PlcExchangeKind.Read,
            $"D{100 + index}",
            1,
            index,
            ok,
            ok ? null : $"错误 {index}",
            [1, 2]);

    [Fact]
    public void Snapshot_keeps_newest_first()
    {
        var log = new PlcTrafficLog();
        log.Record(Exchange(1));
        log.Record(Exchange(2));

        var snapshot = log.Snapshot();

        Assert.Equal(new[] { "D102", "D101" }, snapshot.Recent.Select(x => x.Address).ToArray());
        Assert.Equal(2, snapshot.TotalCount);
    }

    [Fact]
    public void Old_entries_are_dropped_once_capacity_is_reached()
    {
        var log = new PlcTrafficLog();
        for (var i = 0; i < PlcTrafficLog.Capacity + 5; i++)
        {
            log.Record(Exchange(i));
        }

        var snapshot = log.Snapshot();

        Assert.Equal(PlcTrafficLog.Capacity, snapshot.Recent.Count);
        Assert.Equal(PlcTrafficLog.Capacity + 5, snapshot.TotalCount);
        // 累计计数不随环形缓冲清掉：失败率的分母要是全部请求，不是最近这几十条。
        Assert.Equal($"D{100 + PlcTrafficLog.Capacity + 4}", snapshot.Recent[0].Address);
        Assert.Equal("D105", snapshot.Recent[^1].Address);
    }

    [Fact]
    public void Failures_and_durations_are_tracked_across_the_whole_log()
    {
        var log = new PlcTrafficLog();
        log.Record(Exchange(10));
        log.Record(Exchange(30, ok: false));
        log.Record(Exchange(20));

        var snapshot = log.Snapshot();

        Assert.Equal(3, snapshot.TotalCount);
        Assert.Equal(1, snapshot.FailureCount);
        Assert.Equal(20, snapshot.LastDurationMs);
        Assert.Equal(30, snapshot.MaxDurationMs);
    }

    [Fact]
    public void Empty_log_reports_nothing_instead_of_a_default_duration()
    {
        var snapshot = new PlcTrafficLog().Snapshot();

        Assert.Equal(0, snapshot.TotalCount);
        Assert.Equal(0, snapshot.MaxDurationMs);
        Assert.Empty(snapshot.Recent);
    }

    /// <summary>
    /// "上次成功"只跟着成功走：失败连片时它停在最后一次成功上，正是断线时最该看的那个数字。
    /// </summary>
    [Fact]
    public void Last_success_follows_successful_exchanges_only()
    {
        var log = new PlcTrafficLog();

        log.Record(Exchange(10));
        log.Record(Exchange(30, ok: false));
        log.Record(Exchange(20, ok: false));

        Assert.Equal(Exchange(10).At, log.Snapshot().LastSuccessAt);

        log.Record(Exchange(40));

        Assert.Equal(Exchange(40).At, log.Snapshot().LastSuccessAt);
    }

    /// <summary>一次都没成功过（或压根没请求过）时是 null：页面据此说"从未成功"，而不是显示一个 0 时刻。</summary>
    [Fact]
    public void Last_success_is_null_until_the_first_success()
    {
        var log = new PlcTrafficLog();
        Assert.Null(log.Snapshot().LastSuccessAt);

        log.Record(Exchange(1, ok: false));
        Assert.Null(log.Snapshot().LastSuccessAt);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(20, PlcTrafficLog.ValuesKept)]
    public void Values_are_trimmed_to_the_kept_length(int provided, int expected)
    {
        var values = Enumerable.Range(1, provided).Select(i => (ushort)i).ToArray();

        Assert.Equal(expected, PlcTrafficLog.Trim(values).Count);
    }

    [Fact]
    public void Trim_accepts_a_missing_payload()
        => Assert.Empty(PlcTrafficLog.Trim(null));

    [Fact]
    public void Capacity_comes_from_the_shared_defaults_because_the_help_text_quotes_it()
        => Assert.Equal(SystemDefaults.PlcTrafficCapacity, PlcTrafficLog.Capacity);

    /// <summary>
    /// 失败要能活过"被成功请求挤出流水窗口"这件事。
    /// </summary>
    /// <remarks>
    /// 这是诊断页出现过的一个真实状态：标题写着"失败 1"，展开后 40 条里一条失败都没有 ——
    /// 因为那次偶发故障早被高频成功请求挤掉了。
    /// </remarks>
    [Fact]
    public void Failures_survive_being_pushed_out_of_the_recent_window()
    {
        var log = new PlcTrafficLog();
        log.Record(Exchange(0, ok: false));

        for (var i = 1; i <= PlcTrafficLog.Capacity + 5; i++)
        {
            log.Record(Exchange(i));
        }

        var snapshot = log.Snapshot();

        Assert.DoesNotContain(snapshot.Recent, entry => !entry.Ok);
        var kept = Assert.Single(snapshot.RecentFailures);
        Assert.Equal("D100", kept.Address);
        Assert.Equal(1, snapshot.FailureCount);
    }

    [Fact]
    public void Kept_failure_list_is_bounded_and_newest_first()
    {
        var log = new PlcTrafficLog();
        for (var i = 0; i < PlcTrafficLog.FailureCapacity + 5; i++)
        {
            log.Record(Exchange(i, ok: false));
        }

        var failures = log.Snapshot().RecentFailures;

        Assert.Equal(PlcTrafficLog.FailureCapacity, failures.Count);
        // 最新在前：现场要看的是"刚才那条失败"，不是最早那条。
        Assert.Equal($"D{100 + PlcTrafficLog.FailureCapacity + 4}", failures[0].Address);
    }

    [Fact]
    public void Failure_capacity_comes_from_the_shared_defaults()
        => Assert.Equal(SystemDefaults.PlcTrafficFailureCapacity, PlcTrafficLog.FailureCapacity);
}
