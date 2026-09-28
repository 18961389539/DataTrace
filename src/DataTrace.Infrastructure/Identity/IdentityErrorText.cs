using Microsoft.AspNetCore.Identity;

namespace DataTrace.Infrastructure.Identity;

/// <summary>Identity 错误码的中文。页面密码框和用户管理用例共用这一份。</summary>
public static class IdentityErrorText
{
    public static string Format(IdentityResult result)
    {
        if (result.Succeeded)
        {
            return "";
        }

        return string.Join("；", result.Errors.Select(Translate));
    }

    public static string Translate(IdentityError error) => error.Code switch
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
