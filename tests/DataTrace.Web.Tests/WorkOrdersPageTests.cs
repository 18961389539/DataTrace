using DataTrace.Application.Configuration;
using DataTrace.Application.Realtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Dialogs;
using DataTrace.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DataTrace.Web.Tests;

public class WorkOrdersPageTests : WebTestBase
{
    private readonly Mock<IConfigurationChanges> _changes = new();

    private IRenderedComponent<WorkOrders> Render(params WorkOrder[] orders)
        => RenderWithActive(null, orders);

    private IRenderedComponent<WorkOrders> RenderWithActive(Recipe? active, params WorkOrder[] orders)
    {
        var recipe = new Recipe { Id = 7, Code = "A100", Name = "演示型号", Model = "A100" };
        var other = new Recipe { Id = 8, Code = "B200", Name = "另一型号", Model = "B200" };
        Config.Snapshot = new AppConfigurationSnapshot
        {
            Recipes = [recipe, other],
            ActiveRecipe = active
        };
        Config.WorkOrders.Clear();
        Config.WorkOrders.AddRange(orders);
        Context.Services.AddSingleton<IConfigurationChanges>(_changes.Object);
        RenderPopoverHost();
        return Context.Render<WorkOrders>();
    }

    private static WorkOrder Order(int id, string code, WorkOrderStatus status = WorkOrderStatus.Pending, bool current = false)
        => new()
        {
            Id = id,
            Code = code,
            RecipeId = 7,
            Recipe = new Recipe { Id = 7, Code = "A100", Name = "演示型号" },
            Model = "A100",
            PlannedQuantity = 500,
            Status = status,
            IsCurrent = current,
            StartDate = new DateTime(2026, 10, 28),
            EndDate = new DateTime(2026, 10, 30),
            Remark = "首批"
        };

    [Fact]
    public void Lists_orders_with_recipe_and_status()
    {
        var cut = Render(Order(1, "WO-001", WorkOrderStatus.InProgress));

        Assert.Contains("WO-001", cut.Markup);
        Assert.Contains("A100", cut.Markup);
        Assert.Contains("演示型号", cut.Markup);
        Assert.Contains("生产中", cut.Markup);
        Assert.Contains("2026-10-28 ~ 2026-10-30", cut.Markup);
        Assert.Contains("设为当前", cut.Markup);
    }

