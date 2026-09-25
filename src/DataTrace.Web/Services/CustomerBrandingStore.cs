using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DataTrace.Web.Options;
using Microsoft.Extensions.Options;

namespace DataTrace.Web.Services;

/// <summary>
/// 运行时品牌与部署身份：启动时从配置装载；保存厂名/Logo 时合并写入 customer.json。
/// </summary>
public sealed class CustomerBrandingStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    private readonly IWebHostEnvironment _env;
    private readonly ILogger<CustomerBrandingStore> _logger;
    private readonly object _gate = new();
    private CustomerOptions _snapshot;

    public CustomerBrandingStore(
        IOptions<CustomerOptions> options,
        IWebHostEnvironment env,
        ILogger<CustomerBrandingStore> logger)
    {
        _env = env;
        _logger = logger;
        _snapshot = Clone(options.Value);
    }

    public string ResolvedDataRoot { get; set; } = "data";

    public string? BindUrl { get; set; }

    public string EnvironmentName => _env.EnvironmentName;

    public string ContentRoot => _env.ContentRootPath;

    public string CustomerJsonPath => Path.Combine(_env.ContentRootPath, "customer.json");

    /// <summary>Assembly InformationalVersion (Directory.Build.props Version), e.g. 1.1.0.</summary>
    public string AppVersion
    {
        get
        {
            var asm = typeof(CustomerBrandingStore).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }

            return asm.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    public CustomerOptions GetSnapshot()
    {
        lock (_gate)
        {
            return Clone(_snapshot);
        }
    }

    public void SaveBranding(string siteName, string? logoPath)
    {
        lock (_gate)
        {
            var path = CustomerJsonPath;
            JsonObject root;
            if (File.Exists(path))
            {
                var raw = File.ReadAllText(path);
                root = JsonNode.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw) as JsonObject
                       ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var name = siteName?.Trim() ?? "";
            root["SiteName"] = name;
            if (string.IsNullOrWhiteSpace(logoPath))
            {
                root.Remove("LogoPath");
            }
            else
            {
                root["LogoPath"] = logoPath.Trim();
            }

            if (string.IsNullOrWhiteSpace(root["CustomerId"]?.GetValue<string>())
                && !string.IsNullOrWhiteSpace(_snapshot.CustomerId))
            {
                root["CustomerId"] = _snapshot.CustomerId;
            }

            // Keep version stamp visible in customer.json for ops (upgrade.ps1 also writes it)
            root["AppVersion"] = AppVersion;
            root["UpdatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            File.WriteAllText(path, root.ToJsonString(JsonOpts) + Environment.NewLine);

            _snapshot.SiteName = name;
            _snapshot.LogoPath = string.IsNullOrWhiteSpace(logoPath) ? null : logoPath.Trim();
            _logger.LogInformation("已保存品牌配置到 {Path}: SiteName={SiteName}", path, _snapshot.EffectiveSiteName);
        }
    }

    /// <summary>Logo 文件所在的安装目录子目录（静态文件中间件以 /branding 前缀提供）。</summary>
    public string BrandingDirectory => Path.Combine(_env.ContentRootPath, "branding");

    /// <summary>
    /// Logo 文件名指向的文件是否真的存在；不存在时返回一句给用户看的提示，存在或未填返回 null。
    /// </summary>
    /// <remarks>
    /// 填错文件名不会有任何报错，顶栏只是"什么都不显示"（img 的 alt 为空），
    /// 现场很难判断是文件没放上去还是配置没生效，所以保存时提示一句。
    /// </remarks>
    public string? LogoFileWarning(string? logoPath)
    {
        if (string.IsNullOrWhiteSpace(logoPath))
        {
            return null;
        }

        var name = logoPath.Trim().Replace('\\', '/').TrimStart('/');
        if (name.StartsWith("branding/", StringComparison.OrdinalIgnoreCase))
        {
            name = name["branding/".Length..];
        }

        // 带 ".." 的路径交给静态文件中间件兜底（它被限制在 branding 目录内），这里不误报。
        if (name.Length == 0 || name.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        return File.Exists(Path.Combine(BrandingDirectory, name))
            ? null
            : $"branding 目录下没有「{name}」，顶栏不会显示 Logo（文件名区分大小写与扩展名）";
    }

    private static CustomerOptions Clone(CustomerOptions src) => new()
    {
        CustomerId = src.CustomerId,
        SiteName = src.SiteName,
        LogoPath = src.LogoPath,
        LogoUrl = src.LogoUrl,
        SimulatorAutoRun = src.SimulatorAutoRun,
        MaxStations = src.MaxStations,
        MaxPlcs = src.MaxPlcs
    };
}