using DataTrace.Application.Configuration;
using DataTrace.Application.Evaluation;
using DataTrace.Application.Mes;
using DataTrace.Application.Realtime;
using DataTrace.Application.Reporting;
using DataTrace.Application.Runtime;
using DataTrace.Infrastructure.Evaluation;
using DataTrace.Infrastructure.Identity;
using DataTrace.Infrastructure.Mes;
using DataTrace.Infrastructure.Persistence;
using DataTrace.Infrastructure.Realtime;
using DataTrace.Infrastructure.Reporting;
using DataTrace.Infrastructure.Seeding;
using DataTrace.Application.Backup;
using DataTrace.Infrastructure.Backup;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Validation;
using DataTrace.Infrastructure.Storage;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Identity 的账号策略：密码规则、用户名字符集、登录失败锁定。
    /// </summary>
    /// <remarks>
    /// 单独抽出来是给测试用的：用户管理页的用例要一套真 Identity，
    /// 策略若在测试里另写一份，就会在产品收紧规则之后继续"绿着"。
    /// 调用前必须先注册 <see cref="ConfigDbContext"/>。
    /// </remarks>
    public static IdentityBuilder AddDataTraceIdentity(this IServiceCollection services)
        => services.AddIdentity<ApplicationUser, IdentityRole>(options =>
            {
                // 显式策略：长度≥8、要大写、要数字；不要强制特殊字符。
                // RequireLowercase / RequiredUniqueChars 保持 Identity 默认（true / 1），
                // Web 端 PasswordPolicy 通过 IOptions<IdentityOptions> 读取合并后的完整规则。
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = true;
                options.Password.RequireDigit = true;
                options.Password.RequiredLength = 8;
                options.User.RequireUniqueEmail = false;
                // 用户名字符集与页面前置校验同源（UserNameRules）：两边各写一套时，
                // 页面放行了中文或 #，提交到 Identity 才以 InvalidUserName 失败。
                options.User.AllowedUserNameCharacters = UserNameRules.AllowedCharacters;
                // 连续失败锁定策略：阈值与时长放在 Domain 的 SystemDefaults —— 用户页的「账号锁定」详解
                // 会把这两个数字复述给现场，两处各写一份必然说岔。
                // 注意：Program 的登录端点必须传 lockoutOnFailure: true 才会计数，否则这里配了也不生效。
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = SystemDefaults.LockoutMaxFailedAttempts;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(SystemDefaults.LockoutMinutes);
            })
            .AddEntityFrameworkStores<ConfigDbContext>()
            .AddDefaultTokenProviders();

    public static IServiceCollection AddDataTraceInfrastructure(this IServiceCollection services, string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        services.AddSingleton(new DataRootPaths(dataRoot));
        services.AddSingleton<IDatabaseBackupService, DatabaseBackupService>();
        services.AddHostedService<DatabaseBackupHostedService>();

        var configPath = Path.Combine(dataRoot, "config.db");
        var runtimePath = Path.Combine(dataRoot, "runtime");
        var curvePath = Path.Combine(dataRoot, "curves");
        var archivePath = Path.Combine(dataRoot, "archive");
        var spoolPath = Path.Combine(dataRoot, "spool");

        // optionsLifetime 必须是 Singleton：下面的工厂也是单例，它注入的就是这份 options，
        // 让单例去解析一个 scoped 服务会在运行时直接报"不能从根容器解析 scoped 服务"。
        services.AddDbContext<ConfigDbContext>(
            options => options.UseSqlite($"Data Source={configPath}"),
            optionsLifetime: ServiceLifetime.Singleton);

        // 只读查询走工厂：每次查询一个自己的 context，读与读、读与写互不干扰。
        // scoped 的那个在 Blazor Server 里是整个电路共用的（EF 的 DbContext 不支持并发），
        // 读也挤在它上面时，页面上任何并行取数都会撞车，配置读被迫串行（见 ConfigRepository.ReadAsync）。
        services.AddDbContextFactory<ConfigDbContext>(options => options.UseSqlite($"Data Source={configPath}"));

        services.AddDataTraceIdentity();

        services.AddSingleton(new RuntimeDbFactory(runtimePath));
        services.AddSingleton<ICurveFileStore>(_ => new CurveFileStore(curvePath));
        services.AddSingleton<ICollectArchiveStore>(_ => new CollectArchiveFileStore(archivePath));
        services.AddSingleton<ISpoolStore>(_ => new FileSpoolStore(spoolPath));
        // 基线缓存必须是单例：后台服务写、采集流水线读，两边看到的必须是同一份。
        services.AddSingleton<ICurveBaselineCache, CurveBaselineCache>();

        services.AddScoped<IConfigRepository, ConfigRepository>();
        services.AddScoped<IAuditLogger, AuditLogger>();
        services.AddScoped<IActiveSessionStore, ActiveSessionStore>();
        services.AddScoped<ISerialNumberGenerator, SerialNumberGenerator>();
        services.AddScoped<IRuntimeStore, RuntimeStore>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<ISpcService, SpcService>();
        services.AddScoped<ICurveTemplateService, CurveTemplateService>();
        services.AddScoped<IMesPublisher, MesPublisher>();
        services.AddScoped<DatabaseSeeder>();

        services.AddSingleton<RuntimeStatusHub>();
        services.AddSingleton<IRuntimeStatusHub>(sp => sp.GetRequiredService<RuntimeStatusHub>());
        services.AddSingleton<ICollectEventBus>(sp => sp.GetRequiredService<RuntimeStatusHub>());

        return services;
    }
}
