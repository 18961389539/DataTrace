namespace DataTrace.Domain.Entities;

public class SystemSettings
{
    public int Id { get; set; }
    public int ScanIntervalMs { get; set; } = 80;
    public int WriteRetryCount { get; set; } = 3;
    public int WriteRetryDelayMs { get; set; } = 50;
    public int RetentionYears { get; set; } = 3;
    public string CurveRootPath { get; set; } = "data/curves";
    public string SpoolPath { get; set; } = "data/spool";
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
}
