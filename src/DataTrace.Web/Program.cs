using System.Globalization;
using DataTrace.Application.Configuration;
using DataTrace.Application.Identity;
using DataTrace.Collector;
using DataTrace.Infrastructure;
using DataTrace.Infrastructure.Identity;
using DataTrace.Infrastructure.Seeding;
using DataTrace.Plc;
using DataTrace.Plc.Drivers.IoTClient;
using DataTrace.Web.Components;
using DataTrace.Web.Options;
using DataTrace.Web.Services;
using DataTrace.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using MudBlazor;
using MudBlazor.Services;
using Serilog;

// 内容根目录固定为程序所在目录：Windows 服务（sc create）启动时工作目录是 System32，
// 按工作目录找 appsettings.json 会一无所获——端口、日志级别、DataRoot 全部静默回落到默认值，
// 现象只是"服务起不来 / 监听在 5000"，看不出原因。
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// ContentRoot 指向 BaseDirectory 后，默认只在 Development + 项目目录下启用的
// Static Web Assets 映射不会生效；显式启用才能从 runtime 清单提供 wwwroot 与
// MudBlazor 的 _content 资源（dotnet run / 非 publish 的 bin 下没有物理 wwwroot）。
builder.WebHost.UseStaticWebAssets();

// 安装目录 customer.json：扁平字段（CustomerId/SiteName/Port/DataRoot/SimulatorAutoRun）覆盖品牌与演示开关。
builder.Configuration.AddJsonFile("customer.json", optional: true, reloadOnChange: true);

// 界面全部为中文产线场景：统一区域文化，日期选择器与数字格式跟随中文习惯。
var culture = new CultureInfo("zh-CN");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

var dataRoot = builder.Configuration["DataRoot"];
if (string.IsNullOrWhiteSpace(dataRoot))
{
    dataRoot = Path.Combine(AppContext.BaseDirectory, "data");
}

if (!Path.IsPathRooted(dataRoot))
{
    dataRoot = Path.Combine(AppContext.BaseDirectory, dataRoot);
}

// 控制台与文件共用一份模板：两边长相不同时，照着控制台的说法去文件里搜会搜不到。
const string LogOutputTemplate =
    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}]{Properties} {Message:lj}{NewLine}{Exception}";

builder.Host.UseWindowsService();
builder.Host.UseSerilog((ctx, log) =>
{
    // 日志跟着 DataRoot 走：数据盘和系统盘分开时，滚动日志写满安装目录会把服务拖死；
    // 而装在 Program Files 下的实例通常对安装目录根本没有写权限。
    var logDir = Path.Combine(dataRoot, "logs");
    Directory.CreateDirectory(logDir);

    // 体积闸门：以前只按天滚动，没有任何上限。PLC 断链重连风暴、数据库锁重试或一次异常刷屏
    // 能在一个班上写出 GB 级日志，把数据盘写满 —— 数据盘一满 SQLite 就开始落库失败，
    // 现场看到的是"采集莫名停了"，而日志还在继续把剩下的空间吃掉。
    // 单文件上限 × 封顶份数把日志总量钉死在两者相乘以内（默认 32 MB × 30 ≈ 960 MB）。
    // 三个参数必须一起给：只给 fileSizeLimitBytes 而不开 rollOnFileSizeLimit，文件涨到上限就不再写；
    // 只给 retainedFileCountLimit 则只按天清理，管不住单日暴涨。
    //
    // {Properties} 不能省：Serilog 的默认模板不渲染作用域属性，而采集链路就是靠
    // "一次采集一个作用域"把读取、判定、落库、补传这几段的日志串起来的 ——
    // 模板里不带它，作用域写进去了也一个字都看不见。
    // 级别（含 Microsoft.EntityFrameworkCore 的压制）来自 appsettings 的 Serilog 段，
    // ReadFrom.Configuration 只读那一段；原来的 Logging:LogLevel 从来没被读过。
    log.ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console(outputTemplate: LogOutputTemplate)
        .WriteTo.File(
            Path.Combine(logDir, "datatrace-.log"),
            rollingInterval: RollingInterval.Day,
            fileSizeLimitBytes: ReadLogFileLimitBytes(ctx.Configuration),
            rollOnFileSizeLimit: true,
            retainedFileCountLimit: ReadLogRetainedFiles(ctx.Configuration),
            outputTemplate: LogOutputTemplate);
});

