using System.Security.Claims;
using DataTrace.Application.Backup;
using DataTrace.Application.Configuration;
using DataTrace.Collector;
using DataTrace.Domain.Constants;
using DataTrace.Plc.Simulator;
using DataTrace.Web.Options;
using DataTrace.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MudBlazor.Services;
using AngleSharp.Dom;

namespace DataTrace.Web.Tests;

/// <summary>给 bUnit 装配本应用页面所需的服务替身。</summary>
public abstract class WebTestBase : IDisposable
{
    protected TestContext Context { get; }

    protected FakeConfigRepository Config { get; }

    protected FakeLineSimulator Simulator { get; }

    protected ToastSpy Toast { get; }

    protected DialogSpy Dialogs { get; }

    protected FakeJsonFileDialog FileDialog { get; }

    /// <summary>popover 宿主只渲染一次：重复渲染会让同一批浮层挂到两个 provider 上。</summary>
    private bool _popoverHostRendered;

    /// <summary>审计写库替身：页面上的关键动作都会经过它。</summary>
    protected Mock<IAuditLogger> Audit { get; } = new();

    protected Mock<IDatabaseBackupService> Backup { get; } = new();

    /// <summary>品牌与部署身份。页面拿它显示厂名/版本/环境，测试里给一份空配置即可。</summary>
    protected CustomerBrandingStore Branding { get; }

    /// <summary>页面与断言共用同一个模拟 PLC 目录，便于回读寄存器。</summary>
    protected SimulatorCatalog Simulators { get; } = new();

    protected WebTestBase()
    {
        Context = new TestContext();
        Config = new FakeConfigRepository();
        Simulator = new FakeLineSimulator();
        Toast = new ToastSpy();
        Dialogs = new DialogSpy();
        FileDialog = new FakeJsonFileDialog();
        Branding = new CustomerBrandingStore(
            // 全限定：本文件同时引了 DataTrace.Web.Options 命名空间，裸写 Options 会解析成命名空间。
            Microsoft.Extensions.Options.Options.Create(new CustomerOptions()),
            new StubWebHostEnvironment(),
            NullLogger<CustomerBrandingStore>.Instance);

        Backup.Setup(b => b.GetStatus()).Returns(new BackupStatusSnapshot());
        Backup
            .Setup(b => b.RunBackupAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackupRunResult { Success = true });

        Context.JSInterop.Mode = JSRuntimeMode.Loose;
        Context.Services.AddMudServices();
        Context.Services.AddSingleton<IConfigRepository>(Config);
        Context.Services.AddSingleton<ILineSimulator>(Simulator);
        Context.Services.AddSingleton(Simulators);
        Context.Services.AddSingleton<ISnackbar>(Toast.Mock.Object);
        Context.Services.AddSingleton<IDialogService>(Dialogs.Mock.Object);
        Context.Services.AddSingleton<IJsonFileDialog>(FileDialog);
        Context.Services.AddSingleton(Branding);
        Context.Services.AddSingleton(Audit.Object);
        Context.Services.AddSingleton(Backup.Object);
        // 页面里有 @inject AuthenticationStateProvider 与 <AuthorizeView>；后者只认级联的
        // Task<AuthenticationState>，所以用 bUnit 的测试授权（它把服务与级联值一起备齐）。
        // 默认管理员；要按角色分档的用例自己再注册一个 AuthenticationStateProvider，后注册的生效。
        Context.AddTestAuthorization().SetAuthorized("admin").SetRoles(AppRoles.Administrator);
        // <AuthorizeView> 还要策略提供者与授权服务，否则它自己就构造不出来。
        Context.Services.AddAuthorizationCore();
        // DtToast 只做"按严重度分档 + 同文案去重"，这里用真身，顺带把它对 ISnackbar 的用法一起测了。
        Context.Services.AddSingleton<DtToast>();
    }

