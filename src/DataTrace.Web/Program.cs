using System.Globalization;
using DataTrace.Application.Configuration;
using DataTrace.Collector;
using DataTrace.Infrastructure;
using DataTrace.Infrastructure.Identity;
using DataTrace.Infrastructure.Seeding;
using DataTrace.Plc;
using DataTrace.Plc.Drivers.IoTClient;
using DataTrace.Web.Components;
using DataTrace.Web.Options;
using DataTrace.Web.Services;
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

builder.Host.UseWindowsService();
builder.Host.UseSerilog((ctx, log) =>
{
    // 日志跟着 DataRoot 走：数据盘和系统盘分开时，滚动日志写满安装目录会把服务拖死；
    // 而装在 Program Files 下的实例通常对安装目录根本没有写权限。
    var logDir = Path.Combine(dataRoot, "logs");
    Directory.CreateDirectory(logDir);
    log.ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console()
        .WriteTo.File(Path.Combine(logDir, "datatrace-.log"), rollingInterval: RollingInterval.Day);
});

builder.Services.AddSingleton<IConfigureOptions<CustomerOptions>, CustomerOptionsSetup>();
builder.Services.AddOptions<CustomerOptions>();
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection(BackupOptions.SectionName));
builder.Services.AddSingleton<CustomerBrandingStore>();

builder.Services.AddDataTraceInfrastructure(dataRoot);
builder.Services.AddDataTracePlc();
builder.Services.AddIoTClientDrivers();
builder.Services.AddDataTraceCollector();
builder.Services.AddHttpClient("mes");
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
builder.Services.AddSingleton<PasswordPolicy>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    // 不能也指向 /login：已登录的人访问 /login 会被跳回首页，
    // 于是角色越权变成"无声地被扔回看板"，ReturnUrl 也一起丢掉。
    options.AccessDeniedPath = "/denied";
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.SlidingExpiration = true;
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Config", p => p.RequireRole("Administrator", "Engineer"));
    options.AddPolicy("Admin", p => p.RequireRole("Administrator"));
});

var app = builder.Build();

var branding = app.Services.GetRequiredService<CustomerBrandingStore>();
branding.ResolvedDataRoot = dataRoot;
branding.BindUrl = builder.Configuration["Kestrel:Endpoints:Http:Url"]
    ?? Environment.GetEnvironmentVariable("Kestrel__Endpoints__Http__Url");

using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
    var customer = scope.ServiceProvider.GetRequiredService<IOptions<CustomerOptions>>().Value;
    var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
    // Development 默认开仿真；Production 默认关。customer.json / Customer:SimulatorAutoRun 可显式覆盖。
    var simulatorAutoRunSeed = customer.SimulatorAutoRun
        ?? (env.IsDevelopment() ? true : false);
    await seeder.SeedAsync(simulatorAutoRunSeed);

    // Production：配置/环境为准，启动后强制把库内 SimulatorAutoRun 写成 false（幂等）。
    // Development/演示不动，避免把本地演示库关掉。
    if (env.IsProduction())
    {
        var config = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var snap = await config.GetSnapshotAsync();
        if (snap.Settings.SimulatorAutoRun)
        {
            snap.Settings.SimulatorAutoRun = false;
            await config.SaveSettingsAsync(snap.Settings);
        }
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
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

    await next();
});
app.UseAuthorization();
app.UseAntiforgery();
app.MapPost("/account/login", async (HttpContext http, SignInManager<ApplicationUser> signIn) =>
{
    var form = await http.Request.ReadFormAsync();
    var userName = form["UserName"].ToString();
    var password = form["Password"].ToString();
    var returnUrl = form[ReturnUrl.QueryKey].ToString();
    var result = await signIn.PasswordSignInAsync(userName, password, isPersistent: true, lockoutOnFailure: false);
    return result.Succeeded
        ? Results.Redirect(ReturnUrl.AfterSignIn(returnUrl))
        : Results.Redirect(ReturnUrl.AfterSignInFailed(returnUrl));
}).AllowAnonymous().DisableAntiforgery();
app.MapGet("/account/logout", async (SignInManager<ApplicationUser> signIn) =>
{
    await signIn.SignOutAsync();
    return Results.Redirect("/");
}).AllowAnonymous();
// Soft-404：未知路径由 Pages/NotFound.razor 的 @page "/{*path:nonfile}" 接住并渲染友好页，
// 避免真 404 返回空白；客户端导航仍走 Routes.razor 的 <NotFound>。
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .DisableAntiforgery();

app.Run();
