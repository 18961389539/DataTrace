using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DataTrace.E2E.Tests;

/// <summary>
/// 起一个隔离的 DataTrace.Web 实例给 E2E 用。
/// </summary>
/// <remarks>
/// 三件事必须绕开开发机上的常态：常驻的 DataTrace.Web.exe 占着 5080 与默认输出目录，
/// 所以端口走配置项覆盖（Kestrel:Endpoints:Http:Url，不是 ASPNETCORE_URLS，后者会被
/// appsettings 静默盖掉）、数据目录指向临时目录。
/// 环境名由派生类决定：界面用例必须 Development（否则 app.css 与 _content/* 取不到，
/// 页面看起来"坏了"），鉴权用例必须非 Development（Development 会免登录直接登成 admin）。
/// </remarks>
public abstract class WebAppHost : IDisposable
{
    private Process? _process;
    private StringBuilder _output = new();
    private string? _dataRoot;
    private Task? _start;

    protected WebAppHost(string environmentName)
    {
        EnvironmentName = environmentName;
    }

    protected string EnvironmentName { get; }

    public string BaseUrl { get; private set; } = "";

    /// <summary>应用自己的目录（也就是内容根应该落到的地方）。</summary>
    public string AppDirectory { get; private set; } = "";

    /// <summary>数据目录：夹具退出时整个删掉。</summary>
    protected string DataRoot => _dataRoot ?? "";

    /// <summary>
    /// 进程的工作目录。默认跟应用同目录（相当于 `cd 安装目录 && 启动`），
    /// 需要模拟"工作目录不是安装目录"的用例可以改写这里。
    /// </summary>
    protected virtual string ResolveWorkingDirectory(string appDirectory) => appDirectory;

    public int Port { get; private set; }

    public Task Started => _start ??= StartAsync();

    private async Task StartAsync()
    {
        var appDll = LocateAppDll();
        AppDirectory = Path.GetDirectoryName(appDll)!;
        Port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{Port}";
        _dataRoot = Path.Combine(Path.GetTempPath(), "datatrace-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var startInfo = new ProcessStartInfo("dotnet", $"\"{appDll}\"")
        {
            WorkingDirectory = ResolveWorkingDirectory(AppDirectory),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = EnvironmentName;
        startInfo.Environment["Kestrel__Endpoints__Http__Url"] = BaseUrl;
        startInfo.Environment["DataRoot"] = _dataRoot;

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 DataTrace.Web");
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_output) _output.AppendLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_output) _output.AppendLine(e.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitUntilReadyAsync();
    }

    public string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    private async Task WaitUntilReadyAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
            {
                throw new InvalidOperationException($"DataTrace.Web 启动失败（退出码 {_process.ExitCode}）：\n{Output}");
            }

            try
            {
                // /login 在 Development 会被自动登成 admin 返 302，非 Development 直接 200，
                // 两种响应都说明管线通了。
                using var response = await http.GetAsync($"{BaseUrl}/login");
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Found)
                {
                    return;
                }
            }
            catch (Exception)
            {
                // 还没起来，继续等
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"DataTrace.Web 在 60 秒内没有就绪。\n{Output}");
    }

    /// <summary>按 --artifacts-path 布局优先找测试产物同级的应用，其次退回常规 bin。</summary>
    private static string LocateAppDll()
    {
        var configured = Environment.GetEnvironmentVariable("DATATRACE_APP_DLL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured!;
        }

        var fromArtifacts = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "DataTrace.Web", "debug", "DataTrace.Web.dll"));
        if (File.Exists(fromArtifacts))
        {
            return fromArtifacts;
        }

        var source = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "DataTrace.Web", "bin", "Debug", "net8.0", "DataTrace.Web.dll"));
        if (File.Exists(source))
        {
            return source;
        }

        throw new FileNotFoundException(
            $"找不到 DataTrace.Web.dll。试过：{configured ?? "(未设 DATATRACE_APP_DLL)"}、{fromArtifacts}、{source}");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }

            _process?.Dispose();
        }
        catch
        {
            // 收尾失败不影响测试结论
        }

        if (_dataRoot is not null && Directory.Exists(_dataRoot))
        {
            try
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
            catch
            {
                // 句柄未释放时不强求清理
            }
        }
    }
}

/// <summary>界面用例用的实例：Development 免登录、静态资源齐。</summary>
public sealed class WebAppFixture : WebAppHost
{
    public WebAppFixture()
        : base("Development")
    {
    }
}

