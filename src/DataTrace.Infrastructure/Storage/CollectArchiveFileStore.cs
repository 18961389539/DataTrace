using DataTrace.Application.Runtime;

namespace DataTrace.Infrastructure.Storage;

/// <summary>
/// 文件源工站的原始 JSON 归档：按 年/月/日 分目录存放，原样字节落盘（不压缩、不重新序列化），
/// 记录里只留相对路径、大小与 CRC32。
/// </summary>
/// <remarks>
/// 命名用「采集时间 + PLC 读到的托盘码 + 工站」—— JSON 里没有序列号/托盘码，
/// 而设备每件覆写同一个固定文件，所以路径必须自己带唯一性：
/// 撞名时像曲线一样让开一格，绝不覆盖（否则回滚与保留策略删掉的会是别人的那一份）。
/// </remarks>
public sealed class CollectArchiveFileStore : ICollectArchiveStore
{
    private readonly string _root;

    public CollectArchiveFileStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public async Task<(string RelativePath, long FileSize, uint Crc32)> WriteAsync(
        DateTime triggerTime,
        string palletCode,
        int stationId,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        var relative = Path.Combine(
            triggerTime.ToString("yyyy"),
            triggerTime.ToString("MM"),
            triggerTime.ToString("dd"),
            $"{triggerTime:HHmmssfff}_{Sanitize(palletCode)}_{stationId}.json");
        var finalFull = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(finalFull)!);
        finalFull = AvailablePath(finalFull);

        // 先写临时文件再改名：进程中途被打断时不会留下一个内容只写了一半、
        // 却已经记进库的"原始数据"。
        var tempFull = finalFull + ".tmp";
        await File.WriteAllBytesAsync(tempFull, content, cancellationToken).ConfigureAwait(false);
        File.Move(tempFull, finalFull, overwrite: true);

        return (Path.GetRelativePath(_root, finalFull).Replace('\\', '/'), content.LongLength, Crc32Util.Compute(content));
    }

    public async Task<byte[]> ReadAsync(string relativePath, uint? expectedCrc = null, CancellationToken cancellationToken = default)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);

        // 损坏与缺失必须区分开：都报"文件缺失"的话，现场会去查备份而不是查磁盘。
        if (expectedCrc is { } expected && Crc32Util.Compute(bytes) != expected)
        {
            throw new InvalidDataException($"归档文件校验失败：CRC 与入库值不一致（{relativePath}）");
        }

        return bytes;
    }

    public Task DeleteFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(full))
        {
            File.Delete(full);
        }

        return Task.CompletedTask;
    }

    public Task DeleteMonthAsync(string yyyy, string mm, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(_root, yyyy, mm);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var text = new string(chars).Trim();
        return text.Length == 0 ? "nopallet" : text;
    }

    /// <summary>路径被占用时依次尝试 <c>-2</c>、<c>-3</c>… 后缀，返回一个当前不存在的路径。</summary>
    private static string AvailablePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(directory, $"{stem}-{i}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}