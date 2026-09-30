namespace DataTrace.Application.Identity;

public sealed class UserAccount
{
    public required string UserName { get; init; }
    public string DisplayName { get; init; } = "";
    public IReadOnlyList<string> Roles { get; init; } = [];
    public DateTimeOffset? LockoutEnd { get; init; }
    public int AccessFailedCount { get; init; }
    public bool IsLocked { get; init; }
}

public enum UserAdminStatus
{
    Success,
    Warning,
    Error
}

public sealed class UserAdminResult
{
    public UserAdminStatus Status { get; init; }
    public string Message { get; init; } = "";
    public string? AuditError { get; init; }

    /// <summary>
    /// 机器可读的失败分类，供"把错误码带过重定向"这类场景使用（如强制改密页）。
    /// 界面直接显示 <see cref="Message"/> 时用不到它。
    /// </summary>
    public string? Code { get; init; }

    public static UserAdminResult Ok(string message) => new() { Status = UserAdminStatus.Success, Message = message };

    public static UserAdminResult Warn(string message) => new() { Status = UserAdminStatus.Warning, Message = message };

    public static UserAdminResult Fail(string message) => new() { Status = UserAdminStatus.Error, Message = message };
}

/// <summary>用户管理用例。页面只负责对话框和提示，账号规则在这里执行。</summary>
public interface IUserAdministration
{
    Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken = default);

    Task<UserAdminResult> CreateAsync(
        string userName,
        string displayName,
        string password,
        string role,
        string actor,
        CancellationToken cancellationToken = default);

    Task<UserAdminResult> UpdateAsync(
        string userName,
        string displayName,
        IReadOnlyList<string> roles,
        string actor,
        CancellationToken cancellationToken = default);

    Task<UserAdminResult> UnlockAsync(string userName, string actor, CancellationToken cancellationToken = default);

    Task<UserAdminResult> ResetPasswordAsync(
        string userName,
        string password,
        string actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 本人改自己的密码：必须校验旧密码。与 <see cref="ResetPasswordAsync"/> 的区别是后者由管理员
    /// 发起、不需要旧密码，因此不能拿它顶替自助改密。
    /// </summary>
    Task<UserAdminResult> ChangePasswordAsync(
        string userName,
        string currentPassword,
        string newPassword,
        string actor,
        CancellationToken cancellationToken = default);

    Task<UserAdminResult> DeleteAsync(string userName, string actor, CancellationToken cancellationToken = default);
}
