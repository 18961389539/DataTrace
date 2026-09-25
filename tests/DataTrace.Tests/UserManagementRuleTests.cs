using DataTrace.Domain.Constants;
using DataTrace.Domain.Validation;

namespace DataTrace.Tests;

/// <summary>用户名字符集：页面前置校验与 Identity 的 UserOptions 必须同一套规则。</summary>
public class UserNameRulesTests
{
    [Theory]
    [InlineData("zhang")]
    [InlineData("abc")]          // 下界
    [InlineData("a.b_c-d@e+f")]
    [InlineData("user1234")]
    public void Accepts_names_inside_the_identity_whitelist(string name)
        => Assert.Null(UserNameRules.Error(name));

    [Fact]
    public void Accepts_exactly_20_chars_but_rejects_21()
    {
        Assert.Null(UserNameRules.Error(new string('a', 20)));
        Assert.Equal("用户名需为 3–20 位", UserNameRules.Error(new string('a', 21)));
    }

    [Fact]
    public void Rejects_names_shorter_than_three_chars()
        => Assert.Equal("用户名需为 3–20 位", UserNameRules.Error("ab"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Asks_for_a_name_when_it_is_blank(string? name)
        => Assert.Equal("请填写用户名", UserNameRules.Error(name));

    [Theory]
    [InlineData("张三丰")]          // 中文：Identity 默认白名单不接受
    [InlineData("a b")]             // 空格
    [InlineData("a#b")]
    [InlineData("a\\b")]
    public void Rejects_characters_identity_would_refuse_anyway(string name)
        => Assert.Equal("用户名只能包含字母、数字与 - . _ @ +（不能含空格或中文）", UserNameRules.Error(name));

    [Fact]
    public void Trims_surrounding_whitespace_before_judging()
        => Assert.Null(UserNameRules.Error("  zhang  "));

    [Fact]
    public void Whitelist_matches_the_identity_default_so_the_rule_can_be_shared()
    {
        // 注册代码把这份常量回填给 Identity；改动它等于同时改了允许注册的用户名。
        Assert.Equal("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+",
            UserNameRules.AllowedCharacters);
    }
}

/// <summary>角色变更守卫：不能把账号改成无角色，也不能撤掉最后一个管理员。</summary>
public class UserRoleRulesTests
{
    [Fact]
    public void Rejects_an_empty_role_set()
        => Assert.Equal("至少要为用户保留一个角色，否则该账号登录后什么也做不了",
            UserRoleRules.Error([]));

    [Fact]
    public void Rejects_a_null_role_set()
        => Assert.NotNull(UserRoleRules.Error(null));

    [Fact]
    public void Accepts_a_single_role()
        => Assert.Null(UserRoleRules.Error([AppRoles.Operator]));

    [Fact]
    public void Blocks_demoting_the_only_administrator()
        => Assert.True(UserRoleRules.RemovesLastAdministrator(
            [AppRoles.Administrator], [AppRoles.Engineer], administratorCount: 1));

    [Fact]
    public void Allows_demoting_when_another_administrator_remains()
        => Assert.False(UserRoleRules.RemovesLastAdministrator(
            [AppRoles.Administrator], [AppRoles.Engineer], administratorCount: 2));

    [Fact]
    public void Allows_adding_a_second_role_to_the_only_administrator()
        => Assert.False(UserRoleRules.RemovesLastAdministrator(
            [AppRoles.Administrator], [AppRoles.Administrator, AppRoles.Engineer], administratorCount: 1));

    [Fact]
    public void Ignores_users_who_were_not_administrators()
        => Assert.False(UserRoleRules.RemovesLastAdministrator(
            [AppRoles.Operator], [AppRoles.Viewer], administratorCount: 0));
}
