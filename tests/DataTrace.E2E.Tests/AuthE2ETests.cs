using Microsoft.Playwright;
using Xunit;

namespace DataTrace.E2E.Tests;

/// <summary>
/// 登录、越权、登出必须在非 Development 实例上验。
/// </summary>
/// <remarks>
/// Development 会自动免登录登成 admin，于是这几条链路在界面用例里是"永远绿"的：
/// 退出后会被下一个请求重新登回来，角色差异也根本触发不到。这里跑 Staging 实例，
/// 断言的都是服务端就完成的跳转与预渲染文本——那个环境下 CSS 与 _content/* 是 404，
/// 任何依赖 MudBlazor 交互（弹窗、Snackbar）的断言都不会成立。
/// </remarks>
public sealed class AuthE2ETests : AuthE2ETestBase
{
    public AuthE2ETests(AuthWebAppFixture app, BrowserFixture browser)
        : base(app, browser)
    {
    }

    [Fact]
    public void AppTreatsItsOwnDirectoryAsTheContentRoot()
    {
        // 这组用例的进程是在一个空目录下被拉起来的，等同 Windows 服务的工作目录（System32）。
        // 内容根必须是应用自己所在的目录：否则 appsettings.json 读不到，端口悄悄退回 5000、
        // 日志级别与 DataRoot 一起失效，而现场看到的只是"服务连不上"。
        Assert.Contains($"Content root path: {App.AppDirectory}", App.Output);
    }

    [Fact]
    public async Task UnauthenticatedDeepLinkIsChallengedToLoginWithReturnUrl()
    {
        await Page.GotoAsync($"{App.BaseUrl}/query");

        Assert.Equal("/login?ReturnUrl=%2Fquery", CurrentPath());
        await WaitBodyContainsAsync("登录 DataTrace");
        await Page.IsVisibleAsync("button.login-btn");
    }

    [Fact]
    public async Task WrongPasswordStaysOnLoginPageWithReason()
    {
        await SubmitLoginAsync("viewer", "wrong-password");

        Assert.Equal("/login?error=1", CurrentPath());
        await WaitBodyContainsAsync("用户名或密码错误");
    }

    [Fact]
    public async Task ViewerReachingUserAdminLandsOnDeniedPageNamingTheAccountAndTarget()
    {
        await SubmitLoginAsync("viewer", "Viewer@123");
        Assert.Equal("/", CurrentPath());

        await Page.GotoAsync($"{App.BaseUrl}/users");

        Assert.Equal("/denied?ReturnUrl=%2Fusers", CurrentPath());
        await WaitBodyContainsAsync("当前账号 viewer 的角色不足");
        await WaitBodyContainsAsync("你想打开的「用户」需要更高的角色权限");
    }

    [Fact]
    public async Task AdminOpensUserAdminWithoutBeingRedirected()
    {
        await SubmitLoginAsync("admin", "Admin@123");

        await Page.GotoAsync($"{App.BaseUrl}/users");

        // 与上一条唯一的区别就是没有跳到 /denied：这正是 AccessDeniedPath 不能指回 /login 的原因。
        Assert.Equal("/users", CurrentPath());
    }

    [Fact]
    public async Task LogoutEndsTheSessionForReal()
    {
        await SubmitLoginAsync("admin", "Admin@123");
        await Page.GotoAsync($"{App.BaseUrl}/users");
        Assert.Equal("/users", CurrentPath());

        // 退出改成 POST 了，得真的提交那个表单；GET 已不再受理（下一条用例盯着）。
        await SubmitLogoutFormAsync();
        await Page.GotoAsync($"{App.BaseUrl}/users");

        // Development 下这里仍是 /users——免登录中间件会把 admin 重新登回来，越权与登出都验不到。
        Assert.Equal("/login?ReturnUrl=%2Fusers", CurrentPath());
    }

