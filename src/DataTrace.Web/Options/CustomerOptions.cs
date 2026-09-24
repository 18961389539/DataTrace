namespace DataTrace.Web.Options;

/// <summary>
/// 客户/站点部署身份与品牌：来自 appsettings*.json 的 Customer 节，以及安装目录 customer.json 扁平字段覆盖。
/// 一客户一套部署；多产线 = 多安装目录（或未来 SiteId），此处只承载本安装的厂名与演示开关。
/// </summary>
public sealed class CustomerOptions
{
    public const string SectionName = "Customer";

    /// <summary>客户标识，与安装目录名、服务名一致（如 ACME）。</summary>
    public string CustomerId { get; set; } = "";

    /// <summary>厂名/站点显示名，顶栏、登录页、浏览器标题使用。空则回退 DataTrace。</summary>
    public string SiteName { get; set; } = "";

    /// <summary>Logo 文件名或相对 branding\ 的路径（如 logo.png）。优先于 LogoUrl。</summary>
    public string? LogoPath { get; set; }

    /// <summary>Logo 完整 URL（外链或 /branding/...）。LogoPath 为空时使用。</summary>
    public string? LogoUrl { get; set; }

    /// <summary>
    /// 新建库时 SimulatorAutoRun 的默认值。
    /// Development 未配置时视为 true（演示）；Production 未配置时视为 false（现场安全）。
    /// 已有 config.db 不改写，仅影响首次 Seed。
    /// </summary>
    public bool? SimulatorAutoRun { get; set; }

    /// <summary>
    /// Soft station cap from customer.json (MaxStations). Warning-only in Settings; not DRM.
    /// Null = no soft limit.
    /// </summary>
    public int? MaxStations { get; set; }

    /// <summary>
    /// Soft PLC connection cap from customer.json (MaxPlcs). Warning-only; not DRM.
    /// Null = no soft limit.
    /// </summary>
    public int? MaxPlcs { get; set; }

    public string EffectiveSiteName =>
        string.IsNullOrWhiteSpace(SiteName) ? "DataTrace" : SiteName.Trim();

    public string? ResolvedLogoUrl
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(LogoPath))
            {
                var name = LogoPath.Trim().Replace('\\', '/').TrimStart('/');
                if (name.StartsWith("branding/", StringComparison.OrdinalIgnoreCase))
                {
                    return "/" + name;
                }

                return "/branding/" + name;
            }

            if (!string.IsNullOrWhiteSpace(LogoUrl))
            {
                return LogoUrl.Trim();
            }

            return null;
        }
    }
}