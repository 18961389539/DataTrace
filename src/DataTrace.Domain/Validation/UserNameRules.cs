namespace DataTrace.Domain.Validation;

/// <summary>
/// 用户名格式约束（页面前置校验与 Identity 的 <c>UserOptions</c> 共用这一份）。
/// </summary>
/// <remarks>
/// 允许字符必须与 <c>UserOptions.AllowedUserNameCharacters</c> 完全一致，注册时把
/// <see cref="AllowedCharacters"/> 回填给 Identity，规则就只剩这一处。
/// 两边各写一套的后果是：页面按"3–20 位不含空格"放行了中文与 <c>#</c>，
/// 提交到 Identity 才以 <c>InvalidUserName</c> 失败，用户按页面提示输入却建不了账号。
/// </remarks>
public static class UserNameRules
{
    public const int MinLength = 3;

    public const int MaxLength = 20;

    /// <summary>与 Identity 默认白名单一致：字母、数字与 <c>-._@+</c>。</summary>
    public const string AllowedCharacters =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";

    /// <summary>校验用户名；返回 null 表示通过，否则返回可以直接给管理员看的那句话。</summary>
    public static string? Error(string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return "请填写用户名";
        }

        var trimmed = userName.Trim();
        if (trimmed.Length < MinLength || trimmed.Length > MaxLength)
        {
            return $"用户名需为 {MinLength}–{MaxLength} 位";
        }

        return trimmed.All(AllowedCharacters.Contains)
            ? null
            : "用户名只能包含字母、数字与 - . _ @ +（不能含空格或中文）";
    }
}
