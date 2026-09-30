using DataTrace.Web.Services;

namespace DataTrace.Web.Tests;

/// <summary>同一数据目录只允许一个实例：抢锁、释放后可重抢、不同目录互不影响。</summary>
public class SingleInstanceGuardTests : IDisposable
{
    private readonly string _baseDirectory = Path.Combine(
        Path.GetTempPath(),
        "datatrace-guard-tests",
        Guid.NewGuid().ToString("N"));

    private string Root => Path.Combine(_baseDirectory, "data");

    [Fact]
    public void SecondAcquireOnSameDataRootIsRefused()
    {
        using var first = SingleInstanceGuard.TryAcquire(Root);

        Assert.NotNull(first);
        Assert.Null(SingleInstanceGuard.TryAcquire(Root));
    }

    [Fact]
    public void ReleasedInstanceCanBeReacquired()
    {
        var first = SingleInstanceGuard.TryAcquire(Root);
        Assert.NotNull(first);

        first!.Dispose();

        // 进程异常退出时句柄由操作系统关掉，等价于这里的释放：锁不该留在盘上等人手工清。
        using var second = SingleInstanceGuard.TryAcquire(Root);
        Assert.NotNull(second);
    }

    [Fact]
    public void DifferentDataRootsDoNotConflict()
    {
        using var first = SingleInstanceGuard.TryAcquire(Root);
        using var other = SingleInstanceGuard.TryAcquire(Path.Combine(_baseDirectory, "another-data"));

        Assert.NotNull(first);
        Assert.NotNull(other);
    }

    [Fact]
    public void HolderPidIsReadableWhileLocked()
    {
        using var first = SingleInstanceGuard.TryAcquire(Root);

        // 抢占失败的一方要能说出"是谁占着"，而不是只回一句"被占用"。
        Assert.Equal(Environment.ProcessId, SingleInstanceGuard.ReadHolderPid(Root));
    }

    [Fact]
    public void HolderPidIsNullWhenNothingIsLocked()
    {
        Assert.Null(SingleInstanceGuard.ReadHolderPid(Root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDirectory))
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
    }
}