// 配置读坏（写错单位、填 0 或负数）时不能退化成"没有上限"—— 那正好是这次要堵的洞。
// 取不到或不合法一律回默认值。
static long ReadLogFileLimitBytes(IConfiguration configuration)
{
    const int defaultMegabytes = 32;
    var megabytes = configuration.GetValue<int?>("Logging:File:FileSizeLimitMb") ?? defaultMegabytes;
    return (megabytes > 0 ? megabytes : defaultMegabytes) * 1024L * 1024L;
}

static int ReadLogRetainedFiles(IConfiguration configuration)
{
    const int defaultCount = 30;
    var count = configuration.GetValue<int?>("Logging:File:RetainedFileCount") ?? defaultCount;
    return count > 0 ? count : defaultCount;
}

builder.Services.AddSingleton<IConfigureOptions<CustomerOptions>, CustomerOptionsSetup>();
builder.Services.AddOptions<CustomerOptions>();
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection(BackupOptions.SectionName));
builder.Services.AddSingleton<CustomerBrandingStore>();

builder.Services.AddDataTraceInfrastructure(dataRoot);
builder.Services.AddDataTracePlc();
builder.Services.AddIoTClientDrivers();
builder.Services.AddDataTraceCollector();
builder.Services.AddHttpClient("mes");
builder.Services.AddHttpClient("alarm", client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton<DataTrace.Web.Services.LineAlarmBoard>();
builder.Services.AddSingleton<DataTrace.Application.Alarms.ILineAlarmBoard>(sp => sp.GetRequiredService<DataTrace.Web.Services.LineAlarmBoard>());
builder.Services.AddHostedService<DataTrace.Web.Services.LineAlarmHostedService>();
builder.Services.AddMudServices(config =>
{
    // 提示统一出现在底部居中，操作反馈更醒目；时长与去重由 DtToast 按严重度分档控制。
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomCenter;
    config.SnackbarConfiguration.VisibleStateDuration = 5000;
    config.SnackbarConfiguration.ShowTransitionDuration = 150;
    config.SnackbarConfiguration.HideTransitionDuration = 150;
    config.SnackbarConfiguration.MaxDisplayedSnackbars = 3;
});
builder.Services.AddScoped<DataTrace.Web.Services.DtToast>();
builder.Services.AddSingleton<IJsonFileDialog, WindowsJsonFileDialog>();
// /healthz 的判据（配置库、数据盘可写、采集器心跳）。只依赖单例服务。
builder.Services.AddSingleton<HealthProbe>();
// 诊断页按接口取探活结果（测试里要能换成假报告）；/healthz 端点继续用具体类型。
builder.Services.AddSingleton<IHealthProbe>(sp => sp.GetRequiredService<HealthProbe>());
builder.Services.AddSingleton<PasswordPolicy>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
// 传输安全：本次是否跑在 TLS 上，由有没有配 HTTPS 端点决定。
// 端点与证书走 Kestrel 的标准配置（Kestrel:Endpoints:Https + Certificate:*），
// 应用侧只需要知道这件事，用来判断 Cookie 要不要钉 Secure、要不要下 HSTS。
var httpsEndpoint = builder.Configuration["Kestrel:Endpoints:Https:Url"]
    ?? Environment.GetEnvironmentVariable("Kestrel__Endpoints__Https__Url");
var usesHttps = !string.IsNullOrWhiteSpace(httpsEndpoint);

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    // 不能也指向 /login：已登录的人访问 /login 会被跳回首页，
    // 于是角色越权变成"无声地被扔回看板"，ReturnUrl 也一起丢掉。
    options.AccessDeniedPath = "/denied";
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.SlidingExpiration = true;
    // 有 HTTPS 就把会话 Cookie 钉死在 Secure 上。默认的 SameAsRequest 会在一次 http
    // 访问时把带凭据的 Cookie 明文发出去 —— 车间里存了一条 http 书签就够了。
    options.Cookie.SecurePolicy = usesHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Config", p => p.RequireRole("Administrator", "Engineer"));
    options.AddPolicy("Admin", p => p.RequireRole("Administrator"));
});

var app = builder.Build();

