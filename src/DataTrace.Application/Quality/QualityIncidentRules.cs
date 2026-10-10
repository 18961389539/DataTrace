using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Application.Quality;

public static class QualityIncidentRules
{
    public static void RequireCanWork(QualityIncidentActor actor)
    {
        if (!actor.IsInRole(AppRoles.Administrator)
            && !actor.IsInRole(AppRoles.Engineer)
            && !actor.IsInRole(AppRoles.Operator))
        {
            throw new QualityIncidentOperationException("当前账号只有查看权限，不能处理质量异常。");
        }
    }

    public static void RequireCanManage(QualityIncidentActor actor)
    {
        if (!actor.IsInRole(AppRoles.Administrator) && !actor.IsInRole(AppRoles.Engineer))
        {
            throw new QualityIncidentOperationException("只有工程师或管理员可以指派、复核和重新打开异常单。");
        }
    }

    public static string RequiredText(string? value, string label, int maxLength = 2000)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new QualityIncidentOperationException($"{label}不能为空。");
        }

        if (text.Length > maxLength)
        {
            throw new QualityIncidentOperationException($"{label}不能超过 {maxLength} 个字符。");
        }

        return text;
    }

    private static void EnsureStatus(QualityIncident incident, params QualityIncidentStatus[] allowed)
    {
        if (!allowed.Contains(incident.Status))
        {
            throw new InvalidOperationException($"当前状态“{incident.Status}”不能执行此操作。");
        }
    }
}