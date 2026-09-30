using DataTrace.Application.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DataTrace.Collector;

/// <summary>
/// 补传的重放执行器：后台循环与诊断页的手动按钮共用同一个入口。
/// </summary>
/// <remarks>
/// 两条路径必须共用一把锁。以前只有后台循环在写，手动补传加进来之后，
/// 同一条缓存可能被"后台正在补"和"有人手点补传"同时写一遍 ——
/// 表现为库里的记录重复，而两边各自的日志都显示成功，事后完全对不上。
/// </remarks>
public sealed class SpoolReplayRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISpoolStore _spool;
    private readonly ILogger<SpoolReplayRunner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SpoolReplayRunner(
        IServiceScopeFactory scopeFactory,
        ISpoolStore spool,
        ILogger<SpoolReplayRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _spool = spool;
        _logger = logger;
    }

    /// <summary>
    /// 从最早的一条开始补，遇到第一个失败就停。
    /// </summary>
    /// <param name="cancellationToken">
    /// 页面上"取消"用。只在两条之间生效：取消时已补进去的不回退，剩下的留在队列里等下一轮。
    /// </param>
    /// <param name="onProgress">每成功补进去一条回调一次；页面拿它在按钮上显示进度。</param>
    /// <returns>本轮成功补进去的条数。</returns>
    /// <remarks>
    /// 失败即止是刻意的：队列里的失败几乎都是同一个原因（库盘满、月库锁住、目录权限掉了），
    /// 继续往下试只会把同一条异常日志刷满，还把失败次数平摊到每一条上，
    /// 让"卡了几次"这个数字失去意义。
    /// </remarks>
    public async Task<int> ReplayAllAsync(CancellationToken cancellationToken = default, Action<int>? onProgress = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = await _spool.ListAsync(cancellationToken).ConfigureAwait(false);
            var done = 0;
            foreach (var (file, request) in items)
            {
                // 取消只在两条之间生效：单条必须整件做完 —— 记录写进去了却没删掉缓存，
                // 下一轮补传会把它再写一遍，正是这把锁要防的那种重复。
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var result = await TryReplayAsync(file, request).ConfigureAwait(false);
                if (!result.Ok)
                {
                    break;
                }

                done++;
                onProgress?.Invoke(done);
            }

            return done;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>手动补传指定的一条。成败与原因直接回给页面。</summary>
    public async Task<SpoolReplayResult> ReplayOneAsync(string fileName, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 重新读一遍而不是让调用方把请求传进来：页面上的列表可能是几秒前的，
            // 而这条在此期间可能已经被后台补传成功并删掉了 —— 拿旧快照去写会造出一条重复记录。
            // 但只读这一条：队列大起来之后，为找它而把整队反序列化一遍，点击代价会随队列长度增长。
            var request = await _spool.ReadOneAsync(fileName, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return new SpoolReplayResult(false, "这条缓存已经不在了（多半刚被补传成功或已被清理）");
            }

            return await TryReplayAsync(fileName, request).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 尝试补传一条。
    /// </summary>
    /// <remarks>
    /// 不带取消令牌：这一条要整件做完（写库 + 删缓存）。半途丢下会在库里留一份、
    /// 缓存也还留在盘上，下一轮补传就会把它写第二遍 —— 取消留给两条之间，见 <see cref="ReplayAllAsync"/>。
    /// </remarks>
    private async Task<SpoolReplayResult> TryReplayAsync(string file, CollectSaveRequest request)
    {
        // 流水号与文件名都要带上：补传发生在另一个后台循环里，作用域早没了，
        // 只有这两个字段能把它接回"这件当初为什么没入库"那一行。
        var serial = request.Record.SerialNo;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var runtime = scope.ServiceProvider.GetRequiredService<ICollectWriter>();
            await runtime.SaveAsync(request).ConfigureAwait(false);
            await _spool.DeleteAsync(file).ConfigureAwait(false);
            _logger.LogInformation("补传成功 {SpoolFile} {Serial}", file, serial);
            return SpoolReplayResult.Success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "补传失败 {SpoolFile} {Serial}，稍后重试", file, serial);
            await _spool.NoteFailureAsync(file, ex.Message).ConfigureAwait(false);
            return new SpoolReplayResult(false, ex.Message);
        }
    }
}
