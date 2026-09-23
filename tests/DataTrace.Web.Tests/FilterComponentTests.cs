using DataTrace.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace DataTrace.Web.Tests;

/// <summary>
/// 时间区间筛选组件。它替掉了三个页面各自手写的一份预设按钮 + 两个日期框，
/// 而这三页原本连"昨天"的含义都不一样，所以语义必须有用例钉住。
/// </summary>
public class DateRangeFilterTests : WebTestBase
{
    private DateTime? _from;
    private DateTime? _to;
    private int _changed;

    private IRenderedComponent<DateRangeFilter> Render(DateTime? from, DateTime? to)
    {
        _changed = 0;

        // MudDatePicker 要往 MudPopoverProvider 里挂浮层；真实应用由 MainLayout 提供，
        // 这里单独渲染裸组件就得自己补一个，否则初始化直接抛。
        Context.RenderComponent<MudPopoverProvider>();

        return Context.RenderComponent<DateRangeFilter>(parameters => parameters
            .Add(p => p.From, from)
            .Add(p => p.FromChanged, v => _from = v)
            .Add(p => p.To, to)
            .Add(p => p.ToChanged, v => _to = v)
            .Add(p => p.OnRangeChanged, () => _changed++));
    }

    [Fact]
    public void AllThreePagesGetTheSameFivePresets()
    {
        var cut = Render(DateTime.Today, DateTime.Today);

        var labels = cut.FindAll(".dt-range-buttons button").Select(b => b.TextContent.Trim()).ToList();

        Assert.Equal(["今天", "昨天", "近 7 天", "近 30 天", "本月"], labels);
    }

    [Fact]
    public void YesterdayMeansYesterdayAlone()
    {
        // 旧实现里 Query 的"昨天"是 today-1 .. today，跨了两个自然日。
        var cut = Render(DateTime.Today, DateTime.Today);

        cut.FindAll(".dt-range-buttons button").Single(b => b.TextContent.Contains("昨天")).Click();

        Assert.Equal(DateTime.Today.AddDays(-1), _from);
        Assert.Equal(DateTime.Today.AddDays(-1), _to);
    }

    [Fact]
    public void SevenDaysCountsTodayAsTheFirstDay()
    {
        var cut = Render(DateTime.Today, DateTime.Today);

        cut.FindAll(".dt-range-buttons button").Single(b => b.TextContent.Contains("近 7 天")).Click();

        Assert.Equal(DateTime.Today.AddDays(-6), _from);
        Assert.Equal(DateTime.Today, _to);
    }

    [Fact]
    public void MonthStartsOnTheFirstAndEndsToday()
    {
        var cut = Render(DateTime.Today, DateTime.Today);

        cut.FindAll(".dt-range-buttons button").Single(b => b.TextContent.Contains("本月")).Click();

        Assert.Equal(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), _from);
        Assert.Equal(DateTime.Today, _to);
    }

    [Fact]
    public void ChoosingAPresetAsksThePageToQueryExactlyOnce()
    {
        var cut = Render(DateTime.Today, DateTime.Today);

        cut.FindAll(".dt-range-buttons button").Single(b => b.TextContent.Contains("近 30 天")).Click();

        Assert.Equal(1, _changed);
    }

    [Fact]
    public void TheActiveRangeIsVisuallyMarked()
    {
        var cut = Render(DateTime.Today.AddDays(-6), DateTime.Today);

        var selected = cut.FindAll(".dt-range-buttons button")
            .Where(b => b.ClassList.Contains("mud-button-filled"))
            .Select(b => b.TextContent.Trim())
            .ToList();

        Assert.Equal(["近 7 天"], selected);
    }

    [Fact]
    public void AFreeTypedRangeMarksNoPreset()
    {
        // 手工填的区间不该假装等于某个预设，否则选中态会骗人。
        var cut = Render(DateTime.Today.AddDays(-3), DateTime.Today.AddDays(-1));

        Assert.DoesNotContain(cut.FindAll(".dt-range-buttons button"), b => b.ClassList.Contains("mud-button-filled"));
    }

    [Fact]
    public void ReversedRangeIsReportedOnTheField()
    {
        var cut = Render(DateTime.Today, DateTime.Today.AddDays(-5));

        Assert.Contains("开始日期不能晚于结束日期", cut.Find(".dt-range").TextContent);
    }
}

/// <summary>
/// 页头组件。标题必须是 h5 且带 dt-page-title：前者是 Routes.razor 里
/// PageFocusOnNavigate 的查找选择器，后者是 app.css 抑制程序性焦点环的依据。
/// 两边都靠约定而不是编译期检查，所以用测试钉住。
/// </summary>
public class PageHeaderTests : WebTestBase
{
    [Fact]
    public void TitleIsTheFocusTargetContract()
    {
        var cut = Context.RenderComponent<PageHeader>(parameters => parameters
            .Add(p => p.Title, "数据查询"));

        var title = cut.Find("h5");

        Assert.Contains("dt-page-title", title.ClassList);
        Assert.Equal("数据查询", title.TextContent);
    }

    [Fact]
    public void DescriptionAndActionsHaveTheirOwnSlots()
    {
        var cut = Context.RenderComponent<PageHeader>(parameters => parameters
            .Add(p => p.Title, "报表")
            .Add(p => p.Description, "按天统计")
            .Add(p => p.Actions, (RenderFragment)(builder => builder.AddMarkupContent(0, "<span>共 7 条</span>")))
            .Add(p => p.ChildContent, (RenderFragment)(builder => builder.AddMarkupContent(0, "<span>当前型号</span>"))));

        Assert.Contains("按天统计", cut.Find(".dt-muted").TextContent);
        Assert.Contains("共 7 条", cut.Markup);
        Assert.Contains("当前型号", cut.Markup);
    }
}