/// <summary>
/// 鉴权用例用的实例：Staging 关掉了免登录，才会真的产生登录挑战、越权跳转和登出。
/// 该环境下 app.css 与 _content/* 取不到（静态资源只在 Development 装载），
/// 所以这里只断言服务端就完成的跳转与预渲染文本，不要指望弹窗、输入框之类的交互。
/// </summary>
public sealed class AuthWebAppFixture : WebAppHost
{
    public AuthWebAppFixture()
        : base("Staging")
    {
    }

    /// <summary>
    /// 故意在一个空目录里拉起进程，模拟 Windows 服务（工作目录 = System32）的形态：
    /// 内容根必须是应用自己的目录，否则 appsettings.json 根本不会被读到。
    /// </summary>
    protected override string ResolveWorkingDirectory(string appDirectory)
    {
        var cwd = Path.Combine(DataRoot, "cwd");
        Directory.CreateDirectory(cwd);
        return cwd;
    }
}

/// <summary>整套 E2E 共用一个应用实例与一个浏览器。</summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = null!;

    /// <summary>
    /// 默认用本机已装的 Edge（channel=msedge），免下载浏览器；
    /// DATATRACE_E2E_BROWSER=chromium 时改用 Playwright 自带 Chromium。
    /// </summary>
    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        var useChromium = string.Equals(
            Environment.GetEnvironmentVariable("DATATRACE_E2E_BROWSER"), "chromium", StringComparison.OrdinalIgnoreCase);

        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Channel = useChromium ? null : "msedge"
        });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }
}

[CollectionDefinition("e2e")]
public sealed class E2ECollection : ICollectionFixture<WebAppFixture>, ICollectionFixture<BrowserFixture>
{
}

/// <summary>鉴权用例单独一个集合：跑在非 Development 实例上，因此也有自己的浏览器。</summary>
[CollectionDefinition("e2e-auth")]
public sealed class AuthE2ECollection : ICollectionFixture<AuthWebAppFixture>, ICollectionFixture<BrowserFixture>
{
}

/// <summary>
/// 视觉回归单独一个集合。像素基线录的是配置页这类"有值"的界面，
/// 跟界面用例共用实例就会被它们改过的配置污染（扫描周期曾经把基线录成 80，
/// 换一下执行顺序就红），所以给它一个全新的应用实例与数据目录。
/// </summary>
[CollectionDefinition("e2e-visual")]
public sealed class VisualE2ECollection : ICollectionFixture<WebAppFixture>, ICollectionFixture<BrowserFixture>
{
}

/// <summary>E2E 用例基类：每个用例一个全新上下文，互不串 cookie。</summary>
[Collection("e2e")]
public abstract class E2ETestBase : IAsyncLifetime
{
    protected WebAppHost App { get; }

    protected IBrowser Browser { get; }

    protected IBrowserContext Context { get; private set; } = null!;

    protected IPage Page { get; private set; } = null!;

    protected E2ETestBase(WebAppHost app, BrowserFixture browser)
    {
        App = app;
        Browser = browser.Browser;
    }

    public async Task InitializeAsync()
    {
        await App.Started;
        Context = await Browser.NewContextAsync();
        Page = await Context.NewPageAsync();
        Page.SetDefaultTimeout(20000);
    }

    public async Task DisposeAsync()
    {
        await Context.DisposeAsync();
    }

    /// <summary>等某个元素出现。Blazor Server 的交互要等 SignalR circuit，用可见元素比固定等待稳。</summary>
    protected async Task<IElementHandle> WaitForAsync(string selector)
        => await Page.WaitForSelectorAsync(selector) ?? throw new TimeoutException($"等不到元素：{selector}");

    /// <summary>等一段文字渲染到页面上。Blazor 的内容是异步补的，按文本等最省事。</summary>
    protected Task WaitBodyContainsAsync(string text, int timeoutMs = 20000)
        => Page.WaitForFunctionAsync(
            "t => document.body.innerText.includes(t)",
            text,
            new PageWaitForFunctionOptions { Timeout = timeoutMs });

    /// <summary>
    /// 等 SignalR circuit 真的接管。
    /// </summary>
    /// <remarks>
    /// 本应用的"导航后把焦点移到页标题"只在接管之后发生，所以它是个现成的就绪信号
    /// （见 AccessibleNameE2ETests；那里用它判断页面是不是只渲染了静态预渲染的一半）。
    /// 需要它的场景：元素在预渲染 HTML 里就已经存在（按钮、输入框），
    /// 而 circuit 接管时会把这段 DOM 整段重建 —— 在那之前发出的点击/回车会被丢掉，
    /// 失败方式往往是"什么都没发生"。页面上浮层（tooltip/下拉）越多，这个窗口越明显。
    /// </remarks>
    protected Task WaitForCircuitReadyAsync()
        => Page.WaitForFunctionAsync(
            "() => document.activeElement === document.querySelector('h5')",
            arg: null,
            new PageWaitForFunctionOptions { Timeout = 20000 });

