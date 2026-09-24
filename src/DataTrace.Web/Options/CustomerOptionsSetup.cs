using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace DataTrace.Web.Options;

/// <summary>
/// 合并 Customer 配置节与安装目录 customer.json 的扁平字段（CustomerId/SiteName/LogoPath/SimulatorAutoRun/MaxStations）。
/// </summary>
public sealed class CustomerOptionsSetup : IConfigureOptions<CustomerOptions>
{
    private readonly IConfiguration _configuration;

    public CustomerOptionsSetup(IConfiguration configuration) => _configuration = configuration;

    public void Configure(CustomerOptions options)
    {
        _configuration.GetSection(CustomerOptions.SectionName).Bind(options);

        Overlay(options, "CustomerId", v => options.CustomerId = v);
        Overlay(options, "SiteName", v => options.SiteName = v);
        Overlay(options, "LogoPath", v => options.LogoPath = v);
        Overlay(options, "LogoUrl", v => options.LogoUrl = v);

        var sim = _configuration["SimulatorAutoRun"];
        if (string.IsNullOrWhiteSpace(sim))
        {
            sim = _configuration["Customer:SimulatorAutoRun"];
        }

        if (!string.IsNullOrWhiteSpace(sim) && bool.TryParse(sim, out var autoRun))
        {
            options.SimulatorAutoRun = autoRun;
        }

        OverlayInt("MaxStations", "Customer:MaxStations", v => options.MaxStations = v);
        OverlayInt("MaxPlcs", "Customer:MaxPlcs", v => options.MaxPlcs = v);
    }

    private void Overlay(CustomerOptions options, string key, Action<string> assign)
    {
        var flat = _configuration[key];
        if (!string.IsNullOrWhiteSpace(flat))
        {
            assign(flat.Trim());
        }
    }

    private void OverlayInt(string flatKey, string sectionKey, Action<int> assign)
    {
        var raw = _configuration[flatKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = _configuration[sectionKey];
        }

        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out var n) && n > 0)
        {
            assign(n);
        }
    }
}