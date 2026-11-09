using System.Reflection;
using System.Text.RegularExpressions;
using DataTrace.Shared;
using Xunit;

namespace DataTrace.Tests;

/// <summary>
/// 概念 tooltip 的覆盖率守卫：业务页面至少九成要有字段级 ⓘ 和页级说明，
/// 文案库至少九成要真的挂到界面上。只统计可路由的业务页面，登录/错误页与
/// 未挂 @page 的内部组件不参与分母。
/// </summary>
public sealed class TooltipCoverageTests
{
    private const double MinimumCoverage = 0.90;

    private static readonly string[] OperationalPages =
    [
        "Alarms.razor",
        "CurveBaseline.razor",
        "Dashboard.razor",
        "Diagnostics.razor",
        "Logs.razor",
        "McpTokens.razor",
        "PlcConfig.razor",
        "QualityIncidentClosedReport.razor",
        "QualityIncidentDetail.razor",
        "QualityIncidents.razor",
        "QualityIncidentNew.razor",
        "Query.razor",
        "Recipes.razor",
        "RecordDetail.razor",
        "Reports.razor",
        "Settings.razor",
        "Simulate.razor",
        "Stations.razor",
        "Users.razor",
        "WorkOrders.razor"
    ];

    [Fact]
    public void Operational_pages_meet_the_90_percent_tooltip_target()
    {
        var pages = OperationalPages
            .Select(name => Path.Combine(FindRepoRoot(), "src", "DataTrace.Web", "Components", "Pages", name))
            .ToList();

        Assert.All(pages, page => Assert.True(File.Exists(page), $"找不到页面文件：{page}"));

        var withHint = pages
            .Where(page => Regex.IsMatch(File.ReadAllText(page), "InfoHint\\s+Topic=\"HelpTexts\\."))
            .ToList();
        var withHelp = pages
            .Where(page => Regex.IsMatch(File.ReadAllText(page), "HelpRoute="))
            .ToList();

        Assert.True(
            withHint.Count / (double)pages.Count >= MinimumCoverage,
            $"字段级 tooltip 覆盖不足：{withHint.Count}/{pages.Count}。缺少：{Missing(pages, withHint)}");
        Assert.True(
            withHelp.Count / (double)pages.Count >= MinimumCoverage,
            $"页级说明覆盖不足：{withHelp.Count}/{pages.Count}。缺少：{Missing(pages, withHelp)}");
    }

    [Fact]
    public void Help_topics_are_wired_to_the_ui_above_the_target()
    {
        var root = FindRepoRoot();
        var defined = typeof(HelpTexts)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(HelpTopic))
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, "src", "DataTrace.Web", "Components"),
                     "*.razor",
                     SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, "(?:Topic|Hint)=\"HelpTexts\\.([A-Za-z0-9_]+)\""))
            {
                referenced.Add(match.Groups[1].Value);
            }
        }

        var wired = defined.Count(referenced.Contains);
        var unused = defined.Where(topic => !referenced.Contains(topic)).OrderBy(topic => topic).ToList();

        Assert.True(
            wired / (double)defined.Count >= MinimumCoverage,
            $"HelpTexts 挂载率不足：{wired}/{defined.Count}。未挂载：{string.Join(", ", unused)}");
        Assert.True(
            referenced.All(defined.Contains),
            $"界面引用了不存在的 HelpTexts 主题：{string.Join(", ", referenced.Where(topic => !defined.Contains(topic)))}");
    }

    private static string Missing(IReadOnlyList<string> all, IReadOnlyList<string> covered)
        => string.Join(", ", all.Except(covered).Select(Path.GetFileName));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DataTrace.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException(
                   $"从 {AppContext.BaseDirectory} 往上找不到 DataTrace.sln，无法定位页面源码。");
    }
}
