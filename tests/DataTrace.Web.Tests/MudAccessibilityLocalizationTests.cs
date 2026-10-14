using DataTrace.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace DataTrace.Web.Tests;

public class MudAccessibilityLocalizationTests : WebTestBase
{
    [Fact]
    public void Pagination_accessible_names_are_chinese()
    {
        UseChineseLocalization();

        var cut = Context.Render<MudPagination>(parameters => parameters
            .Add(pagination => pagination.Count, 4)
            .Add(pagination => pagination.Selected, 2)
            .Add(pagination => pagination.ShowFirstButton, true)
            .Add(pagination => pagination.ShowLastButton, true));

        var names = cut.FindAll("button")
            .Select(button => button.GetAttribute("aria-label"))
            .Where(label => label is not null)
            .ToArray();

        Assert.Contains("上一页", names);
        Assert.Contains("下一页", names);
        Assert.Contains("第一页", names);
        Assert.Contains("最后一页", names);
        Assert.DoesNotContain(names, label => label!.Contains("page", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Numeric_step_controls_have_chinese_accessible_names()
    {
        UseChineseLocalization();

        var cut = Context.Render<MudNumericField<int>>(parameters => parameters
            .Add(field => field.Value, 10)
            .Add(field => field.Step, 1)
            .Add(field => field.HideSpinButtons, false));

        Assert.Contains("增加数值", cut.Markup);
        Assert.Contains("减少数值", cut.Markup);
        Assert.DoesNotContain("Increment", cut.Markup);
        Assert.DoesNotContain("Decrement", cut.Markup);
    }

    private void UseChineseLocalization()
        => Context.Services.AddTransient<MudLocalizer, DataTraceMudLocalizer>();
}
