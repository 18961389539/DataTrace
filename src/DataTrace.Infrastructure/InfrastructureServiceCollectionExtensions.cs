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
using DataTrace.Infrastructure.Storage;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddDataTraceInfrastructure(this IServiceCollection services, string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        services.AddSingleton(new DataRootPaths(dataRoot));
        services.AddSingleton<IDatabaseBackupService, DatabaseBackupService>();
        services.AddHostedService<DatabaseBackupHostedService>();

        var configPath = Path.Combine(dataRoot, "config.db");
        var runtimePath = Path.Combine(dataRoot, "runtime");
        var curvePath = Path.Combine(dataRoot, "curves");
        var spoolPath = Path.Combine(dataRoot, "spool");

        services.AddDbContext<ConfigDbContext>(options =>
            options.UseSqlite($"Data Source={configPath}"));

        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
            {
                // 显式策略：长度≥8、要大写、要数字；不要强制特殊字符。
                // RequireLowercase / RequiredUniqueChars 保持 Identity 默认（true / 1），
                // Web 端 PasswordPolicy 通过 IOptions<IdentityOptions> 读取合并后的完整规则。
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = true;
                options.Password.RequireDigit = true;
                options.Password.RequiredLength = 8;
                options.User.RequireUniqueEmail = false;
            })
            .AddEntityFrameworkStores<ConfigDbContext>()
            .AddDefaultTokenProviders();

        services.AddSingleton(new RuntimeDbFactory(runtimePath));
        services.AddSingleton<ICurveFileStore>(_ => new CurveFileStore(curvePath));
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
