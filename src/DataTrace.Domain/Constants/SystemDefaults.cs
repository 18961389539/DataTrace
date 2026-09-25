namespace DataTrace.Domain.Constants;

/// <summary>
/// 跨层共用的运行时约定值。
/// </summary>
/// <remarks>
/// 这些数字以前散在页面常量、后台服务的字面量与界面文案里，改一处、别处继续说旧话；
/// 尤其是概念详解（HelpTexts）直接把它们写进了 tooltip 文本，一旦配置变更就会对现场说谎。
/// 所以：<b>凡是会在界面上被复述的数字，都必须从这里引用</b>。
/// 与 <see cref="Validation.SettingsLimits"/> 的分工：那边是"输入框允许的范围"（用来拦非法输入），
/// 这边是"系统实际按什么值跑"（用来解释行为）。
/// </remarks>
public static class SystemDefaults
{
    /// <summary>看板判定"数据已停止更新"的心跳超时。</summary>
    public const int StaleHeartbeatSeconds = 30;

    /// <summary>最近节拍超过它转黄。与 StaleHeartbeatSeconds 同值但语义独立，不要合并。</summary>
    public const int CadenceWarnSeconds = 30;

    /// <summary>最近节拍超过它转灰（产线本就可能长时间没料）。</summary>
    public const int CadenceIdleSeconds = 300;

    /// <summary>单次导出查询结果的最大条数。</summary>
    public const int ExportRowLimit = 10_000;

    /// <summary>参数趋势与过程能力单次最多统计的采样点数。</summary>
    public const int TrendSampleLimit = 20_000;

    /// <summary>连续登录失败多少次后锁定账号。</summary>
    public const int LockoutMaxFailedAttempts = 5;

    /// <summary>锁定时长（分钟），到点自动解除。</summary>
    public const int LockoutMinutes = 5;

    /// <summary>数据保留任务两次检查之间的间隔（小时）。</summary>
    public const int RetentionCheckHours = 6;

    /// <summary>数据库备份超过它没成功就算"陈旧"，看板与设置页都会告警（小时）。</summary>
    public const int BackupStaleHours = 48;

    /// <summary>MES 推送积压超过它就在界面上红字告警（小时）。</summary>
    public const int MesBacklogWarnHours = 1;
}
