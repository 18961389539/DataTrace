using DataTrace.Domain.Constants;

namespace DataTrace.Domain.Entities;

public class SystemSettings
{
    public int Id { get; set; }
    public int ScanIntervalMs { get; set; } = 200;
    public int WriteRetryCount { get; set; } = 3;
    public int WriteRetryDelayMs { get; set; } = 50;
    public int RetentionYears { get; set; } = 3;
    /// <summary>审计日志保留年数；0 表示永久在线保留，不执行自动归档或清理。</summary>
    public int AuditRetentionYears { get; set; }

    /// <summary>
    /// 历史遗留列，<b>不生效</b>：曲线 / spool / 月库的实际路径来自安装目录的 customer.json
    /// （Customer:DataRoot → DataRootPaths），没有代码读取这三个字段。
    /// 保留列是为了不改动已部署库的表结构；改它们不会改变任何行为。
    /// </summary>
    public string CurveRootPath { get; set; } = "data/curves";

    /// <inheritdoc cref="CurveRootPath"/>
    public string SpoolPath { get; set; } = "data/spool";

    /// <inheritdoc cref="CurveRootPath"/>
    public string RuntimeDbPath { get; set; } = "data/runtime";

    public bool CollectEnabled { get; set; } = true;
    public bool MesEnabled { get; set; }
    public string? MesEndpoint { get; set; }
    public int MesTimeoutSeconds { get; set; } = 10;
    public bool SimulatorAutoRun { get; set; } = true;
    public int SimulatorIntervalMs { get; set; } = 2500;
    public int SimulatorNgPercent { get; set; } = 8;
    public int SimulatorPalletPool { get; set; } = 20;

    /// <summary>
    /// 下一件将使用的产品型号。null = 新件按点位默认限值。
    /// 已经在制的件仍用进首站时记下的型号，不随这次切换改变。
    /// </summary>
    public int? ActiveRecipeId { get; set; }

    /// <summary>第一班从当天这个整点开始。0–23。</summary>
    public int ShiftStartHour { get; set; } = SystemDefaults.ShiftStartHour;

    /// <summary>每一班多少小时。只能是 8、12 或 24。</summary>
    public int ShiftLengthHours { get; set; } = SystemDefaults.ShiftLengthHours;

    /// <summary>
    /// 异常呼叫地址。留空则只在已打开的页面响铃；填写后采集中断、MES 积压和连续 NG
    /// 还会 POST 到这个 http(s) 地址。
    /// </summary>
    public string? AlarmWebhookUrl { get; set; }

    /// <summary>
    /// 复制一份用于"先打补丁再保存"的副本。
    /// </summary>
    /// <remarks>
    /// 配置快照是全局共享的只读实例（见 <c>IConfigRepository.GetSnapshotAsync</c>），
    /// 页面上"取库里那一行、只改本页动过的字段、再整体保存"的写法必须作用在副本上：
    /// 直接改共享实例的话，哪怕保存失败，改动也会留在快照里被别的页面当成已保存的值读走。
    /// 本类型只有值类型与 string 成员，所以浅拷贝就是完整副本，新加字段也不会漏。
    /// </remarks>
    public SystemSettings Clone() => (SystemSettings)MemberwiseClone();
}