    [Fact]
    public async Task GetLogoutIsRejectedSoAStrayNavigationOrImageCannotEndTheSession()
    {
        await SubmitLoginAsync("admin", "Admin@123");

        // GET 带副作用时，一张 <img src="/account/logout"> 或一次顶层导航就能把在场操作员踢下线。
        var response = await Context.APIRequest.GetAsync(
            $"{App.BaseUrl}/account/logout", new APIRequestContextOptions { MaxRedirects = 0 });

        // 只注册了 POST：GET 落到 soft-404 页（"页面不存在"），不会执行退出。
        Assert.NotEqual(302, response.Status);
        Assert.Contains("页面不存在", await response.TextAsync());

        await Page.GotoAsync($"{App.BaseUrl}/users");
        Assert.Equal("/users", CurrentPath());
    }

    [Fact]
    public async Task LoginPostFromAnotherOriginIsRejected()
    {
        await Page.GotoAsync($"{App.BaseUrl}/login");

        // 登录请求不带凭据，SameSite 帮不上忙；页面又拿不到防伪令牌（交互式渲染），
        // 所以来源校验是这一条链路上唯一的防线，必须有用例盯着。
        // 用 Context.APIRequest 直接发：浏览器里发起时 Origin 由浏览器写死成自己，伪造不出来。
        var response = await Context.APIRequest.PostAsync($"{App.BaseUrl}/account/login",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/x-www-form-urlencoded",
                    ["Origin"] = "http://evil.example"
                },
                Data = "UserName=admin&Password=Admin%40123",
                MaxRedirects = 0
            });

        Assert.Equal(302, response.Status);
        Assert.Contains("error=crosssite", response.Headers["location"]);
    }

    [Fact]
    public async Task RepeatedFailuresLockTheAccountAndThePageSaysSo()
    {
        // operator 只在本用例里登录，锁掉它不会影响其他用例（它们用 admin/engineer/viewer）。
        for (var i = 0; i < 4; i++)
        {
            await SubmitLoginAsync("operator", "wrong-password");
            Assert.Contains("error=1", CurrentPath());
        }

        // 第 5 次失败即触发锁定（阈值 5 次 / 5 分钟）。
        await SubmitLoginAsync("operator", "wrong-password");
        Assert.Contains("error=locked", CurrentPath());

        // 锁定期间连正确口令也不再校验，页面必须说清原因：否则操作员会一直重试，
        // 每失败一次锁定时间就往后推一次。
        await SubmitLoginAsync("operator", "Operator@123");
        Assert.Contains("error=locked", CurrentPath());
        await WaitBodyContainsAsync("已被临时锁定");
    }

    [Fact]
    public async Task SignInReturnsUserToTheDeepLinkThatTriggeredTheChallenge()
    {
        await Page.GotoAsync($"{App.BaseUrl}/query?size=100");
        Assert.Equal("/login?ReturnUrl=%2Fquery%3Fsize%3D100", CurrentPath());

        await PostLoginFormAsync("admin", "Admin@123");

        // 光"能登录"不够：操作工从 MES 拷的是带参数的记录地址，落到看板就等于没进来。
        Assert.Equal("/query?size=100", CurrentPath());
    }

    [Fact]
    public async Task FailedSignInKeepsTheDeepLinkForTheRetry()
    {
        await Page.GotoAsync($"{App.BaseUrl}/users");

        await PostLoginFormAsync("viewer", "wrong-password");
        Assert.Equal("/login?error=1&ReturnUrl=%2Fusers", CurrentPath());
        Assert.Equal("/users", await Page.GetAttributeAsync("input[name=ReturnUrl]", "value") ?? "");

        await PostLoginFormAsync("admin", "Admin@123");
        Assert.Equal("/users", CurrentPath());
    }

    [Fact]
    public async Task ForeignReturnUrlIsIgnoredAndLandsOnTheDashboard()
    {
        await Page.GotoAsync($"{App.BaseUrl}/login?ReturnUrl={Uri.EscapeDataString("//evil.example/steal")}");

        await PostLoginFormAsync("admin", "Admin@123");

        Assert.Equal("/", CurrentPath());
        Assert.DoesNotContain("evil.example", Page.Url);
    }

    [Fact]
    public async Task SignedInUserIsBouncedOffTheLoginPage()
    {
        await SubmitLoginAsync("engineer", "Engineer@123");

        await Page.GotoAsync($"{App.BaseUrl}/login");

        Assert.Equal("/", CurrentPath());
    }
}