// 同一数据目录只允许一个实例：采集、补传、备份都按独占 DataRoot 设计，双开会让同一件
// 被两个进程各采一次（各拿一个流水号、入库两份）、补传队列互相抢，两边日志还各记各的。
// 拿不到锁就明确报错退出，而不是让一个"半功能"的实例继续跑 —— 它造成的重复数据
// 事后只能靠人工从库里一条条分辨。
using var instanceGuard = SingleInstanceGuard.TryAcquire(dataRoot);
if (instanceGuard is null)
{
    var holder = SingleInstanceGuard.ReadHolderPid(dataRoot);
    app.Logger.LogCritical(
        "已有 DataTrace 实例正在使用数据目录 {DataRoot}（进程号 {HolderPid}），本进程退出。"
        + "请先停掉它（服务：sc stop DataTrace；控制台：deploy\\stop.ps1）后重试。",
        dataRoot,
        holder is { } pid ? pid.ToString() : "未知");
    Environment.ExitCode = 1;
    return;
}

var branding = app.Services.GetRequiredService<CustomerBrandingStore>();
branding.ResolvedDataRoot = dataRoot;
branding.BindUrl = builder.Configuration["Kestrel:Endpoints:Http:Url"]
    ?? Environment.GetEnvironmentVariable("Kestrel__Endpoints__Http__Url");

using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
    var customer = scope.ServiceProvider.GetRequiredService<IOptions<CustomerOptions>>().Value;
    var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
    // 建库时的默认值：Development 默认开仿真、Production 默认关，customer.json / Customer:SimulatorAutoRun
    // 可显式覆盖。它只在新建库时写入一次 —— 之后现场可以在「PLC 仿真」页随时开或关，
    // 启动流程不再把它改回去：以前每次启动都强制写 false，界面上的开关实际上是个摆设。
    var simulatorAutoRunSeed = customer.SimulatorAutoRun
        ?? (env.IsDevelopment() ? true : false);

    // 种子账号：生产**一律**不种演示账号，而且没有开关能把它打开 —— 演示口令写在源码里，
    // 种进现场就等于给每台机器配了一把公开钥匙。
    // 其它环境（Development / Staging）默认跟 Development 走，也可用 Seed:DemoUsers 显式打开：
    // E2E 的鉴权用例刻意跑在 Staging（要关掉开发态免登录才能触发真的登录挑战），
    // 那些用例需要 admin/Admin@123 存在，所以由它自己显式声明要演示账号。
    // 需要指定引导口令时用 Seed:AdminPassword（环境变量 Seed__AdminPassword）。
    var demoUsers = !env.IsProduction()
        && (builder.Configuration.GetValue<bool?>("Seed:DemoUsers") ?? env.IsDevelopment());
    var seedUsers = new SeedUserOptions
    {
        DemoUsers = demoUsers,
        AdminPassword = demoUsers ? null : builder.Configuration["Seed:AdminPassword"]
    };

    await seeder.SeedAsync(seedUsers, simulatorAutoRunSeed);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// 回报码必须挂在异常处理**之后**（也就是更内层）：它先拿到异常、配上码、记日志，再把异常抛回去，
// 由上面的 UseExceptionHandler 渲染 /Error 页 —— 两者共用同一个 HttpContext，所以码能传过去。
// 挂反了就永远拿不到异常（外层先接住），只剩一块没有码的错误页。
// 开发态这里没有 UseExceptionHandler，于是只记日志、异常照旧弹开发者页，本来也不该给现场发码。
app.UseExceptionTrace();

// 只有真跑在 TLS 上才下 HSTS：纯 HTTP 部署里下它，浏览器会把后续访问硬升级成 https，
// 现场看到的现象是"网站突然打不开了"。
if (usesHttps && !app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseStaticFiles();

// 客户 Logo：安装目录 branding\，URL 前缀 /branding/
var brandingDir = Path.Combine(AppContext.BaseDirectory, "branding");
Directory.CreateDirectory(brandingDir);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(brandingDir),
    RequestPath = "/branding"
});

app.UseAuthentication();

// 开发环境免登录直接进 admin，省掉每次改界面都要手工登录；生产必须走 /login。
// 否则「退出」会被下一个请求重新登成 admin，角色差异化界面也永远无法验收。
var autoSignInAdminInDevelopment = app.Environment.IsDevelopment();