    /// <summary>在查询表单里填条件并回车提交——页面自己就提示"在输入框内按回车即查询"。</summary>
    protected async Task SearchAsync(ILocator input, string value)
    {
        await input.FillAsync(value);
        await input.PressAsync("Enter");
    }

    /// <summary>
    /// 反复执行一个动作直到页面出现预期文字。
    /// 预渲染的 DOM 会在 circuit 接手时被整段重建，这个窗口里发出的点击/回车会被丢掉
    /// （与登录表被抹空是同一个根因），而失败方式往往是"什么都没发生"，后续断言静默空过。
    /// </summary>
    protected async Task ActUntilAsync(Func<Task> action, string expectedText, string label, int attempts = 10)
    {
        for (var i = 0; i < attempts; i++)
        {
            await action();
            try
            {
                await WaitBodyContainsAsync(expectedText, 3000);
                return;
            }
            catch (TimeoutException)
            {
                await Task.Delay(500);
            }
        }

        throw new TimeoutException($"{attempts} 次{label}后页面仍未出现「{expectedText}」");
    }
}

/// <summary>
/// 鉴权用例基类：跑在 Staging 实例上，cookie 认证才会真的生效。
/// 登录/登出/越权跳转全部在服务端完成，因此不依赖那个环境里 404 的 CSS 与 JS。
/// </summary>
[Collection("e2e-auth")]
public abstract class AuthE2ETestBase : E2ETestBase
{
    protected AuthE2ETestBase(AuthWebAppFixture app, BrowserFixture browser)
        : base(app, browser)
    {
    }

    /// <summary>从登录页提交（不带回跳目标）。</summary>
    protected async Task SubmitLoginAsync(string userName, string password)
    {
        await Page.GotoAsync($"{App.BaseUrl}/login");
        await PostLoginFormAsync(userName, password);
    }

    /// <summary>
    /// 在当前这个登录页上提交 —— 被挑战跳过来的地址带着 ReturnUrl，
    /// 从 /login 重新进就会丢掉它，所以回跳用例必须走这里。
    /// </summary>
    /// <remarks>
    /// 赋值与提交放在同一个 JS 任务里，不能拆成 FillAsync + ClickAsync：登录表在预渲染的
    /// DOM 上，circuit 起来后第一次渲染会把这一段重建，把先前填进去的值一起抹掉，
    /// 于是服务端收到的是空用户名空密码。真人手打碰不到这个窗口，自动化必撞。
    /// </remarks>
    protected async Task PostLoginFormAsync(string userName, string password)
    {
        await Page.WaitForSelectorAsync("form[action='/account/login'] input[name=UserName]");

        // 只等"地址变了"：被挑战过来的登录页本身就满足"不是裸 /login"，
        // 按 URL 形状等会立刻返回，断言就读到还没跳转的旧地址。
        var from = Page.Url;
        await Page.EvaluateAsync(
            @"([userName, password]) => {
                  const form = document.querySelector('form[action=""/account/login""]');
                  form.querySelector('input[name=UserName]').value = userName;
                  form.querySelector('input[name=Password]').value = password;
                  form.requestSubmit(form.querySelector('button.login-btn'));
              }",
            new[] { userName, password });

        await Page.WaitForURLAsync(url => url != from);
    }

    /// <summary>
    /// 提交顶栏的退出表单。退出是 POST，GET /account/logout 已不再受理，
    /// 所以不能再像以前那样直接 Goto 那个地址。
    /// </summary>
    protected async Task SubmitLogoutFormAsync()
    {
        await Page.WaitForSelectorAsync("form[action='/account/logout'] button[type=submit]");
        var from = Page.Url;
        await Page.EvaluateAsync(
            @"() => {
                  const form = document.querySelector('form[action=""/account/logout""]');
                  form.requestSubmit(form.querySelector('button[type=submit]'));
              }");
        await Page.WaitForURLAsync(url => url != from);
    }

    /// <summary>浏览器直接跟随服务端跳转，Page.Url 就是最终落点。</summary>
    protected string CurrentPath() => new Uri(Page.Url).PathAndQuery;
}
