using DataTrace.Shared;

namespace DataTrace.Tests;

/// <summary>
/// 数据盘剩余空间读数。
/// </summary>
/// <remarks>
/// 阈值判断分别在健康探针（判为不健康）与报警规则（判为要叫人）里，这里只管读数本身：
/// 它必须能区分"确实还剩多少"和"读不到"，不能把后者折叠成一个看着充足的数字。
/// </remarks>
public class DiskSpaceTests
{
    [Fact]
    public void Reports_free_space_for_the_working_directory()
    {
        var free = DiskSpace.FreeMegabytesOf(AppContext.BaseDirectory);

        Assert.NotNull(free);
        Assert.True(free > 0, $"程序目录所在卷报告剩余 {free} MB，这台机器不足以跑这条断言");
    }

    [Fact]
    public void Unknown_volume_reads_as_null_instead_of_a_large_number()
    {
        // 挑一个本机不存在的盘符，避免 CI 上正好有 Z: 这类巧合。
        char? missing = null;
        foreach (var letter in "ZYXWVUTSRQPONMLKJIHGFEDCBA")
        {
            if (!Directory.Exists($"{letter}:\\"))
            {
                missing = letter;
                break;
            }
        }

        Assert.True(missing is not null, "这台机器把 26 个盘符全占了，换一台跑这条用例");

        Assert.Null(DiskSpace.FreeMegabytesOf($"{missing}:\\datatrace-not-a-real-volume"));
    }
}
