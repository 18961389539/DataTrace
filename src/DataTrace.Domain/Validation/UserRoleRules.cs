using DataTrace.Domain.Constants;

namespace DataTrace.Domain.Validation;

/// <summary>
/// 用户角色变更的守卫规则。
/// </summary>
public static class UserRoleRules
{
    /// <summary>目标角色集合本身是否可用：一个角色都不留的账号能登录却什么都做不了。</summary>
    public static string? Error(IReadOnlyCollection<string>? roles)
        => roles is null || roles.Count == 0
            ? "至少要为用户保留一个角色，否则该账号登录后什么也做不了"
            : null;

    /// <summary>
    /// 这次改动会不会把系统里最后一个管理员撤下来。
    /// </summary>
    /// <param name="currentRoles">该用户当前的角色。</param>
    /// <param name="targetRoles">保存后的角色。</param>
    /// <param name="administratorCount">库里管理员账号的实况数量，<b>不能只看页面上那一行</b>：
    /// 两个管理员互相撤权时，各自看到的都是"还有另一个管理员"，结果谁都不是管理员了。</param>
    public static bool RemovesLastAdministrator(
        IReadOnlyCollection<string> currentRoles,
        IReadOnlyCollection<string> targetRoles,
        int administratorCount)
        => currentRoles.Contains(AppRoles.Administrator)
           && !targetRoles.Contains(AppRoles.Administrator)
           && administratorCount <= 1;
}
