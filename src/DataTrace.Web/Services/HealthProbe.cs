using System.Data;
using System.Diagnostics;
using DataTrace.Application.Realtime;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Constants;
using DataTrace.Infrastructure.Backup;
using DataTrace.Infrastructure.Persistence;
using DataTrace.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DataTrace.Web.Services;

/// <summary>单个健康项的结论。<paramref name="Detail"/> 只放给人看的一句话，不回显安装路径。</summary>
public sealed record HealthCheck(string Name, bool Healthy, string Detail);

/// <summary>
/// 探活报告。<c>Status</c> 为 <c>healthy</c> / <c>unhealthy</c>，供守护进程与监控系统直接判读。
/// </summary>
public sealed record HealthReport(
    string Status,
    string Version,
    string Commit,
    string Environment,
    double UptimeSeconds,
    IReadOnlyList<HealthCheck> Checks);

/// <summary>
/// <c>/healthz</c> 的判据：只回答"现在能不能干活"，不查业务数据。
/// </summary>
/// <remarks>
/// 独立检查，任何一项失败即视为不健康：
/// <list type="bullet">
/// <item><c>config-db</c>：配置库连得上、读得动。</item>
/// <item><c>data-root</c> / <c>runtime-dir</c>：数据盘可写 —— 装到 Program Files 下、或数据盘掉线时，
/// 采集会在落库那一步才炸，探活要能提前发现。</item>
/// <item><c>disk-free</c>：数据盘还剩多少。可写只说明"此刻写得进去"，不说明"还能写多久"，
/// 而按月分库 + 曲线文件 + 归档是持续吃盘的。</item>
/// <item><c>collector</c>：采集器主循环的心跳还新鲜。心跳停了意味着 PLC 扫描不再推进，
/// 界面看起来一切正常但数据早就停更了。</item>
/// <item><c>collection-readiness</c>：启动时的会话/持久化对账已完成。该项独立于心跳：循环可以仍然活着，
/// 但只要未完成安全对账，扫描就必须保持阻塞。</item>
/// <item><c>collection-stations</c>：没有工站处于明确的 <see cref="StationRuntimeState.Fault"/> 状态。
/// 不对 Idle、Busy 或 Disabled 猜测超时。</item>
/// </list>
/// 失败明细使用固定短语，不向未认证的调用方回显异常、路径或启动对账细节；完整异常写到本机日志。
/// </remarks>
/// <summary>
/// 探活能力的端口。
/// </summary>
/// <remarks>
/// 抽出来是为了让诊断页可测：它需要能在测试里塞一份假报告，而 <see cref="HealthProbe"/> 本身
/// 要连着配置库、数据目录与采集器心跳才构得出来。
/// </remarks>
public interface IHealthProbe
{
    Task<HealthReport> CheckAsync(CancellationToken cancellationToken = default);
}

public sealed class HealthProbe : IHealthProbe
{
    /// <summary>采集器心跳超过这个时长即判定停摆。主循环按配置的扫描间隔跑，正常是毫秒级。</summary>
    private static readonly TimeSpan CollectorStaleAfter = TimeSpan.FromSeconds(60);

    private static readonly Stopwatch Uptime = Stopwatch.StartNew();

    private readonly IDbContextFactory<ConfigDbContext> _configFactory;
    private readonly DataRootPaths _paths;
    private readonly IRuntimeStatusHub _status;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<HealthProbe> _logger;

    public HealthProbe(
        IDbContextFactory<ConfigDbContext> configFactory,
        DataRootPaths paths,
        IRuntimeStatusHub status,
        IHostEnvironment environment,
        ILogger<HealthProbe> logger)
    {
        _configFactory = configFactory;
        _paths = paths;
        _status = status;
        _environment = environment;
        _logger = logger;
    }

    public async Task<HealthReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<HealthCheck>
        {
            await CheckConfigDatabaseAsync(cancellationToken).ConfigureAwait(false),
            CheckDirectoryWritable("data-root", _paths.Root),
            CheckDirectoryWritable("runtime-dir", _paths.RuntimeDirectory),
            CheckDiskFree(_paths.Root),
            CheckCollector(),
            CheckCollectionReadiness(),
            CheckCollectionStations()
        };

        var healthy = checks.TrueForAll(check => check.Healthy);
        return new HealthReport(
            healthy ? "healthy" : "unhealthy",
            BuildInfo.Version,
            BuildInfo.Commit,
            _environment.EnvironmentName,
            Math.Round(Uptime.Elapsed.TotalSeconds, 1),
            checks);
    }

    private async Task<HealthCheck> CheckConfigDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _configFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1;";
            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return new HealthCheck("config-db", true, "ok");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check could not query the configuration database");
            return new HealthCheck("config-db", false, "配置库检查失败");
        }
    }

    private HealthCheck CheckDirectoryWritable(string name, string directory)
    {
        var probe = Path.Combine(directory, $".health-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(probe, "ok");
            return new HealthCheck(name, true, "ok");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check could not write to directory {DirectoryName}", name);
            return new HealthCheck(name, false, name == "runtime-dir" ? "运行数据目录不可写" : "数据目录不可写");
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch
            {
                // 探测文件清理失败不该反过来把健康检查判红。
            }
        }
    }

    /// <summary>
    /// 数据盘剩余空间。写满之后 SQLite 才开始落库失败，而那时现场看到的是"采集莫名停了"，
    /// 与磁盘早就没关系了 —— 所以这一项要在写满之前就叫。
    /// </summary>
    /// <remarks>
    /// 读不到剩余空间（网络盘未就绪、权限不足）判为**不健康**：宁可误报，也不要在
    /// "不知道还剩多少"的时候对外说一切正常。
    /// </remarks>
    private static HealthCheck CheckDiskFree(string directory)
    {
        if (DiskSpace.FreeMegabytesOf(directory) is not { } freeMegabytes)
        {
            return new HealthCheck("disk-free", false, "读不到数据盘剩余空间");
        }

        return freeMegabytes >= SystemDefaults.MinDiskFreeMegabytes
            ? new HealthCheck("disk-free", true, "ok")
            : new HealthCheck(
                "disk-free",
                false,
                $"数据盘剩余 {freeMegabytes} MB，低于 {SystemDefaults.MinDiskFreeMegabytes} MB");
    }

    private HealthCheck CheckCollector()
    {
        var age = DateTime.Now - _status.LastCollectorAt;
        return age <= CollectorStaleAfter
            ? new HealthCheck("collector", true, "ok")
            : new HealthCheck(
                "collector",
                false,
                $"采集器已 {age.TotalSeconds:F0} 秒没有心跳（阈值 {CollectorStaleAfter.TotalSeconds:F0} 秒）");
    }

    private HealthCheck CheckCollectionReadiness()
    {
        var readiness = _status.CollectionReadiness;
        if (readiness.Ready)
        {
            return new HealthCheck("collection-readiness", true, "ok");
        }

        return new HealthCheck("collection-readiness", false, "启动核对未完成，采集已阻止");
    }

    private HealthCheck CheckCollectionStations()
    {
        var faultCount = _status.Stations.Count(station => station.State == StationRuntimeState.Fault);
        return faultCount == 0
            ? new HealthCheck("collection-stations", true, "ok")
            : new HealthCheck("collection-stations", false, $"{faultCount} 个工站处于故障状态");
    }
}
