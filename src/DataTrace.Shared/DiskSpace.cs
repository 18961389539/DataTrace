namespace DataTrace.Shared;

/// <summary>
/// 数据盘剩余空间。健康探针与异常呼叫共用这一处实现。
/// </summary>
/// <remarks>
/// 两个调用方各读一次盘本来也无妨，但它们会各自决定"读不到时算好还是算坏"，
/// 于是同一台机器可能同时出现"探活说没问题、报警说磁盘不足"。口径收在这里，只有一处判断。
/// </remarks>
public static class DiskSpace
{
    private const long BytesPerMegabyte = 1024L * 1024L;

    /// <summary>
    /// 目录所在卷的剩余空间（MB）。取不到返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 刻意不把失败折叠成 0 或 <see cref="long.MaxValue"/>：本系统吃过"把故障读成空数据"的亏
    /// （报警页清空后与错误提示同屏自相矛盾）。调用方必须显式处理"不知道"这一种情形。
    /// </remarks>
    public static long? FreeMegabytesOf(string directory)
    {
        try
        {
            // 取到卷根再问 DriveInfo：直接把它指向 data 子目录会抛 ArgumentException。
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace / BytesPerMegabyte : null;
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or System.Security.SecurityException)
        {
            return null;
        }
    }
}
