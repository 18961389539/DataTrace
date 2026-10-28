using DataTrace.Application.Configuration;
using DataTrace.Application.Realtime;
using DataTrace.Domain.Entities;
using DataTrace.Web.Components.Dialogs;
using DataTrace.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DataTrace.Web.Tests;

public class RecipesPageTests : WebTestBase
{
    private readonly Mock<IConfigurationChanges> _changes = new();
    private readonly Mock<IRuntimeStatusHub> _status = new();

    private IRenderedComponent<Recipes> Render(Recipe recipe, bool active = false)
    {
        Config.Snapshot = new AppConfigurationSnapshot
        {
            Recipes = [recipe],
            ActiveRecipe = active ? recipe : null
        };
        Context.Services.AddSingleton<IConfigurationChanges>(_changes.Object);
        Context.Services.AddSingleton<IRuntimeStatusHub>(_status.Object);
        RenderPopoverHost();
        return Context.Render<Recipes>();
    }

    [Fact]
    public void Warns_when_current_work_order_model_differs_from_active()
    {
        Config.WorkOrders.Add(new WorkOrder
        {
            Id = 1, Code = "WO-1", IsCurrent = true, RecipeId = 9, Model = "M9"
        });

        var cut = Render(new Recipe { Id = 7, Code = "A100", Name = "演示型号", Model = "M7" }, active: true);

        Assert.Contains("与当前型号不一致", cut.Markup);
        Assert.Contains("WO-1", cut.Markup);
    }

    [Fact]
    public void List_shows_model_and_name_without_system_code()
    {
        var cut = Render(new Recipe { Id = 7, Code = "SYS-XYZ", Model = "A100", Name = "演示型号" });

        Assert.Contains("A100", cut.Markup);
        Assert.Contains("演示型号", cut.Markup);
        Assert.DoesNotContain("SYS-XYZ", cut.Markup);
    }

    [Fact]
    public void Edit_passes_model_to_save()
    {
        Recipe? attempted = null;
        _changes
            .Setup(changes => changes.SaveRecipeAsync(
                It.IsAny<Recipe>(), It.IsAny<ConfigAudit>(), It.IsAny<CancellationToken>()))
            .Callback<Recipe, ConfigAudit, CancellationToken>((recipe, _, _) => attempted = recipe);
        Dialogs.DialogResult = DialogResult.Ok(new RecipeEditDialog.RecipeEditResult
        {
            Code = "A100",
            Model = "M-9",
            Name = "演示型号"
        });
        var cut = Render(new Recipe { Id = 7, Code = "A100", Model = "M-1", Name = "演示型号" });

        ClickButton(cut, "编辑");

        Assert.NotNull(attempted);
        Assert.Equal("M-9", attempted.Model);
    }

    [Fact]
    public void Edit_failure_does_not_mutate_the_recipe_in_the_list()
    {
        Recipe? attempted = null;
        _changes
            .Setup(changes => changes.SaveRecipeAsync(
                It.IsAny<Recipe>(), It.IsAny<ConfigAudit>(), It.IsAny<CancellationToken>()))
            .Callback<Recipe, ConfigAudit, CancellationToken>((recipe, _, _) => attempted = recipe)
            .ThrowsAsync(new Exception("数据库只读"));
        Dialogs.DialogResult = DialogResult.Ok(new RecipeEditDialog.RecipeEditResult
        {
            Code = "A100",
            Name = "新名称",
            Remark = "新备注"
        });
        var original = new Recipe { Id = 7, Code = "A100", Name = "旧名称", Remark = "旧备注" };
        var cut = Render(original);

        ClickButton(cut, "编辑");

        Assert.NotNull(attempted);
        Assert.Equal("新名称", attempted.Name);
        Assert.Equal("旧名称", original.Name);
        Assert.Contains("旧名称", cut.Markup);
        Assert.DoesNotContain("新名称", cut.Markup);
    }

    [Fact]
    public void Disable_failure_keeps_the_persisted_enabled_state()
    {
        Recipe? attempted = null;
        _changes
            .Setup(changes => changes.SaveRecipeAsync(
                It.IsAny<Recipe>(), It.IsAny<ConfigAudit>(), It.IsAny<CancellationToken>()))
            .Callback<Recipe, ConfigAudit, CancellationToken>((recipe, _, _) => attempted = recipe)
            .ThrowsAsync(new Exception("数据库只读"));
        var original = new Recipe { Id = 7, Code = "A100", Name = "型号 A100", Enabled = true };
        var cut = Render(original);

        cut.Find("input[type=checkbox]").Change(false);

        Assert.NotNull(attempted);
        Assert.False(attempted.Enabled);
        Assert.True(original.Enabled);
    }
}