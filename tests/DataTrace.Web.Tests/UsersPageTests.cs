using DataTrace.Domain.Constants;
using DataTrace.Domain.Validation;
using DataTrace.Infrastructure;
using DataTrace.Infrastructure.Identity;
using DataTrace.Infrastructure.Persistence;
using DataTrace.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DataTrace.Web.Tests;

/// <summary>
/// 用户管理页。
/// </summary>
/// <remarks>
/// 这里接的是一套真实 Identity（临时 SQLite 库）而不是假 UserManager：
/// 要验的恰恰是"UserManager 回来的东西怎么呈现"和"守卫是否拦在写入之前"，
/// 把 UserManager 替掉之后，剩下的就只有页面自己那几个 if 了。
/// </remarks>
public class UsersPageTests : WebTestBase, IDisposable
{
    private const string DbFileName = "config.db";

    private readonly string _dbDir;

    public UsersPageTests()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), "datatrace-web-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dbDir);

        Context.Services.AddLogging();
        // Pooling=False：连接一关就真的放开文件句柄，否则临时库删不掉（测试之间也会互相绊住）。
        Context.Services.AddDbContext<ConfigDbContext>(
            o => o.UseSqlite($"Data Source={Path.Combine(_dbDir, DbFileName)};Pooling=False"));
        // 与产品同一份账号策略；策略改严之后，这里的用例会跟着一起变红。
        Context.Services.AddDataTraceIdentity();
        Context.Services.AddSingleton<PasswordPolicy>();
        // 角色编辑要真的把对话框里的复选框点出来，所以用真 DialogService 而不是替身。
        // 必须在渲染任何组件之前注册：TestServiceProvider 一旦被取用就不许再加注册。
        Context.Services.AddSingleton<IDialogService, DialogService>();

        // 当前登录账号由 WebTestBase 的测试授权固定成 admin。
        Seed("admin", "管理员", "Admin@123", AppRoles.Administrator);
        Seed("zhang", "张工", "Zhang@1234", AppRoles.Operator);
    }

    /// <summary>临时配置库跟着用例一起删掉。</summary>
    void IDisposable.Dispose()
    {
        base.Dispose();
        try
        {
            Directory.Delete(_dbDir, recursive: true);
        }
        catch
        {
            // 句柄没放开就留给系统临时目录
        }
    }

    // ---------- P3：不再预填共享初始口令 ----------

    [Fact]
    public void New_user_form_does_not_prefill_a_shared_password()
    {
        var cut = RenderUsers();

        var password = cut.FindAll("input[type=password]").First();
        Assert.True(
            string.IsNullOrEmpty(password.GetAttribute("value")),
            $"初始密码不该预填，实际「{password.GetAttribute("value")}」");

        // 规则说明仍在，只是值要管理员自己填。
        Assert.Contains("必填", cut.Markup);
    }

    [Fact]
    public void Submitting_without_a_password_is_refused_on_the_field_and_creates_nothing()
    {
        var cut = RenderUsers();

        TypeInto(cut, "用户名", "newbie");
        TypeInto(cut, "显示名", "新来的");
        ClickButton(cut, "创建");
        cut.WaitForAssertion(() => Assert.Contains("请填写初始密码", cut.Markup));

        Assert.DoesNotContain("newbie", UserNames());
    }

    // ---------- P8：用户名规则与 Identity 同源 ----------

    [Fact]
    public void A_name_identity_would_refuse_is_rejected_before_the_write()
    {
        var cut = RenderUsers();

        TypeInto(cut, "用户名", "张三丰");
        TypeInto(cut, "显示名", "三丰");
        TypeInto(cut, "初始密码", "Newbie@123");
        ClickButton(cut, "创建");

        // 报在字段上，而不是等 Identity 回一句英文错误码再弹 Toast。
        cut.WaitForAssertion(() => Assert.Contains(UserNameRules.Error("张三丰")!, cut.Markup));
        Assert.DoesNotContain("张三丰", UserNames());
    }

    // ---------- P2：审计失败与主操作成功分开说 ----------

    [Fact]
    public void Audit_failure_is_reported_without_pretending_the_create_failed()
    {
        Audit
            .Setup(a => a.WriteAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("审计库只读"));

        var cut = RenderUsers();
        TypeInto(cut, "用户名", "newbie");
        TypeInto(cut, "显示名", "新来的");
        TypeInto(cut, "初始密码", "Newbie@123");
        ClickButton(cut, "创建");

        cut.WaitForAssertion(() =>
            Assert.Contains(Toast.Messages, m => m.StartsWith("操作已完成，但审计记录失败", StringComparison.Ordinal)));

        // 账号确实建出来了，两条提示各说各的事实。
        Assert.Contains(Toast.Messages, m => m.Contains("已创建 newbie"));
        Assert.Contains("newbie", UserNames());
    }

    // ---------- P9：读失败不留半份名单 ----------

    [Fact]
    public void Load_failure_empties_the_list_and_says_so()
    {
        File.Delete(Path.Combine(_dbDir, DbFileName));

        var cut = RenderUsers();

        cut.WaitForAssertion(() => Assert.Contains("列表已清空", cut.Markup));
        Assert.Contains(Toast.Messages, m => m.Contains("加载用户失败，列表已清空"));
        // 空表 + 明确的失败说明，而不是"这几个账号看起来被删了"。
        Assert.DoesNotContain("zhang", cut.Markup);
    }

    // ---------- P4：锁定可见、可解 ----------

    [Fact]
    public void A_locked_account_is_flagged_and_can_be_unlocked()
    {
        LockAccount("zhang", TimeSpan.FromMinutes(5));

        var cut = RenderUsers();
        cut.WaitForAssertion(() => Assert.Contains("已锁定", cut.Markup));

        ClickButton(cut, "解除锁定");

        cut.WaitForAssertion(() => Assert.Contains(Toast.Messages, m => m.Contains("已解除 zhang 的登录锁定")));
        Assert.Null(LockoutEndOf("zhang"));
    }

    // ---------- P5：角色可编辑，且撤不掉最后一个管理员 ----------

    [Fact]
    public void Cannot_strip_the_administrator_role_from_the_only_administrator()
    {
        var provider = RenderDialogHost();
        var cut = RenderUsers();

        ClickRowButton(cut, "admin", "编辑");
        provider.WaitForAssertion(() => Assert.Contains("编辑用户", provider.Markup));

        // 管理员 → 工程师：对话框自己会拦"一个角色都不留"，拦不住"最后一个管理员"。
        ToggleDialogRole(provider, "管理员", on: false);
        ToggleDialogRole(provider, "工程师", on: true);
        ClickButton(provider, "保存");

        cut.WaitForAssertion(() => Assert.Contains(Toast.Messages, m => m.Contains("不能撤掉最后一个管理员")));
        Assert.Contains(AppRoles.Administrator, RolesOf("admin"));
        Assert.DoesNotContain(AppRoles.Engineer, RolesOf("admin"));
    }

    [Fact]
    public void Editing_a_user_rewrites_display_name_and_roles_and_is_audited()
    {
        var provider = RenderDialogHost();
        var cut = RenderUsers();

        ClickRowButton(cut, "zhang", "编辑");
        provider.WaitForAssertion(() => Assert.Contains("编辑用户", provider.Markup));

        TypeInto(provider, "显示名", "张工（调岗）");
        ToggleDialogRole(provider, "操作员", on: false);
        ToggleDialogRole(provider, "工程师", on: true);
        ClickButton(provider, "保存");

        cut.WaitForAssertion(() => Assert.Contains(Toast.Messages, m => m.Contains("已更新 zhang")));

        // 调岗不必删号重建：角色改掉了，历史审计主体还是同一个账号。
        var user = User("zhang")!;
        Assert.Equal("张工（调岗）", user.DisplayName);
        Assert.Equal([AppRoles.Engineer], RolesOf("zhang"));
        Audit.Verify(
            a => a.WriteAsync("admin", "Update", "User", "zhang",
                It.Is<string?>(old => old != null && old.Contains("Operator")),
                It.Is<string?>(@new => @new != null && @new.Contains("Engineer")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ---------- 辅助 ----------

    private IRenderedComponent<DataTrace.Web.Components.Pages.Users> RenderUsers()
    {
        RenderPopoverHost();
        return Context.RenderComponent<DataTrace.Web.Components.Pages.Users>();
    }

    /// <summary>对话框宿主。必须先于页面渲染好，DialogService 才知道往哪儿放对话框。</summary>
    private IRenderedComponent<MudDialogProvider> RenderDialogHost()
    {
        RenderPopoverHost();
        var provider = Context.RenderComponent<MudDialogProvider>();
        provider.Render();
        return provider;
    }

    /// <summary>按行内文字点按钮：几行操作列长得一样，只能先定位到那一行。</summary>
    private static void ClickRowButton(IRenderedFragment cut, string rowText, string buttonText)
    {
        var row = cut.FindAll("tr").Single(r => r.TextContent.Contains(rowText));
        var button = row.QuerySelectorAll("button").Single(b => b.TextContent.Contains(buttonText));
        button.Click();
    }

    /// <summary>按角色名勾/取消对话框里的复选框（MudCheckBox 把 aria-label 挂在包裹元素上）。</summary>
    private static void ToggleDialogRole(IRenderedFragment cut, string roleText, bool on)
    {
        var box = cut.FindAll("input[type=checkbox]")
            .Single(i => i.Closest("[aria-label]")?.GetAttribute("aria-label") == roleText);
        box.Change(on);
    }

    private void Seed(string name, string display, string password, string role)
    {
        using var scope = Context.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ConfigDbContext>().Database.EnsureCreated();

        // 4 个角色按产品的播种口径一次备齐：编辑用户的用例要能把操作员改成工程师，
        // 缺角色会以 "Role ENGINEER does not exist" 的形式在写入那一刻才炸。
        var roles = sp.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var known in AppRoles.All)
        {
            if (!roles.RoleExistsAsync(known).GetAwaiter().GetResult())
            {
                roles.CreateAsync(new IdentityRole(known)).GetAwaiter().GetResult();
            }
        }

        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = name, DisplayName = display, Email = $"{name}@datatrace.local" };
        var created = users.CreateAsync(user, password).GetAwaiter().GetResult();
        Assert.True(created.Succeeded, string.Join("；", created.Errors.Select(e => e.Description)));
        users.AddToRoleAsync(user, role).GetAwaiter().GetResult();
    }

    /// <summary>把账号打到锁定态，等价于"连续失败次数超限"。</summary>
    private void LockAccount(string name, TimeSpan duration)
    {
        using var scope = Context.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = users.FindByNameAsync(name).GetAwaiter().GetResult()!;
        users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.Add(duration)).GetAwaiter().GetResult();
    }

    private ApplicationUser? User(string name)
    {
        using var scope = Context.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByNameAsync(name).GetAwaiter().GetResult();
    }

    private DateTimeOffset? LockoutEndOf(string name) => User(name)?.LockoutEnd;

    private IReadOnlyList<string> RolesOf(string name)
    {
        using var scope = Context.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = users.FindByNameAsync(name).GetAwaiter().GetResult()!;
        return users.GetRolesAsync(user).GetAwaiter().GetResult().ToList();
    }

    private IReadOnlyList<string> UserNames()
    {
        using var scope = Context.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ConfigDbContext>()
            .Users.Select(u => u.UserName!).ToList();
    }
}
