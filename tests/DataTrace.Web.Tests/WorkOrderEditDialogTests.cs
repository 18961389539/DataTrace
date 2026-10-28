using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Web.Tests;

public class WorkOrderEditDialogTests : WebTestBase
{
    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IDialogReference Reference)> ShowAsync(
        WorkOrder order, params WorkOrder[] siblings)
    {
        Context.Services.AddSingleton<IDialogService, DialogService>();
        RenderPopoverHost();
        var provider = Context.Render<MudDialogProvider>();
        var reference = await Context.Services.GetRequiredService<IDialogService>()
            .ShowAsync<WorkOrderEditDialog>("编辑工单", new DialogParameters
            {
                ["WorkOrder"] = order,
                ["Recipes"] = new List<Recipe> { new() { Id = 7, Code = "A100", Name = "演示型号" } },
                ["Siblings"] = siblings.ToList()
            });
        provider.Render();
        return (provider, reference);
    }

    [Fact]
    public async Task Duplicate_code_blocks_submit_with_a_readable_reason()
    {
        var (provider, _) = await ShowAsync(new WorkOrder(), new WorkOrder { Id = 2, Code = "WO-001" });

        TypeInto(provider, "工单号", "wo-001");

        Assert.Contains("已被占用", provider.Markup);
        var submit = provider.FindAll("button").Single(button => button.TextContent.Trim() == "确定");
        Assert.True(submit.HasAttribute("disabled"));
    }

    [Fact]
    public async Task End_before_start_is_rejected()
    {
        var (provider, _) = await ShowAsync(new WorkOrder
        {
            Code = "WO-009",
            StartDate = new DateTime(2026, 10, 30),
            EndDate = new DateTime(2026, 10, 28)
        });

        var submit = provider.FindAll("button").Single(button => button.TextContent.Trim() == "确定");
        Assert.True(submit.HasAttribute("disabled"));
    }

    [Fact]
    public async Task Shows_selected_recipe_name_as_read_only()
    {
        var (provider, _) = await ShowAsync(new WorkOrder { Code = "WO-020", RecipeId = 7 });

        Assert.Contains("产品名称", provider.Markup);
        Assert.Contains("演示型号", provider.Markup);
        Assert.DoesNotContain("自动带出，可修改", provider.Markup);
        var input = InputForLabel(provider, "产品名称");
        Assert.True(input.HasAttribute("readonly"));
    }

    [Fact]
    public async Task Valid_form_returns_trimmed_values()
    {
        var (provider, reference) = await ShowAsync(new WorkOrder
        {
            Code = "WO-010",
            Model = "A100",
            Status = WorkOrderStatus.Pending
        });

        ClickButton(provider, "确定");

        var result = await reference.Result;
        var edited = Assert.IsType<WorkOrderEditDialog.WorkOrderEditResult>(result?.Data);
        Assert.Equal("WO-010", edited.Code);
        Assert.Equal("A100", edited.Model);
        Assert.Equal(WorkOrderStatus.Pending, edited.Status);
    }
}