    /// <summary>只有环境名与内容根会被品牌存储读到，其余成员用不到。</summary>
    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "DataTrace.Web.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public string WebRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>只读 Name 与角色声明的身份，够页面判断角色用。</summary>
    private sealed class StubAuthenticationStateProvider(string role) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, "admin"),
                    new Claim(ClaimTypes.Role, role)
                ],
                authenticationType: "test");

            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }

    /// <summary>按角色重设身份，覆盖基类默认的管理员。
    /// 页面读的是注入的 AuthenticationStateProvider，后注册的同类型服务生效。</summary>
    protected void UseRole(string role)
        => Context.Services.AddSingleton<AuthenticationStateProvider>(new StubAuthenticationStateProvider(role));

    /// <summary>
    /// 渲染 popover 宿主。
    /// </summary>
    /// <remarks>
    /// MudTooltip / MudSelect / MudDatePicker 这些组件要把浮层挂进 MudPopoverProvider，
    /// 真实应用由 MainLayout 提供；bUnit 里没有布局，渲染带这些组件的页面之前必须自己补一个，
    /// 否则组件初始化就抛 "Missing &lt;MudPopoverProvider /&gt;"。
    /// 幂等，可以放心放在各测试类的渲染辅助方法里。
    /// </remarks>
    protected void RenderPopoverHost()
    {
        if (_popoverHostRendered)
        {
            return;
        }

        Context.RenderComponent<MudPopoverProvider>();
        _popoverHostRendered = true;
    }

    /// <summary>按可见文字点击按钮，避开 Material 类名随版本漂移的问题。</summary>
    protected void ClickButton(IRenderedFragment cut, string text)
    {
        var button = cut.FindAll("button").SingleOrDefault(b => b.TextContent.Contains(text));
        Assert.NotNull(button);
        button.Click();
    }

    /// <summary>向带 Label 的输入框打字。Immediate 的 MudInput 绑 oninput、其余绑 onchange，两种都照顾到。</summary>
    protected void TypeInto(IRenderedFragment cut, string label, string value)
    {
        var input = InputForLabel(cut, label);
        try
        {
            input.Input(value);
        }
        catch (MissingEventHandlerException)
        {
            input.Change(value);
        }
    }

    /// <summary>按 Label 文本定位输入框（MudInput 会把 label 的 for 指到 input 的 id）。</summary>
    protected IElement InputForLabel(IRenderedFragment cut, string label)
    {
        var found = cut.FindAll("label").SingleOrDefault(l => l.TextContent.Contains(label));
        Assert.NotNull(found);
        var id = found.GetAttribute("for");
        Assert.False(string.IsNullOrEmpty(id));
        return cut.Find($"#{id}");
    }

    /// <summary>
    /// 按 aria-label 定位输入框。设置页的说明文字是普通 div（不是 label[for]），
    /// 那些字段只有 aria-label 这一个可访问名，也只能按它找。
    /// </summary>
    protected IElement InputForAriaLabel(IRenderedFragment cut, string label)
    {
        var found = cut.FindAll($"input[aria-label=\"{label}\"]").FirstOrDefault();
        Assert.NotNull(found);
        return found;
    }

    /// <summary>向带 aria-label 的输入框打字；Immediate 的 MudInput 绑 oninput、其余绑 onchange。</summary>
    protected void TypeIntoAriaLabel(IRenderedFragment cut, string label, string value)
    {
        var input = InputForAriaLabel(cut, label);
        try
        {
            input.Input(value);
        }
        catch (MissingEventHandlerException)
        {
            input.Change(value);
        }
    }

    /// <summary>把第 n 个开关拨到目标值（MudSwitch 的 input 只监听 onchange，没有 onclick）。</summary>
    protected void ToggleSwitch(IRenderedFragment cut, bool value, int index = 0)
    {
        var switches = cut.FindAll("input[type=checkbox]");
        Assert.True(index < switches.Count, $"页面上只有 {switches.Count} 个开关，取不到第 {index} 个");
        switches[index].Change(value);
    }

    public void Dispose() => Context.Dispose();
}