app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var isAuthEndpoint = path.StartsWithSegments("/account/login") || path.StartsWithSegments("/account/logout");

    if (!isAuthEndpoint && autoSignInAdminInDevelopment && context.User.Identity?.IsAuthenticated != true)
    {
        var users = context.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var signIn = context.RequestServices.GetRequiredService<SignInManager<ApplicationUser>>();
        var admin = await users.FindByNameAsync("admin");
        if (admin is not null)
        {
            await signIn.SignInAsync(admin, isPersistent: true);
            context.User = await signIn.CreateUserPrincipalAsync(admin);
        }
    }

    // 已登录就没有登录页可看了；未登录时留给 Cookie 认证的 LoginPath 挑战。
    if (!isAuthEndpoint && context.User.Identity?.IsAuthenticated == true
        && path.Equals("/login", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Redirect(ReturnUrl.AfterSignIn(context.Request.Query[ReturnUrl.QueryKey].ToString()));
        return;
    }

    // 首登必须改密：标记随登录主体走（ApplicationUserClaimsPrincipalFactory），这里只识别与引导，
    // 不查库。初始口令没换掉之前只放行改密页与页面渲染必需的静态资源。
    if (!isAuthEndpoint
        && context.User.Identity?.IsAuthenticated == true
        && context.User.HasClaim(ApplicationUserClaimsPrincipalFactory.MustChangePasswordClaim, "1")
        && !IsPasswordChangeExempt(path))
    {
        context.Response.Redirect("/change-password");
        return;
    }

    await next();
});

// 强制改密期间仍须可达的路径：改密页自己、改密/登录/登出端点、探活、错误页，
// 以及**所有带扩展名的静态资源** —— 页面渲染要用它们。
// 静态资源不按目录白名单逐个列：app.css / favicon / manifest 这些就挂在根下，
// 列漏一个的症状是"改密页没有样式"，而且只在被强制改密时才出现，很难第一时间想到。
static bool IsPasswordChangeExempt(PathString path)
    => path.StartsWithSegments("/account")
       || path.StartsWithSegments("/change-password")
       || path.StartsWithSegments("/healthz")
       || path.StartsWithSegments("/error")
       || path.StartsWithSegments("/_framework")
       || path.StartsWithSegments("/_content")
       || path.StartsWithSegments("/_blazor")
       || path.StartsWithSegments("/branding")
       || Path.HasExtension(path.Value);
app.UseAuthorization();
app.UseAntiforgery();

// 不带凭据的表单 POST（登录）没法靠 SameSite 拦跨站提交，也用不上防伪令牌：
// 登录页走交互式渲染，AntiforgeryToken 只在静态 SSR 才拿得到 HttpContext 输出令牌。
// 于是用浏览器一定会带、页面脚本改不了的 Origin / Referer 判来源（详见 CrossSiteRequestGuard）。
static bool SameSitePost(HttpContext http) => CrossSiteRequestGuard.IsSameSite(
    http.Request.Headers.Origin,
    http.Request.Headers.Referer,
    http.Request.Host.Host ?? "",
    http.Request.Host.Port ?? (http.Request.Scheme == "https" ? 443 : 80));

