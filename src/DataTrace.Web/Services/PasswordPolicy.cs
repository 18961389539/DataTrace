using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DataTrace.Web.Services;

/// <summary>
/// 前端密码规则的唯一来源：读取 Identity 的 <see cref="PasswordOptions"/>，
/// 不在页面里再写一套魔法数字。后端策略本身不在这里修改。
/// </summary>
public sealed class PasswordPolicy
{
    private readonly PasswordOptions _options;

    public PasswordPolicy(IOptions<IdentityOptions> identityOptions)
    {
        _options = identityOptions.Value.Password;
    }

    public PasswordOptions Options => _options;

    /// <summary>用于密码框 HelperText 的完整中文规则说明。</summary>
    public string HelperText
    {
        get
        {
            var parts = new List<string> { $"至少 {_options.RequiredLength} 位" };
            if (_options.RequireUppercase)
            {
                parts.Add("大写字母");
            }

            if (_options.RequireLowercase)
            {
                parts.Add("小写字母");
            }

            if (_options.RequireDigit)
            {
                parts.Add("数字");
            }

            if (_options.RequireNonAlphanumeric)
            {
                parts.Add("特殊字符");
            }

            if (_options.RequiredUniqueChars > 1)
            {
                parts.Add($"至少 {_options.RequiredUniqueChars} 个不同字符");
            }

            // "至少 8 位，须包含大写字母、小写字母与数字"
            if (parts.Count == 1)
            {
                return parts[0];
            }

            var head = parts[0];
            var rest = parts.Skip(1).ToList();
            return rest.Count == 1
                ? $"{head}，须包含{rest[0]}"
                : $"{head}，须包含{string.Join("、", rest.Take(rest.Count - 1))}与{rest[^1]}";
        }
    }

    /// <summary>
    /// 本地校验。返回第一条未满足规则的中文说明；全部满足则返回 null。
    /// </summary>
    public string? Validate(string? password)
    {
        password ??= "";

        if (password.Length < _options.RequiredLength)
        {
            return $"密码长度至少 {_options.RequiredLength} 位";
        }

        if (_options.RequireUppercase && !password.Any(char.IsUpper))
        {
            return "密码须包含至少 1 个大写字母";
        }

        if (_options.RequireLowercase && !password.Any(char.IsLower))
        {
            return "密码须包含至少 1 个小写字母";
        }

        if (_options.RequireDigit && !password.Any(char.IsDigit))
        {
            return "密码须包含至少 1 个数字";
        }

        if (_options.RequireNonAlphanumeric && password.All(char.IsLetterOrDigit))
        {
            return "密码须包含至少 1 个特殊字符";
        }

        if (_options.RequiredUniqueChars > 1
            && password.Distinct().Count() < _options.RequiredUniqueChars)
        {
            return $"密码须包含至少 {_options.RequiredUniqueChars} 个不同字符";
        }

        return null;
    }

    /// <summary>把 IdentityResult 错误码翻成中文；未知码回退原文。</summary>
    public static string FormatIdentityErrors(IdentityResult result)
    {
        if (result.Succeeded)
        {
            return "";
        }

        return string.Join("；", result.Errors.Select(TranslateError));
    }

    public static string TranslateError(IdentityError error) => error.Code switch
    {
        "PasswordTooShort" => "密码长度不足",
        "PasswordRequiresDigit" => "密码须包含数字",
        "PasswordRequiresLower" => "密码须包含小写字母",
        "PasswordRequiresUpper" => "密码须包含大写字母",
        "PasswordRequiresNonAlphanumeric" => "密码须包含特殊字符",
        "PasswordRequiresUniqueChars" => "密码中不同字符数量不足",
        "PasswordMismatch" => "密码不正确",
        "DuplicateUserName" => "用户名已存在",
        "InvalidUserName" => "用户名不合法",
        _ => string.IsNullOrWhiteSpace(error.Description) ? error.Code : error.Description
    };
}
