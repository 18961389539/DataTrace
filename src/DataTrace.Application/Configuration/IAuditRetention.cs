namespace DataTrace.Application.Configuration;

/// <summary>审计日志按保留年数归档后从在线库删除。采集侧只依赖这个端口。</summary>
public interface IAuditRetention
{
    Task<int> ArchiveAndPurgeAsync(int retentionYears, CancellationToken cancellationToken = default);
}
