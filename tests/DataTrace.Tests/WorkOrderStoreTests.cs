using DataTrace.Application.Configuration;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Tests;

/// <summary>工单档案的真库行为：唯一性、字段归一、删除与型号校验。</summary>
public class WorkOrderStoreTests
{
    private static IConfigRepository Repository(InfrastructureContext context)
        => context.Scope.ServiceProvider.GetRequiredService<IConfigRepository>();

    [Fact]
    public async Task Save_and_read_round_trip_normalizes_fields()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        var repository = Repository(context);

        await repository.SaveWorkOrderAsync(new WorkOrder
        {
            Code = "  WO-001  ",
            Model = "  A100  ",
            PlannedQuantity = 500,
            Status = WorkOrderStatus.InProgress,
            StartDate = new DateTime(2026, 10, 28, 13, 45, 0),
            EndDate = new DateTime(2026, 10, 30, 9, 0, 0),
            Remark = "  首批  "
        });

        var order = Assert.Single(await repository.GetWorkOrdersAsync());
        Assert.Equal("WO-001", order.Code);
        Assert.Equal("A100", order.Model);
        Assert.Equal(500, order.PlannedQuantity);
        Assert.Equal(WorkOrderStatus.InProgress, order.Status);
        Assert.Equal(new DateTime(2026, 10, 28), order.StartDate);
        Assert.Equal(new DateTime(2026, 10, 30), order.EndDate);
        Assert.Equal("首批", order.Remark);
        Assert.NotNull(await repository.GetWorkOrderAsync(order.Id));
    }

    [Fact]
    public async Task Duplicate_code_is_rejected_case_insensitively()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        var repository = Repository(context);
        await repository.SaveWorkOrderAsync(new WorkOrder { Code = "WO-002" });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.SaveWorkOrderAsync(new WorkOrder { Code = "wo-002" }));
        Assert.Contains("已存在", error.Message);
    }

    [Fact]
    public async Task Update_of_missing_order_throws()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        var repository = Repository(context);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.SaveWorkOrderAsync(new WorkOrder { Id = 9999, Code = "WO-404" }));
        Assert.Contains("不存在", error.Message);
    }

    [Fact]
    public async Task Unknown_recipe_is_rejected()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        var repository = Repository(context);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.SaveWorkOrderAsync(new WorkOrder { Code = "WO-003", RecipeId = 9999 }));
        Assert.Contains("型号", error.Message);
    }

    [Fact]
    public async Task Setting_current_keeps_only_one_and_starts_production()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        var repository = Repository(context);
        await repository.SaveWorkOrderAsync(new WorkOrder { Code = "WO-010" });
        await repository.SaveWorkOrderAsync(new WorkOrder { Code = "WO-011" });
        var first = (await repository.GetWorkOrdersAsync()).Single(order => order.Code == "WO-010");
        var second = (await repository.GetWorkOrdersAsync()).Single(order => order.Code == "WO-011");

        await repository.SetCurrentWorkOrderAsync(first.Id);
        var afterFirst = await repository.GetWorkOrdersAsync();
        Assert.True(afterFirst.Single(order => order.Id == first.Id).IsCurrent);
        Assert.NotNull(afterFirst.Single(order => order.Id == first.Id).CurrentSince);
        Assert.Equal(WorkOrderStatus.InProgress, afterFirst.Single(order => order.Id == first.Id).Status);
        Assert.False(afterFirst.Single(order => order.Id == second.Id).IsCurrent);

        await repository.SetCurrentWorkOrderAsync(second.Id);
        var afterSecond = await repository.GetWorkOrdersAsync();
        Assert.False(afterSecond.Single(order => order.Id == first.Id).IsCurrent);
        Assert.True(afterSecond.Single(order => order.Id == second.Id).IsCurrent);

        await repository.SetCurrentWorkOrderAsync(null);
        var cleared = await repository.GetWorkOrdersAsync();
        Assert.DoesNotContain(cleared, order => order.IsCurrent);
        Assert.All(cleared, order => Assert.Null(order.CurrentSince));
    }

    [Fact]
    public async Task Leaving_production_status_clears_the_current_flag()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        var repository = Repository(context);
        await repository.SaveWorkOrderAsync(new WorkOrder { Code = "WO-012" });
        var order = (await repository.GetWorkOrdersAsync()).Single();
        await repository.SetCurrentWorkOrderAsync(order.Id);

        order.Status = WorkOrderStatus.Completed;
        await repository.SaveWorkOrderAsync(order);

        var stored = (await repository.GetWorkOrdersAsync()).Single();
        Assert.Equal(WorkOrderStatus.Completed, stored.Status);
        Assert.False(stored.IsCurrent);
        Assert.Null(stored.CurrentSince);
    }

    [Fact]
    public async Task Delete_removes_the_order_only()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        var repository = Repository(context);
        await repository.SaveWorkOrderAsync(new WorkOrder { Code = "WO-004" });
        await repository.SaveWorkOrderAsync(new WorkOrder { Code = "WO-005" });
        var first = (await repository.GetWorkOrdersAsync())
            .Single(order => order.Code == "WO-004");

        await repository.DeleteWorkOrderAsync(first.Id);

        var remaining = Assert.Single(await repository.GetWorkOrdersAsync());
        Assert.Equal("WO-005", remaining.Code);
    }
}