    [Fact]
    public void Current_order_shows_cancel_action()
    {
        var cut = Render(Order(1, "WO-001", WorkOrderStatus.InProgress, current: true));

        Assert.Contains("取消选择", cut.Markup);
        Assert.Contains("当前工单", cut.Markup);
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "设为当前");
    }

    [Fact]
    public void Set_current_invokes_change_with_the_order_id()
    {
        var active = new Recipe { Id = 7, Code = "A100", Name = "演示型号", Model = "A100" };
        var cut = RenderWithActive(active, Order(1, "WO-001"));

        ClickButton(cut, "设为当前");

        var invocation = Assert.Single(
            _changes.Invocations,
            call => call.Method.Name == nameof(IConfigurationChanges.SetCurrentWorkOrderAsync));
        Assert.Equal(1, (int?)invocation.Arguments[0]);
        Assert.Empty(Dialogs.Prompts);
        Assert.Contains("已设为当前", Toast.LastMessage);
    }

    [Fact]
    public void Mismatching_model_prompts_and_switches_when_confirmed()
    {
        var active = new Recipe { Id = 8, Code = "B200", Name = "另一型号", Model = "B200" };
        Dialogs.PromptResult = true;
        var cut = RenderWithActive(active, Order(1, "WO-001"));

        ClickButton(cut, "设为当前");

        var prompt = Assert.Single(Dialogs.Prompts);
        Assert.Equal("当前型号不一致", prompt.Title);
        Assert.Contains("是否同时把当前型号切换", prompt.Message);
        var recipeInvocation = Assert.Single(
            _changes.Invocations,
            call => call.Method.Name == nameof(IConfigurationChanges.SetActiveRecipeAsync));
        Assert.Equal(7, (int?)recipeInvocation.Arguments[0]);
        Assert.Contains("当前型号同步为 A100", Toast.LastMessage);
    }

    [Fact]
    public void Mismatching_model_can_keep_the_active_model()
    {
        var active = new Recipe { Id = 8, Code = "B200", Name = "另一型号", Model = "B200" };
        Dialogs.PromptResult = false;
        var cut = RenderWithActive(active, Order(1, "WO-001"));

        ClickButton(cut, "设为当前");

        Assert.Single(_changes.Invocations,
            call => call.Method.Name == nameof(IConfigurationChanges.SetCurrentWorkOrderAsync));
        Assert.DoesNotContain(_changes.Invocations,
            call => call.Method.Name == nameof(IConfigurationChanges.SetActiveRecipeAsync));
        Assert.Contains("已设为当前", Toast.LastMessage);
    }

    [Fact]
    public void Dismissing_the_prompt_cancels_setting_current()
    {
        var active = new Recipe { Id = 8, Code = "B200", Name = "另一型号", Model = "B200" };
        Dialogs.PromptResult = null;
        var cut = RenderWithActive(active, Order(1, "WO-001"));

        ClickButton(cut, "设为当前");

        Assert.DoesNotContain(_changes.Invocations,
            call => call.Method.Name == nameof(IConfigurationChanges.SetCurrentWorkOrderAsync));
    }

    [Fact]
    public void Mismatch_banner_offers_model_sync()
    {
        var active = new Recipe { Id = 8, Code = "B200", Name = "另一型号", Model = "B200" };
        var cut = RenderWithActive(active,
            Order(1, "WO-001", WorkOrderStatus.InProgress, current: true));

        Assert.Contains("与当前型号", cut.Markup);
        ClickButton(cut, "同步当前型号");

        var recipeInvocation = Assert.Single(
            _changes.Invocations,
            call => call.Method.Name == nameof(IConfigurationChanges.SetActiveRecipeAsync));
        Assert.Equal(7, (int?)recipeInvocation.Arguments[0]);
    }

    [Fact]
    public void Clearing_current_invokes_change_with_null()
    {
        var cut = Render(Order(1, "WO-001", WorkOrderStatus.InProgress, current: true));

        ClickButton(cut, "取消选择");

        var invocation = Assert.Single(
            _changes.Invocations,
            call => call.Method.Name == nameof(IConfigurationChanges.SetCurrentWorkOrderAsync));
        Assert.Null((int?)invocation.Arguments[0]);
        Assert.Contains("已取消当前工单", Toast.LastMessage);
    }

    [Fact]
    public void Create_success_writes_the_edited_values()
    {
        var cut = Render();
        Dialogs.DialogResult = DialogResult.Ok(new WorkOrderEditDialog.WorkOrderEditResult
        {
            Code = "WO-100",
            RecipeId = 7,
            Model = "A100",
            PlannedQuantity = 1200,
            Status = WorkOrderStatus.Completed,
            StartDate = new DateTime(2026, 11, 1),
            EndDate = new DateTime(2026, 11, 5),
            Remark = "末批"
        });

        ClickButton(cut, "新增工单");

        var saved = Assert.Single(_changes.Invocations
            .Where(call => call.Method.Name == nameof(IConfigurationChanges.SaveWorkOrderAsync))
            .Select(call => (WorkOrder)call.Arguments[0]));
        Assert.Equal("WO-100", saved.Code);
        Assert.Equal(7, saved.RecipeId);
        Assert.Equal("A100", saved.Model);
        Assert.Equal(1200, saved.PlannedQuantity);
        Assert.Equal(WorkOrderStatus.Completed, saved.Status);
        Assert.Equal(new DateTime(2026, 11, 1), saved.StartDate);
        Assert.Equal(new DateTime(2026, 11, 5), saved.EndDate);
        Assert.Equal("末批", saved.Remark);
        Assert.Equal("新增工单", Assert.Single(Dialogs.Shown).Title);
        Assert.Contains("已新增", Toast.LastMessage);
    }

    [Fact]
    public void Save_failure_keeps_the_persisted_list()
    {
        var cut = Render(Order(1, "WO-001"));
        _changes
            .Setup(changes => changes.SaveWorkOrderAsync(
                It.IsAny<WorkOrder>(), It.IsAny<ConfigAudit>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConfigurationChangeFailedException("改动已回滚或未写入：数据库只读"));
        Dialogs.DialogResult = DialogResult.Ok(new WorkOrderEditDialog.WorkOrderEditResult
        {
            Code = "WO-001",
            Status = WorkOrderStatus.Cancelled
        });

        ClickButton(cut, "编辑");
        cut.WaitForAssertion(() => Assert.Contains("失败", Toast.LastMessage));

        // 页面列表仍是库里的那一份（状态没有被界面上的编辑结果改掉）。
        Assert.Contains("待生产", cut.Markup);
        // status filter chips always render labels; assert table cells only
        var statusCells = cut.FindAll("td").Select(td => td.TextContent).ToList();
        Assert.Contains(statusCells, text => text.Contains("待生产", StringComparison.Ordinal));
        Assert.DoesNotContain(statusCells, text => text.Contains("已取消", StringComparison.Ordinal));
    }

    [Fact]
    public void Delete_cancelled_does_nothing()
    {
        var cut = Render(Order(1, "WO-001"));
        Dialogs.MessageBoxResult = false;

        ClickButton(cut, "删除");

        cut.WaitForAssertion(() => Assert.Single(Dialogs.MessageBoxes));
        Assert.Empty(Config.DeletedWorkOrderIds);
    }

    [Fact]
    public void Delete_confirmed_removes_the_order()
    {
        var cut = Render(Order(1, "WO-001"));
        Dialogs.MessageBoxResult = true;

        ClickButton(cut, "删除");

        cut.WaitForAssertion(() => Assert.Single(
            _changes.Invocations,
            call => call.Method.Name == nameof(IConfigurationChanges.DeleteWorkOrderAsync)));
        Assert.Contains("已删除", Toast.LastMessage);
    }
}
