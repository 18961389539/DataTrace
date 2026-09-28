using DataTrace.Web.Components.Shared;
using DataTrace.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace DataTrace.Web.Tests;

public sealed class PageHelpTests : WebTestBase
{
    private IRenderedComponent<MudDialogProvider> RenderDialogProvider()
    {
        Context.Services.AddSingleton<IDialogService, DialogService>();
        RenderPopoverHost();
        return Context.RenderComponent<MudDialogProvider>();
    }

    [Fact]
    public void Opens_topic_dialog_and_switches_the_single_detail()
    {
        var provider = RenderDialogProvider();
        var pageHelp = Context.RenderComponent<PageHelp>(p => p.Add(x => x.Route, "/"));

        pageHelp.Find("button[aria-label='本页说明']").Click();
        provider.Render();

        Assert.Contains("13 条说明", provider.Markup);
        Assert.Equal("页面用途", provider.Find(".dt-page-help-detail .dt-help-title").TextContent);
        Assert.Contains("采集器", provider.Find(".dt-page-help-detail").TextContent);

        provider.FindAll(".dt-page-help-nav button")
            .Single(button => button.TextContent.Contains("限值三档"))
            .Click();
        provider.Render();

        Assert.Equal("限值三档", provider.Find(".dt-page-help-detail .dt-help-title").TextContent);
        Assert.Contains("说明项 8 / 13", provider.Markup);
    }

    [Fact]
    public void Filters_topics_when_the_page_has_many()
    {
        var provider = RenderDialogProvider();
        var pageHelp = Context.RenderComponent<PageHelp>(p => p.Add(x => x.Route, "reports"));

        pageHelp.Find("button[aria-label='本页说明']").Click();
        provider.Render();

        var search = provider.Find("input[aria-label='搜索说明主题']");
        search.Input("Nelson");
        provider.Render();

        Assert.Single(provider.FindAll(".dt-page-help-nav button"));
        Assert.Equal("趋势与过程能力", provider.Find(".dt-page-help-detail .dt-help-title").TextContent);
        Assert.Contains("规则子集", provider.Find(".dt-page-help-detail").TextContent);
    }

    [Fact]
    public void Hides_entry_when_the_route_has_no_help_topics()
    {
        var pageHelp = Context.RenderComponent<PageHelp>(p => p.Add(x => x.Route, "not-a-page"));

        Assert.Empty(pageHelp.FindAll("button[aria-label='本页说明']"));
    }
}
