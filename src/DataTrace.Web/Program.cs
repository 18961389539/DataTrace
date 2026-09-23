using System.Globalization;
using DataTrace.Collector;
using DataTrace.Infrastructure;
using DataTrace.Infrastructure.Identity;
using DataTrace.Infrastructure.Seeding;
using DataTrace.Plc;
using DataTrace.Plc.Drivers.IoTClient;
using DataTrace.Web.Components;
using DataTrace.Web.Services;
using Microsoft.AspNetCore.Identity;
using MudBlazor;
using MudBlazor.Services;
using Serilog;

// 内容根目录固定为程序所在目录：Windows 服务（sc create）启动时工作目录是 System32，
// 按工作目录找 appsettings.json 会一无所获 —— 端口、日志级别、DataRoot 全部静默回落到默认值，
// 现象只是"服务起不来"或"监听在 5000"，看不出原因。
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

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
    // 日志跟着 DataRoot 走：数据盘和系统盘分开时，滚动日志写满安装目录会把服务拖死，
    // 而装在 Program Files 下的实例通常对安装目录根本没有写权限。
    var logDir = Path.Combine(dataRoot, "logs");
    Directory.CreateDirectory(logDir);
    log.ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console()
        .WriteTo.File(Path.Combine(logDir, "datatrace-.log"), rollingInterval: RollingInterval.Day);
});

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

using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
    await seeder.SeedAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAuthentication();

// 开发环境免登录直接进 admin，省掉每次改界面都要手工登录；生产必须走 /login，
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

    // 已登录就没有登录页可看了；未登录时留给 Cookie 认证按 LoginPath 挑战。
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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .DisableAntiforgery();

app.Run();
