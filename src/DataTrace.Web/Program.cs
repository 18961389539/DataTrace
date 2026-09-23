using System.Globalization;
using DataTrace.Collector;
using DataTrace.Infrastructure;
using DataTrace.Infrastructure.Identity;
using DataTrace.Infrastructure.Seeding;
using DataTrace.Plc;
using DataTrace.Plc.Drivers.IoTClient;
using DataTrace.Web.Components;
using Microsoft.AspNetCore.Identity;
using MudBlazor;
using MudBlazor.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// 界面全部为中文产线场景：统一区域文化，日期选择器与数字格式跟随中文习惯。
var culture = new CultureInfo("zh-CN");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

builder.Host.UseWindowsService();
builder.Host.UseSerilog((ctx, log) =>
{
    var logDir = Path.Combine(AppContext.BaseDirectory, "data", "logs");
    Directory.CreateDirectory(logDir);
    log.ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console()
        .WriteTo.File(Path.Combine(logDir, "datatrace-.log"), rollingInterval: RollingInterval.Day);
});

var dataRoot = builder.Configuration["DataRoot"];
if (string.IsNullOrWhiteSpace(dataRoot))
{
    dataRoot = Path.Combine(AppContext.BaseDirectory, "data");
}

if (!Path.IsPathRooted(dataRoot))
{
    dataRoot = Path.Combine(AppContext.BaseDirectory, dataRoot);
}

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
        context.Response.Redirect("/");
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
    var result = await signIn.PasswordSignInAsync(userName, password, isPersistent: true, lockoutOnFailure: false);
    return result.Succeeded
        ? Results.Redirect("/")
        : Results.Redirect("/login?error=1");
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
