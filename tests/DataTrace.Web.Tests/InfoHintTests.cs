using DataTrace.Web.Components.Shared;
using DataTrace.Web.Services;

namespace DataTrace.Web.Tests;

/// <summary>
/// 概念详解的两个共享组件。
/// </summary>
/// <remarks>
/// 悬停真的能出内容这件事在 E2E 里验（那里有真浏览器与真 popover）；
/// 这里盯的是"组件有没有把话说全、名字对不对、空值会不会出半截图标"。
/// </remarks>
public class InfoHintTests : WebTestBase
{
    [Fact]
    public void Renders_a_focusable_button_named_after_the_concept()
    {
        RenderPopoverHost();
        var cut = Context.RenderComponent<InfoHint>(p => p.Add(x => x.Topic, HelpTexts.YieldRate));

        var button = cut.Find("button");
        // 前缀固定为「说明：」：E2E 用 GetByRole(Button, Name=…) 点真按钮，
        // Playwright 的 Name 是子串匹配，名字里不能混进页面按钮的词。
        Assert.Equal("说明：直通率", button.GetAttribute("aria-label"));
    }

    [Fact]
    public void Name_covers_every_topic_without_colliding_with_button_words()
    {
        RenderPopoverHost();

        foreach (var topic in new[]
                 {
                     HelpTexts.TodayScope, HelpTexts.JudgementThreeState, HelpTexts.ExportLimit,
                     HelpTexts.DeleteUser, HelpTexts.SaveToDispatch
                 })
        {
            var cut = Context.RenderComponent<InfoHint>(p => p.Add(x => x.Topic, topic));
            var label = cut.Find("button").GetAttribute("aria-label") ?? "";

            Assert.StartsWith("说明：", label);
            Assert.Contains(topic.Title, label);
            Assert.DoesNotContain("保存", label);
            Assert.DoesNotContain("重置", label);
        }
    }

    [Fact]
    public void Renders_nothing_at_all_when_there_is_no_topic()
    {
        // 调用方常直接传一个可能为空的字段，组件得自己兜住：不能留一枚点不出东西的图标。
        var cut = Context.RenderComponent<InfoHint>();

        Assert.Empty(cut.FindAll("button"));
        Assert.DoesNotContain("mud-tooltip", cut.Markup);
    }

    [Fact]
    public void Screen_readers_get_the_whole_explanation_not_just_the_title()
    {
        RenderPopoverHost();
        var cut = Context.RenderComponent<InfoHint>(p => p.Add(x => x.Topic, HelpTexts.Lockout));

        var describedBy = cut.Find("button").GetAttribute("aria-describedby");
        Assert.False(string.IsNullOrWhiteSpace(describedBy));

        // 指向的元素必须真的存在且含三段：MudTooltip 自身不做 ARIA 关联，
        // 少了这条关联，读屏用户只听到「说明：账号锁定」这五个字。
        var description = cut.Find($"#{describedBy}").TextContent;
        Assert.Contains(HelpTexts.Lockout.Title, description);
        Assert.Contains(HelpTexts.Lockout.How, description);
        Assert.Contains(HelpTexts.Lockout.Impact, description);
    }

    [Fact]
    public void Each_hint_carries_its_own_description_id()
    {
        RenderPopoverHost();
        var first = Context.RenderComponent<InfoHint>(p => p.Add(x => x.Topic, HelpTexts.YieldRate));
        var second = Context.RenderComponent<InfoHint>(p => p.Add(x => x.Topic, HelpTexts.RoleScope));

        // 同一页往往有十来枚 ⓘ，id 撞了就会把人指到别人的说明上。
        Assert.NotEqual(
            first.Find("button").GetAttribute("aria-describedby"),
            second.Find("button").GetAttribute("aria-describedby"));
    }

    [Fact]
    public void Dynamic_extra_line_is_read_aloud_as_well()
    {
        RenderPopoverHost();
        var cut = Context.RenderComponent<InfoHint>(p => p
            .Add(x => x.Topic, HelpTexts.Lockout)
            .Add(x => x.Extra, "连续失败 3 次，锁定到 14:05"));

        var describedBy = cut.Find("button").GetAttribute("aria-describedby")!;
        var description = cut.Find($"#{describedBy}").TextContent;

        Assert.Contains("连续失败 3 次", description);
        Assert.Contains(HelpTexts.Lockout.How, description);
    }

    [Fact]
    public void Help_content_renders_the_three_parts_in_order()
    {
        var cut = Context.RenderComponent<HelpContent>(p => p.Add(x => x.Topic, HelpTexts.YieldRate));

        Assert.Contains(HelpTexts.YieldRate.Title, cut.Markup);
        Assert.Contains(HelpTexts.YieldRate.How, cut.Markup);
        Assert.Contains(HelpTexts.YieldRate.Impact, cut.Markup);

        // 三段有各自的样式钩子：标题加粗、影响一句弱化，不能全糊成一行。
        Assert.Contains("dt-help-title", cut.Markup);
        Assert.Contains("dt-help-impact", cut.Markup);
    }

    [Fact]
    public void Help_content_puts_the_dynamic_line_before_the_three_parts()
    {
        // 锁定状态、某个角色的权限范围这类随数据变化的内容走 Extra，
        // 它必须排在三段之前 —— 先看"现在什么情况"，再看"这个概念是什么"。
        var cut = Context.RenderComponent<HelpContent>(p => p
            .Add(x => x.Topic, HelpTexts.Lockout)
            .Add(x => x.Extra, "连续失败 3 次，锁定到 14:05"));

        var markup = cut.Markup;
        Assert.Contains("连续失败 3 次，锁定到 14:05", markup);
        Assert.True(
            markup.IndexOf("连续失败 3 次", StringComparison.Ordinal)
            < markup.IndexOf(HelpTexts.Lockout.Title, StringComparison.Ordinal),
            "动态补充行应当排在三段之前");
    }

    [Fact]
    public void Help_content_is_silent_without_a_topic()
    {
        var cut = Context.RenderComponent<HelpContent>();

        Assert.Equal("", cut.Markup.Trim());
    }
}
