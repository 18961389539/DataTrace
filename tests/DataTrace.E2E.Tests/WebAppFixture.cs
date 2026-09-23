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
/// 所以端口走配置项覆盖、数据目录指向临时目录、并且必须是 Development
/// （Production 下 app.css 与 _content/* 取不到，页面会看起来"坏了"）。
/// </remarks>
public sealed class WebAppFixture : IDisposable
{
    private Process? _process;
    private StringBuilder _output = new();
    private string? _dataRoot;
    private Task? _start;

    public string BaseUrl { get; private set; } = "";

    public int Port { get; private set; }

    public Task Started => _start ??= StartAsync();

    private async Task StartAsync()
    {
        var appDll = LocateAppDll();
        Port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{Port}";
        _dataRoot = Path.Combine(Path.GetTempPath(), "datatrace-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var startInfo = new ProcessStartInfo("dotnet", $"\"{appDll}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(appDll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
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
                // 首访会被自动登成 admin 并跳回首页，任一响应都说明管线通了。
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

/// <summary>E2E 用例基类：每个用例一个全新上下文，互不串 cookie。</summary>
[Collection("e2e")]
public abstract class E2ETestBase : IAsyncLifetime
{
    protected WebAppFixture App { get; }

    protected IBrowser Browser { get; }

    protected IBrowserContext Context { get; private set; } = null!;

    protected IPage Page { get; private set; } = null!;

    protected E2ETestBase(WebAppFixture app, BrowserFixture browser)
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

    /// <summary>以指定账号登录（应用对未登录请求会自动登成 admin，所以非 admin 角色必须显式登录）。</summary>
    protected async Task LoginAsync(string userName, string password)
    {
        await Page.GotoAsync($"{App.BaseUrl}/login");
        await Page.FillAsync("input[name=UserName]", userName);
        await Page.FillAsync("input[name=Password]", password);
        await Page.ClickAsync("button.login-btn");
        await Page.WaitForURLAsync("**/", new PageWaitForURLOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
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

    /// <summary>在查询表单里填条件并回车提交——页面自己就提示"在输入框内按回车即查询"。</summary>
    protected async Task SearchAsync(ILocator input, string value)
    {
        await input.FillAsync(value);
        await input.PressAsync("Enter");
    }
}
