using System.Reflection;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Validation;
using DataTrace.Web.Services;

namespace DataTrace.Tests;

/// <summary>
/// 概念详解的文案守则。
/// </summary>
/// <remarks>
/// 这些约束不是审美问题，都是会真出事的：
/// - tooltip 是补充说明，写成长文就没人看，也会把浮层撑得满屏；
/// - ⓘ 的 aria-label 是「说明：<Title>」，而 E2E 用 GetByRole(Button, Name=…) 点按钮，
///   Playwright 的 Name 默认子串匹配 —— 名字里混进「保存」这类词会同时命中真按钮，
///   严格模式下当场失败；
/// - 「未保存」「未配置」两个词会让原本断言"页面上不该出现"的 E2E 用例变红
///   （UiRegressionE2ETests 的未保存胶囊、记录明细页的四道限值）。
/// 用反射枚举全部条目：新增一条文案自动纳入检查，不需要维护清单。
/// </remarks>
public class HelpTextsTests
{
    /// <summary>页面上有同名按钮的词，进 aria-label 后会和按钮抢定位器。</summary>
    private static readonly string[] WordsThatCollideWithButtonNames =
    [
        "保存", "重置", "查询", "编辑", "刷新报表", "复制托盘码", "展开或收起菜单", "显示密码"
    ];

    private static readonly string[] WordsThatBreakExistingE2E =
    [
        "未保存", "未配置"
    ];

    public static TheoryData<string, HelpTopic> AllTopics()
    {
        var data = new TheoryData<string, HelpTopic>();
        foreach (var (name, topic) in Enumerate())
        {
            data.Add(name, topic);
        }

        return data;
    }

