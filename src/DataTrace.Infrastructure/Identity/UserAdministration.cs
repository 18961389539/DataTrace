using DataTrace.Application.Configuration;
using DataTrace.Application.Identity;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Validation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Identity;

public sealed class UserAdministration : IUserAdministration
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly IAuditLogger _audit;

    public UserAdministration(UserManager<ApplicationUser> users, IAuditLogger audit)
    {
        _users = users;
        _audit = audit;
    }

    public async Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken = default)
    {
        var list = await _users.Users.ToListAsync(cancellationToken).ConfigureAwait(false);
        var accounts = new List<UserAccount>(list.Count);
        foreach (var user in list)
        {
            var roles = await _users.GetRolesAsync(user).ConfigureAwait(false);
            accounts.Add(ToAccount(user, roles));
        }

        return accounts
            .OrderBy(account => account.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<UserAdminResult> CreateAsync(
        string userName,
        string displayName,
        string password,
        string role,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (await _users.FindByNameAsync(userName).ConfigureAwait(false) is not null)
        {
            return UserAdminResult.Fail("该用户名已存在");
        }

        var user = new ApplicationUser
        {
            UserName = userName,
            DisplayName = displayName,
            Email = $"{userName}@datatrace.local"
        };
        var created = await _users.CreateAsync(user, password).ConfigureAwait(false);
        if (!created.Succeeded)
        {
            var detail = IdentityErrorText.Format(created);
            return await FinishAsync(actor, "Create", userName, null, "create failed; " + detail, "Failure", UserAdminResult.Fail(detail), cancellationToken)
                .ConfigureAwait(false);
        }

        var roleResult = await _users.AddToRoleAsync(user, role).ConfigureAwait(false);
        if (!roleResult.Succeeded)
        {
            var detail = IdentityErrorText.Format(roleResult);
            return await FinishAsync(
                    actor,
                    "Create",
                    userName,
                    null,
                    $"partial; account created; role={role} failed; {detail}",
                    "Failure",
                    UserAdminResult.Warn("用户已创建，但角色分配失败：" + detail),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await FinishAsync(
                actor,
                "Create",
                userName,
                null,
                $"display={displayName}; role={role}",
                "Success",
                UserAdminResult.Ok($"已创建 {userName}"),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<UserAdminResult> UpdateAsync(
        string userName,
        string displayName,
        IReadOnlyList<string> roles,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (UserRoleRules.Error(roles) is { } roleError)
        {
            return UserAdminResult.Warn(roleError);
        }

        var user = await _users.FindByNameAsync(userName).ConfigureAwait(false);
        if (user is null)
        {
            return UserAdminResult.Fail("用户不存在，可能已被删除");
        }

        var currentRoles = (await _users.GetRolesAsync(user).ConfigureAwait(false)).ToList();
        var previousDisplay = user.DisplayName;
        var administrators = await _users.GetUsersInRoleAsync(AppRoles.Administrator).ConfigureAwait(false);
        if (UserRoleRules.RemovesLastAdministrator(currentRoles, roles, administrators.Count))
        {
            return UserAdminResult.Fail("系统至少需要保留一个管理员账号，不能撤掉最后一个管理员");
        }

        var failures = new List<string>();
        var toRemove = currentRoles.Except(roles).ToList();
        var toAdd = roles.Except(currentRoles).ToList();
        if (toRemove.Count > 0)
        {
            var removed = await _users.RemoveFromRolesAsync(user, toRemove).ConfigureAwait(false);
            if (!removed.Succeeded)
            {
                failures.Add("撤销角色失败：" + IdentityErrorText.Format(removed));
            }
        }

        if (toAdd.Count > 0)
        {
            var added = await _users.AddToRolesAsync(user, toAdd).ConfigureAwait(false);
            if (!added.Succeeded)
            {
                failures.Add("分配角色失败：" + IdentityErrorText.Format(added));
            }
        }

        if (user.DisplayName != displayName)
        {
            user.DisplayName = displayName;
            var updated = await _users.UpdateAsync(user).ConfigureAwait(false);
            if (!updated.Succeeded)
            {
                failures.Add("更新显示名失败：" + IdentityErrorText.Format(updated));
            }
        }

        var actualUser = await _users.FindByIdAsync(user.Id).ConfigureAwait(false);
        var actualRoles = actualUser is null
            ? currentRoles
            : (await _users.GetRolesAsync(actualUser).ConfigureAwait(false)).ToList();
        var actualDisplay = actualUser?.DisplayName ?? displayName;
        var details = failures.Count == 0
            ? $"display={actualDisplay}; roles={string.Join(',', actualRoles)}"
            : $"partial; actual display={actualDisplay}; actual roles={string.Join(',', actualRoles)}; errors={string.Join(" | ", failures)}";
        var outcome = failures.Count == 0 ? UserAdminResult.Ok($"已更新 {userName}") : UserAdminResult.Fail("部分修改未生效：" + string.Join("；", failures));
        return await FinishAsync(
                actor,
                "Update",
                userName,
                $"display={previousDisplay}; roles={string.Join(',', currentRoles)}",
                details,
                failures.Count == 0 ? "Success" : "Failure",
                outcome,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<UserAdminResult> UnlockAsync(string userName, string actor, CancellationToken cancellationToken = default)
    {
        var user = await _users.FindByNameAsync(userName).ConfigureAwait(false);
        if (user is null)
        {
            return UserAdminResult.Fail("用户不存在，可能已被删除");
        }

        var failedBefore = user.AccessFailedCount;
        var cleared = await _users.SetLockoutEndDateAsync(user, null).ConfigureAwait(false);
        var counted = await _users.ResetAccessFailedCountAsync(user).ConfigureAwait(false);
        if (cleared.Succeeded && counted.Succeeded)
        {
            return await FinishAsync(
                    actor,
                    "Unlock",
                    userName,
                    $"failed={failedBefore}",
                    "lockout cleared",
                    "Success",
                    UserAdminResult.Ok($"已解除 {userName} 的登录锁定"),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var detail = IdentityErrorText.Format(cleared.Succeeded ? counted : cleared);
        return await FinishAsync(
                actor,
                "Unlock",
                userName,
                $"failed={failedBefore}",
                detail,
                "Failure",
                UserAdminResult.Fail("解除锁定失败：" + detail),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<UserAdminResult> ResetPasswordAsync(
        string userName,
        string password,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var user = await _users.FindByNameAsync(userName).ConfigureAwait(false);
        if (user is null)
        {
            return UserAdminResult.Fail("用户不存在，可能已被删除");
        }

        var token = await _users.GeneratePasswordResetTokenAsync(user).ConfigureAwait(false);
        var reset = await _users.ResetPasswordAsync(user, token, password).ConfigureAwait(false);
        if (reset.Succeeded)
        {
            return await FinishAsync(
                    actor,
                    "ResetPassword",
                    userName,
                    null,
                    "password reset",
                    "Success",
                    UserAdminResult.Ok($"已重置 {userName} 的密码"),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var detail = IdentityErrorText.Format(reset);
        return await FinishAsync(
                actor,
                "ResetPassword",
                userName,
                null,
                detail,
                "Failure",
                UserAdminResult.Fail("重置失败：" + detail),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<UserAdminResult> DeleteAsync(string userName, string actor, CancellationToken cancellationToken = default)
    {
        if (string.Equals(userName, actor, StringComparison.Ordinal))
        {
            return UserAdminResult.Warn("不能删除当前登录账号");
        }

        var user = await _users.FindByNameAsync(userName).ConfigureAwait(false);
        if (user is null)
        {
            return UserAdminResult.Fail("用户不存在，可能已被删除");
        }

        var roles = (await _users.GetRolesAsync(user).ConfigureAwait(false)).ToList();
        if (roles.Contains(AppRoles.Administrator))
        {
            var administrators = await _users.GetUsersInRoleAsync(AppRoles.Administrator).ConfigureAwait(false);
            if (administrators.Count <= 1)
            {
                return UserAdminResult.Fail("系统至少需要保留一个管理员账号");
            }
        }

        var deleted = await _users.DeleteAsync(user).ConfigureAwait(false);
        if (!deleted.Succeeded)
        {
            var detail = IdentityErrorText.Format(deleted);
            return await FinishAsync(
                    actor,
                    "Delete",
                    userName,
                    $"roles={string.Join(',', roles)}",
                    detail,
                    "Failure",
                    UserAdminResult.Fail(detail),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await FinishAsync(
                actor,
                "Delete",
                userName,
                $"roles={string.Join(',', roles)}",
                null,
                "Success",
                UserAdminResult.Ok($"已删除 {userName}"),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<UserAdminResult> FinishAsync(
        string actor,
        string action,
        string key,
        string? oldValue,
        string? newValue,
        string outcome,
        UserAdminResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            if (outcome == "Success")
            {
                await _audit.WriteAsync(actor, action, "User", key, oldValue, newValue, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _audit.WriteAsync(actor, action, "User", key, oldValue, newValue, cancellationToken, outcome)
                    .ConfigureAwait(false);
            }
            return result;
        }
        catch (Exception ex)
        {
            return new UserAdminResult
            {
                Status = result.Status,
                Message = result.Message,
                AuditError = ex.Message
            };
        }
    }

    private static UserAccount ToAccount(ApplicationUser user, IList<string> roles)
    {
        var locked = user.LockoutEnd is { } end && end > DateTimeOffset.Now;
        return new UserAccount
        {
            UserName = user.UserName ?? "",
            DisplayName = user.DisplayName,
            Roles = roles.ToList(),
            LockoutEnd = user.LockoutEnd,
            AccessFailedCount = user.AccessFailedCount,
            IsLocked = locked
        };
    }
}
