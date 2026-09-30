using DataTrace.Shared;

namespace DataTrace.Tests;

/// <summary>
/// 托盘全链路时序的间隔计算。
/// </summary>
/// <remarks>
/// 这里断言的都是"缺数据时不能显示成 0"这一类边界：一串看起来很小的间隔会把
/// "这一段根本没采到"说成"走得很快"，方向刚好反了。
/// </remarks>
public class PalletTimelineTests
{
    private static readonly DateTime Base = new(2026, 9, 30, 10, 0, 0);

    [Fact]
    public void Transit_is_the_gap_between_previous_complete_and_current_trigger()
    {
        var span = PalletTimeline.Transit(Base, Base.AddSeconds(12.5));

        Assert.Equal(TimeSpan.FromSeconds(12.5), span);
    }

    [Fact]
    public void Transit_is_zero_when_both_timestamps_are_equal()
        => Assert.Equal(TimeSpan.Zero, PalletTimeline.Transit(Base, Base));

    /// <summary>首站没有上一站；缺失必须是 null，不能回落成 0。</summary>
    [Fact]
    public void Transit_is_null_without_a_previous_completion()
    {
        Assert.Null(PalletTimeline.Transit(null, Base));
        Assert.Null(PalletTimeline.Transit(null, null));
    }

    [Fact]
    public void Transit_is_null_without_a_current_trigger()
        => Assert.Null(PalletTimeline.Transit(Base, null));

    /// <summary>时钟回拨、跨设备对时不一致时不能算出负数间隔。</summary>
    [Fact]
    public void Transit_is_null_when_the_clock_goes_backwards()
        => Assert.Null(PalletTimeline.Transit(Base, Base.AddSeconds(-1)));

    [Fact]
    public void Total_spans_first_trigger_to_last_complete()
        => Assert.Equal(
            TimeSpan.FromMinutes(3),
            PalletTimeline.Total(Base, Base.AddMinutes(3)));

    [Theory]
    [InlineData(0.25, 0.25)]
    [InlineData(1.0, 1.0)]
    public void Share_is_the_fraction_of_the_whole(double partSeconds, double expected)
        => Assert.Equal(
            expected,
            PalletTimeline.Share(TimeSpan.FromSeconds(partSeconds), TimeSpan.FromSeconds(1)),
            precision: 6);

    [Fact]
    public void Share_is_clamped_and_never_throws_on_a_bad_whole()
    {
        // 单段比总长还长（记录跨了会话边界之类的脏数据）：夹到 1，不抛。
        Assert.Equal(1, PalletTimeline.Share(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)));
        Assert.Equal(0, PalletTimeline.Share(TimeSpan.FromSeconds(5), TimeSpan.Zero));
        Assert.Equal(0, PalletTimeline.Share(TimeSpan.FromSeconds(5), null));
        Assert.Equal(0, PalletTimeline.Share(null, TimeSpan.FromSeconds(5)));
    }
}
