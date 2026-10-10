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

    private IRenderedComponent<Recipes> Render(Recipe recipe)
    {
        Config.Snapshot = new AppConfigurationSnapshot { Recipes = [recipe] };
        Context.Services.AddSingleton<IConfigurationChanges>(_changes.Object);
        Context.Services.AddSingleton<IRuntimeStatusHub>(_status.Object);
        RenderPopoverHost();
        return Context.Render<Recipes>();
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