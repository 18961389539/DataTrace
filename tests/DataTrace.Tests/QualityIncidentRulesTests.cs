using DataTrace.Application.Quality;
using DataTrace.Domain.Constants;

namespace DataTrace.Tests;

public class QualityIncidentRulesTests
{
    private static readonly QualityIncidentActor Operator = new("operator", [AppRoles.Operator]);
    private static readonly QualityIncidentActor Engineer = new("engineer", [AppRoles.Engineer]);

    [Fact]
    public void Operator_can_work_but_cannot_review_or_assign()
    {
        QualityIncidentRules.RequireCanWork(Operator);
        Assert.Throws<QualityIncidentOperationException>(() => QualityIncidentRules.RequireCanManage(Operator));
        QualityIncidentRules.RequireCanManage(Engineer);
    }

    [Fact]
    public void Viewer_cannot_create_or_work_an_incident()
    {
        var viewer = new QualityIncidentActor("viewer", [AppRoles.Viewer]);

        Assert.Throws<QualityIncidentOperationException>(() => QualityIncidentRules.RequireCanWork(viewer));
        Assert.Throws<QualityIncidentOperationException>(() => QualityIncidentRules.RequireCanManage(viewer));
    }

    [Fact]
    public void Required_text_rejects_empty_and_overlong_values()
    {
        Assert.Equal("有效标题", QualityIncidentRules.RequiredText(" 有效标题 ", "标题", 10));
        Assert.Throws<QualityIncidentOperationException>(() => QualityIncidentRules.RequiredText(" ", "标题"));
        Assert.Throws<QualityIncidentOperationException>(() => QualityIncidentRules.RequiredText("过长标题", "标题", 2));
    }
}