    private static IEnumerable<(string Name, HelpTopic Topic)> Enumerate()
        => typeof(HelpTexts)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(HelpTopic))
            .Select(f => (f.Name, (HelpTopic)f.GetValue(null)!));

    [Fact]
    public void Every_topic_is_reachable_by_reflection()
    {
        // 反射是本类的检查入口，取不到条目就等于整组断言空转。
        Assert.NotEmpty(Enumerate());
    }

    [Theory]
    [MemberData(nameof(AllTopics))]
    public void Topic_has_all_three_parts(string name, HelpTopic topic)
    {
        Assert.False(string.IsNullOrWhiteSpace(topic.Title), $"{name} 缺 Title");
        Assert.False(string.IsNullOrWhiteSpace(topic.How), $"{name} 缺 How（怎么算或边界）");
        Assert.False(string.IsNullOrWhiteSpace(topic.Impact), $"{name} 缺 Impact（影响什么）");
    }

    [Theory]
    [MemberData(nameof(AllTopics))]
    public void Topic_stays_short_enough_to_be_read_in_a_tooltip(string name, HelpTopic topic)
    {
        Assert.True(topic.Title.Length <= 12, $"{name} 的 Title 有 {topic.Title.Length} 字，超过 12：{topic.Title}");
        Assert.True(topic.How.Length <= 60, $"{name} 的 How 有 {topic.How.Length} 字，超过 60：{topic.How}");
        Assert.True(topic.Impact.Length <= 60, $"{name} 的 Impact 有 {topic.Impact.Length} 字，超过 60：{topic.Impact}");
    }

    [Fact]
    public void Titles_are_unique_so_two_icons_never_read_the_same()
    {
        var duplicated = Enumerate()
            .GroupBy(x => x.Topic.Title)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.Empty(duplicated);
    }

    [Theory]
    [MemberData(nameof(AllTopics))]
    public void Title_avoids_words_that_collide_with_button_accessible_names(string name, HelpTopic topic)
        => Assert.All(
            WordsThatCollideWithButtonNames,
            word => Assert.False(
                topic.Title.Contains(word, StringComparison.Ordinal),
                $"{name} 的 Title「{topic.Title}」含「{word}」：ⓘ 的可访问名会与页面按钮撞名，"
                + "E2E 用 GetByRole(Button, Name=…) 定位时会命中两个元素"));

    [Theory]
    [MemberData(nameof(AllTopics))]
    public void No_copy_uses_words_that_existing_e2e_asserts_are_absent(string name, HelpTopic topic)
    {
        var all = $"{topic.Title}｜{topic.How}｜{topic.Impact}";
        Assert.All(
            WordsThatBreakExistingE2E,
            word => Assert.False(
                all.Contains(word, StringComparison.Ordinal),
                $"{name} 的文案含「{word}」：会撞上 E2E 里「页面上不该出现这个词」的断言"));
    }

    [Fact]
    public void Numbers_in_copy_come_from_the_shared_defaults()
    {
        // 文案里的数字必须引用 SystemDefaults。写死之后，常量一改 tooltip 就开始对现场说谎，
        // 而长度/禁用词这类检查一个字都抓不到。这条断言就是防回退的那道闸。
        Assert.Contains(SystemDefaults.ExportRowLimit.ToString("N0"), HelpTexts.ExportLimit.How);
        Assert.Contains(SystemDefaults.TrendSampleLimit.ToString("N0"), HelpTexts.TrendSampleLimit.How);
        Assert.Contains(SystemDefaults.StaleHeartbeatSeconds.ToString(), HelpTexts.StaleData.Impact);
        Assert.Contains(SystemDefaults.CadenceWarnSeconds.ToString(), HelpTexts.Cadence.Impact);
        Assert.Contains((SystemDefaults.CadenceIdleSeconds / 60).ToString(), HelpTexts.Cadence.Impact);
        Assert.Contains(SystemDefaults.LockoutMaxFailedAttempts.ToString(), HelpTexts.Lockout.How);
        Assert.Contains(SystemDefaults.LockoutMinutes.ToString(), HelpTexts.Lockout.How);
        Assert.Contains(SystemDefaults.MesBacklogWarnHours.ToString(), HelpTexts.MesOutbox.Impact);

        // 取值范围来自 SettingsLimits：文案里复述边界值，就得跟拦输入的规则同源。
        Assert.Contains(SettingsLimits.MinScanIntervalMs.ToString(), HelpTexts.ScanInterval.Impact);
        Assert.Contains(SettingsLimits.MaxScanIntervalMs.ToString(), HelpTexts.ScanInterval.Impact);
        Assert.Contains(SettingsLimits.MaxWriteRetryCount.ToString(), HelpTexts.WriteRetry.Impact);
        Assert.Contains(SettingsLimits.MaxWriteRetryDelayMs.ToString(), HelpTexts.WriteRetry.Impact);
    }

    [Fact]
    public void Cross_page_concepts_are_defined_once()
    {
        // 直通率（看板/报表）、限值三档（看板/查询/报表）、判定三态、型号口径、角色权限
        // 都只有一条 —— 这正是这套文案库存在的理由。
        Assert.Equal("直通率", HelpTexts.YieldRate.Title);
        Assert.Equal("限值三档", HelpTexts.LimitThreeTiers.Title);
        Assert.Equal("判定三态", HelpTexts.JudgementThreeState.Title);
        Assert.Equal("型号口径", HelpTexts.RecipeScope.Title);

        var count = Enumerate().Count();
        Assert.True(count is >= 40 and <= 65, $"文案条目数 {count} 超出预期区间（40~65）");
    }

    [Theory]
    [InlineData("", 6)]
    [InlineData("query", 6)]
    [InlineData("reports", 8)]
    [InlineData("curve-baseline", 6)]
    [InlineData("logs", 4)]
    [InlineData("config/plc", 4)]
    [InlineData("config/stations", 4)]
    [InlineData("config/recipes", 7)]
    [InlineData("config/settings", 6)]
    [InlineData("simulate", 5)]
    [InlineData("users", 5)]
    [InlineData("record", 7)]
    public void Every_page_mapping_lists_topics_that_exist(string route, int expected)
    {
        // 页头「本页说明」按这份映射取条目；写错路由会让整页入口凭空消失，且没有任何别的检查能发现。
        var topics = HelpTexts.ForPage(route);
        Assert.NotEmpty(topics);
        Assert.Equal(expected, topics.Count);
    }

    [Fact]
    public void Unknown_route_has_no_topics()
        => Assert.Empty(HelpTexts.ForPage("不存在的路由"));
}
