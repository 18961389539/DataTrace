using System.Globalization;
using System.Text;

namespace DataTrace.Web.Services;

/// <summary>
/// 同一数据目录只允许一个 DataTrace 实例在跑。
/// </summary>
/// <remarks>
/// 采集、补传、备份与保留清理都按"一个进程独占 DataRoot"设计。双开时两个进程会同时扫
/// 同一台 PLC（同一件各拿一个流水号、入库两份）、互相抢补传队列、MES 重复推送，
/// 而两边日志各记各的，事后很难对上 —— 所以拿不到锁就明确报错退出，不带病运行。
/// <para>
/// 判定单位是<b>数据目录</b>而不是机器：一台工控机上装多个客户/多条线实例是正常部署，
/// 所以用数据目录里的独占锁文件判定，而不是命名互斥体。进程异常退出时操作系统会关掉
/// 句柄、锁随之释放，不会留下需要手工清理的死锁。
/// </para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>锁文件名：躺在数据目录根下，运维一眼能看出"这个目录有实例在用"。</summary>
    public const string LockFileName = ".datatrace.instance.lock";

    private readonly FileStream _handle;

    private SingleInstanceGuard(FileStream handle) => _handle = handle;

    /// <summary>
    /// 尝试独占数据目录。返回 <c>null</c> 表示已有实例在用它，调用方应明确报错后退出。
    /// </summary>
    public static SingleInstanceGuard? TryAcquire(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        var path = Path.Combine(dataRoot, LockFileName);

        FileStream handle;
        try
        {
            // FileShare.Read：只有本进程能写；允许别的进程读，是为了让抢占失败的一方
            // 能把 pid 读出来写进日志 —— 光说"已被占用"帮不了现场定位是谁占着。
            handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        }
        catch (IOException)
        {
            return null;
        }

        try
        {
            // pid 只是给人看的：写失败（例如磁盘满）不影响互斥本身，锁照常生效。
            handle.SetLength(0);
            handle.Write(Encoding.UTF8.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)));
            handle.Flush();
        }
        catch (IOException)
        {
        }

        return new SingleInstanceGuard(handle);
    }

    /// <summary>抢占失败时读一把"是谁占着"（占用者的进程号）。读不到就是读不到，不抛。</summary>
    public static int? ReadHolderPid(string dataRoot)
    {
        try
        {
            var path = Path.Combine(dataRoot, LockFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(handle, Encoding.UTF8);
            return int.TryParse(reader.ReadToEnd().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
                ? pid
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose() => _handle.Dispose();
}