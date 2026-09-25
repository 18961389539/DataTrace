namespace DataTrace.Domain.Entities;

public class SystemSettings
{
    public int Id { get; set; }
    public int ScanIntervalMs { get; set; } = 200;
    public int WriteRetryCount { get; set; } = 3;
    public int WriteRetryDelayMs { get; set; } = 50;
    public int RetentionYears { get; set; } = 3;

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
    /// 当前生效的产品型号（配方）。null = 使用点位自身默认限值。
    /// 由人在界面上手工切换；切换会自增配置版本，采集器下一次轮询即换用新限值。
    /// </summary>
    public int? ActiveRecipeId { get; set; }

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
