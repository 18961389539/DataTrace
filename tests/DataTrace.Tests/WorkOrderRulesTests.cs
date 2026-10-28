using DataTrace.Domain.Enums;
using DataTrace.Domain.Validation;

namespace DataTrace.Tests;

public class WorkOrderRulesTests
{
    [Theory]
    [InlineData("WO-2026-001")]
    [InlineData("工单001")]
    public void Accepts_non_blank_codes(string code)
        => Assert.Null(WorkOrderRules.CodeError(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_blank_codes(string? code)
        => Assert.NotNull(WorkOrderRules.CodeError(code));

    [Fact]
    public void Rejects_codes_longer_than_limit()
        => Assert.NotNull(WorkOrderRules.CodeError(new string('A', WorkOrderRules.MaxCodeLength + 1)));

    [Fact]
    public void Planned_quantity_must_positive_when_present()
    {
        Assert.Null(WorkOrderRules.PlannedQuantityError(null));
        Assert.Null(WorkOrderRules.PlannedQuantityError(1));
        Assert.NotNull(WorkOrderRules.PlannedQuantityError(0));
        Assert.NotNull(WorkOrderRules.PlannedQuantityError(-3));
    }

    [Fact]
    public void End_date_cannot_precede_start()
    {
        var start = new DateTime(2026, 10, 28);
        Assert.Null(WorkOrderRules.DateRangeError(start, start));
        Assert.Null(WorkOrderRules.DateRangeError(start, start.AddDays(1)));
        Assert.Null(WorkOrderRules.DateRangeError(start, null));
        Assert.NotNull(WorkOrderRules.DateRangeError(start, start.AddDays(-1)));
    }

    [Fact]
    public void Unknown_status_is_rejected()
    {
        Assert.Null(WorkOrderRules.StatusError(WorkOrderStatus.InProgress));
        Assert.NotNull(WorkOrderRules.StatusError((WorkOrderStatus)99));
    }
}

public class ProductModelRulesTests
{
    [Fact]
    public void Empty_model_is_allowed()
    {
        Assert.Null(ProductModelRules.Error(null));
        Assert.Null(ProductModelRules.Error("   "));
        Assert.Null(ProductModelRules.Error("A100"));
    }

    [Fact]
    public void Model_longer_than_limit_is_rejected()
        => Assert.NotNull(ProductModelRules.Error(new string('A', ProductModelRules.MaxLength + 1)));
}