app.MapPost("/account/login", async (
    HttpContext http,
    SignInManager<ApplicationUser> signIn,
    UserManager<ApplicationUser> users,
    IAuditLogger audit,
    ILogger<Program> logger) =>
{
    var form = await http.Request.ReadFormAsync();
    var userName = form["UserName"].ToString();
    var password = form["Password"].ToString();
    var returnUrl = form[ReturnUrl.QueryKey].ToString();
    // "记住我"：勾选框只在勾上时随表单提交（value="true"），没勾到就是空串。
    // 这里定的是"关掉浏览器还要不要认这个会话"——勾上才发带过期时间的持久 Cookie。
    var rememberMe = form["RememberMe"].ToString() is "true" or "on";

    if (!SameSitePost(http))
    {
        logger.LogWarning("登录请求来源与本站不一致，已拒绝：Origin={Origin} Referer={Referer}",
            http.Request.Headers.Origin.ToString(), http.Request.Headers.Referer.ToString());
        return Results.Redirect(ReturnUrl.AfterSignInFailed(returnUrl, ReturnUrl.CrossSiteError));
    }

    // lockoutOnFailure: true 才会累计失败次数（阈值与时长见 AddDataTraceInfrastructure 的 Lockout 配置）。
    // 传 false 等于把 Identity 的锁定关掉：口令可以被无限次猜，且审计里只留成功登录。
    // isPersistent 跟着"记住我"走：不勾就是会话 Cookie，浏览器一关就断，公共终端不会被下一个人接着用。
    var result = await signIn.PasswordSignInAsync(userName, password, isPersistent: rememberMe, lockoutOnFailure: true);
    if (result.Succeeded)
    {
        try
        {
            await audit.WriteAsync(
                userName, "Login", "User", userName, null, "登录成功",
                outcome: "Success",
                source: "Login endpoint",
                sourceIp: http.Connection.RemoteIpAddress?.ToString(),
                correlationId: http.TraceIdentifier);
        }
        catch (Exception ex)
        {
            // 登录是重定向流程，弹不出提示；但也不能静默，日志里必须留痕。
            logger.LogWarning(ex, "登录成功但审计写入失败：{UserName}", userName);
        }

        // 初始口令还没换掉就先把人送去改密页 —— 别让他拿着一次性口令去操作。
        // 改完由改密页把人送回原目标。
        var signedIn = await users.FindByNameAsync(userName);
        if (signedIn?.MustChangePassword == true)
        {
            var target = Uri.EscapeDataString(ReturnUrl.AfterSignIn(returnUrl));
            return Results.Redirect($"/change-password?{ReturnUrl.QueryKey}={target}");
        }

        return Results.Redirect(ReturnUrl.AfterSignIn(returnUrl));
    }

    var error = result.IsLockedOut ? ReturnUrl.LockedError : ReturnUrl.BadCredentialsError;
    if (!string.IsNullOrWhiteSpace(userName))
    {
        try
        {
            await audit.WriteAsync(
                userName, "LoginFailed", "User", userName, null, $"reason={error}",
                outcome: "Failure",
                source: "Login endpoint",
                sourceIp: http.Connection.RemoteIpAddress?.ToString(),
                correlationId: http.TraceIdentifier);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "登录失败审计写入失败：{UserName}", userName);
        }
    }

    return Results.Redirect(ReturnUrl.AfterSignInFailed(returnUrl, error, rememberMe));
}).AllowAnonymous().DisableAntiforgery();

// 登出必须是 POST：GET 带副作用时，一张 <img src="/account/logout"> 或一次顶层导航
// 就能把在场操作员踢下线（SameSite=Lax 只挡跨站 POST 与子资源请求，挡不住顶层 GET 导航）。
app.MapPost("/account/logout", async (
    HttpContext http,
    SignInManager<ApplicationUser> signIn,
    IAuditLogger audit,
    ILogger<Program> logger) =>
{
    if (!SameSitePost(http))
    {
        logger.LogWarning("登出请求来源与本站不一致，已拒绝：Origin={Origin} Referer={Referer}",
            http.Request.Headers.Origin.ToString(), http.Request.Headers.Referer.ToString());
        return Results.Redirect("/");
    }

    var userName = http.User.Identity?.Name ?? "";
    await signIn.SignOutAsync();
    if (!string.IsNullOrWhiteSpace(userName))
    {
        try
        {
            await audit.WriteAsync(
                userName, "Logout", "User", userName, null, null,
                source: "Logout endpoint",
                sourceIp: http.Connection.RemoteIpAddress?.ToString(),
                correlationId: http.TraceIdentifier);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "退出审计写入失败：{UserName}", userName);
        }
    }

    return Results.Redirect("/");
}).AllowAnonymous();

