using DataTrace.Application.Evaluation;

namespace DataTrace.Infrastructure.Evaluation;

/// <summary>
/// 波形基线缓存的内存实现。
/// </summary>
/// <remarks>
/// 用"整体替换引用"而不是就地增删：采集线程只会读到某个完整一致的快照，
/// 不会看到刷新到一半的状态（一半新模板一半旧模板），也不需要加锁。
/// </remarks>
public sealed class CurveBaselineCache : ICurveBaselineCache
{
    private CurveBaselineSnapshot? _current;

    public CurveBaselineSnapshot? Current => Volatile.Read(ref _current);

    public void Replace(CurveBaselineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }
}