// 强制改密走表单 POST，而不是 Blazor 组件里改：改完必须重发认证 Cookie，
// 而交互式渲染阶段拿不到 HttpContext，SignInManager 在里面写不了 Cookie —— 会变成
// "密码改了、主体里的标记还在"，下一页又被拦回本页。和登录端点同一套取舍，来源同样用 Origin/Referer 判。
app.MapPost("/account/change-password", async (
    HttpContext http,
    SignInManager<ApplicationUser> signIn,
    UserManager<ApplicationUser> users,
    IUserAdministration administration,
    ILogger<Program> logger) =>
{
    var form = await http.Request.ReadFormAsync();
    var returnUrl = ReturnUrl.AfterSignIn(form[ReturnUrl.QueryKey].ToString());

    if (!SameSitePost(http))
    {
        logger.LogWarning("改密请求来源与本站不一致，已拒绝：Origin={Origin} Referer={Referer}",
            http.Request.Headers.Origin.ToString(), http.Request.Headers.Referer.ToString());
        return Results.Redirect(ChangePasswordError(ReturnUrl.CrossSiteError, returnUrl));
    }

    var userName = http.User.Identity?.Name;
    if (string.IsNullOrEmpty(userName))
    {
        return Results.Redirect("/login");
    }

    var current = form["CurrentPassword"].ToString();
    var next = form["NewPassword"].ToString();
    var confirm = form["ConfirmPassword"].ToString();

    // 先在端点里做能一次说清的本地校验，省得把"两次不一致"这类问题绕一圈 Identity 再翻成中文。
    string? localError = null;
    if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(next) || string.IsNullOrEmpty(confirm))
    {
        localError = "missing";
    }
    else if (!string.Equals(next, confirm, StringComparison.Ordinal))
    {
        localError = "mismatch";
    }
    else if (string.Equals(current, next, StringComparison.Ordinal))
    {
        localError = "same";
    }

    if (localError is not null)
    {
        return Results.Redirect(ChangePasswordError(localError, returnUrl));
    }

    var result = await administration.ChangePasswordAsync(userName, current, next, userName);
    if (result.Status != UserAdminStatus.Success)
    {
        return Results.Redirect(ChangePasswordError(result.Code ?? "unexpected", returnUrl));
    }

    // 改完重发 Cookie：主体里的"必须改密"标记随新 Cookie 消失，否则下一页又被拦回来。
    var changed = await users.FindByNameAsync(userName);
    if (changed is not null)
    {
        await signIn.RefreshSignInAsync(changed);
    }

    return Results.Redirect(returnUrl);
}).RequireAuthorization().DisableAntiforgery();

// 错误码随重定向回改密页；页面把码翻成中文，避免把服务端文案塞进 URL。
static string ChangePasswordError(string code, string returnUrl)
    => $"/change-password?error={Uri.EscapeDataString(code)}&{ReturnUrl.QueryKey}={Uri.EscapeDataString(returnUrl)}";

// 探活：给 Windows 服务守护与客户监控用。匿名可访问，因此报告里只回结论与错误摘要，不回安装路径。
app.MapGet("/healthz", async (HealthProbe probe, CancellationToken cancellationToken) =>
{
    var report = await probe.CheckAsync(cancellationToken);
    return report.Status == "healthy"
        ? Results.Ok(report)
        : Results.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

// 审计导出：临时文件由日志页面在导出时写好（见 Logs.razor），这里只负责把它发出去。
// 用 DeleteOnClose 让文件在响应写完、句柄关闭时自己消失 —— 直接删会打断还在传输的响应，
// 不管又会在临时目录里越积越多（进程崩溃留下的那批正属于后者，这里顺手清掉）。
app.MapGet("/audit-export/{id:guid}", (Guid id) =>
{
    var path = Path.Combine(Path.GetTempPath(), $"datatrace_audit_{id:N}.csv");
    if (!File.Exists(path))
    {
        return Results.NotFound();
    }

    SweepStaleAuditExports(Path.GetTempPath());

    var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read | FileShare.Delete,
        bufferSize: 4096,
        FileOptions.DeleteOnClose);
    return Results.Stream(stream, "text/csv; charset=utf-8", $"datatrace_audit_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
}).RequireAuthorization("Config");

// 导出即拿即用，留一天足够应付下载失败重试；再久的基本都是崩溃残留。
static void SweepStaleAuditExports(string tempDirectory)
{
    try
    {
        var cutoff = DateTime.UtcNow.AddDays(-1);
        foreach (var stale in Directory.EnumerateFiles(tempDirectory, "datatrace_audit_*.csv"))
        {
            if (File.GetLastWriteTimeUtc(stale) < cutoff)
            {
                File.Delete(stale);
            }
        }
    }
    catch
    {
        // 清扫失败不该让这次导出失败。
    }
}

// Soft-404：未知路径交给 Pages/NotFound.razor 的 @page "/{*path:nonfile}" 渲染友好页，
// 这里的组件映射同时兜住"真 404 返回空白"；客户端导航另走 Routes.razor 的 <NotFound>。
// DisableAntiforgery 只影响交互式服务端渲染：防伪由 SignalR 电路负责，
// 而带凭据的表单 POST（登录/登出）另有 CrossSiteRequestGuard 判来源。
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .DisableAntiforgery();

app.Run();
