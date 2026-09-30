using System.Security.Cryptography;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Infrastructure.Identity;
using DataTrace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DataTrace.Infrastructure.Seeding;

// 按职责拆成 partial 多文件：账号与主流程本文件，补表补列见 .Schema.cs，
// 脏数据清理见 .Maintenance.cs，演示产线见 .DemoLine.cs。
public sealed partial class DatabaseSeeder
{
    private readonly ConfigDbContext _db;

    private readonly UserManager<ApplicationUser> _users;

    private readonly RoleManager<IdentityRole> _roles;

    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(
        ConfigDbContext db,
        UserManager<ApplicationUser> users,
        RoleManager<IdentityRole> roles,
        ILogger<DatabaseSeeder> logger)
    {
        _db = db;
        _users = users;
        _roles = roles;
        _logger = logger;
    }

    /// <summary>
/// 初始化配置库（建表、补列、角色与账号、演示产线）。
    /// </summary>
    /// <param name="users">
    /// 种子账号策略。默认种入口令写在源码里的演示账号，仅供开发/演示/测试；
    /// 生产必须显式传 <see cref="SeedUserOptions.Production"/>。
    /// </param>
    /// <param name="simulatorAutoRunSeed">新建库时写入的"启动即开仿真"初值。</param>
    public async Task SeedAsync(
        SeedUserOptions? users = null,
        bool simulatorAutoRunSeed = true,
        CancellationToken cancellationToken = default)
    {
        users ??= new SeedUserOptions();
        await _db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await EnsureAuditLogSchemaAsync(cancellationToken).ConfigureAwait(false);
        await EnsureAlarmSchemaAsync(cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "SystemSettings", "SimulatorAutoRun",
            "ALTER TABLE SystemSettings ADD COLUMN SimulatorAutoRun INTEGER NOT NULL DEFAULT 1", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "SystemSettings", "SimulatorIntervalMs",
            "ALTER TABLE SystemSettings ADD COLUMN SimulatorIntervalMs INTEGER NOT NULL DEFAULT 2500", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "SystemSettings", "SimulatorNgPercent",
            "ALTER TABLE SystemSettings ADD COLUMN SimulatorNgPercent INTEGER NOT NULL DEFAULT 8", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "SystemSettings", "SimulatorPalletPool",
            "ALTER TABLE SystemSettings ADD COLUMN SimulatorPalletPool INTEGER NOT NULL DEFAULT 20", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "SystemSettings", "AuditRetentionYears",
            "ALTER TABLE SystemSettings ADD COLUMN AuditRetentionYears INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "SystemSettings", "AlarmWebhookUrl",
            "ALTER TABLE SystemSettings ADD COLUMN AlarmWebhookUrl TEXT NULL", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "SystemSettings", "ShiftStartHour",
            "ALTER TABLE SystemSettings ADD COLUMN ShiftStartHour INTEGER NOT NULL DEFAULT 8", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "SystemSettings", "ShiftLengthHours",
            "ALTER TABLE SystemSettings ADD COLUMN ShiftLengthHours INTEGER NOT NULL DEFAULT 12", cancellationToken).ConfigureAwait(false);

        // 点位三级限值：老配置库缺这些列，采集侧读限值会直接报 no such column。
        await SqliteSchema.AddColumnIfMissingAsync(_db, "Tags", "WarningLowerLimit",
            "ALTER TABLE Tags ADD COLUMN WarningLowerLimit REAL NULL", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "Tags", "WarningUpperLimit",
            "ALTER TABLE Tags ADD COLUMN WarningUpperLimit REAL NULL", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "Tags", "TargetValue",
            "ALTER TABLE Tags ADD COLUMN TargetValue REAL NULL", cancellationToken).ConfigureAwait(false);

        // 当前生效的产品型号指针（手工切换）。
        await SqliteSchema.AddColumnIfMissingAsync(_db, "SystemSettings", "ActiveRecipeId",
            "ALTER TABLE SystemSettings ADD COLUMN ActiveRecipeId INTEGER NULL", cancellationToken).ConfigureAwait(false);

        await SqliteSchema.AddColumnIfMissingAsync(_db, "Recipes", "PreviousCodes",
            "ALTER TABLE Recipes ADD COLUMN PreviousCodes TEXT NULL", cancellationToken).ConfigureAwait(false);

        // 在制索引要记住首站型号和最后停在哪一站。老库没有这些列时，采集侧一读就报 no such column。
        await SqliteSchema.AddColumnIfMissingAsync(_db, "ActiveSessions", "RecipeCode",
            "ALTER TABLE ActiveSessions ADD COLUMN RecipeCode TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "ActiveSessions", "LastStationId",
            "ALTER TABLE ActiveSessions ADD COLUMN LastStationId INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "ActiveSessions", "LastStationCode",
            "ALTER TABLE ActiveSessions ADD COLUMN LastStationCode TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "ActiveSessions", "LastActivityAt",
            "ALTER TABLE ActiveSessions ADD COLUMN LastActivityAt TEXT NULL", cancellationToken).ConfigureAwait(false);

        // 点位取值来源（0 = PLC 寄存器）与文件源点位读的那一个文件路径。
        // 老配置库没有这两列，采集侧读配置会直接报 no such column。
        await SqliteSchema.AddColumnIfMissingAsync(_db, "Tags", "Source",
            "ALTER TABLE Tags ADD COLUMN Source INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "Stations", "DataFilePath",
            "ALTER TABLE Stations ADD COLUMN DataFilePath TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "Stations", "DataFileFormat",
            "ALTER TABLE Stations ADD COLUMN DataFileFormat INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        SqliteSchema.DropColumnIfPresent(_db, "Stations", "ScriptPath");

        // 判异规则开关（NULL = 全套规则）与按点位冻结的控制限。
        // 老配置库缺这些列时，报表读点位会直接报 no such column。
        await SqliteSchema.AddColumnIfMissingAsync(_db, "Tags", "SpcRuleMask",
            "ALTER TABLE Tags ADD COLUMN SpcRuleMask INTEGER NULL", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "Tags", "ControlCenterLine",
            "ALTER TABLE Tags ADD COLUMN ControlCenterLine REAL NULL", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "Tags", "ControlUpperLimit",
            "ALTER TABLE Tags ADD COLUMN ControlUpperLimit REAL NULL", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "Tags", "ControlLowerLimit",
            "ALTER TABLE Tags ADD COLUMN ControlLowerLimit REAL NULL", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "Tags", "ControlSampleCount",
            "ALTER TABLE Tags ADD COLUMN ControlSampleCount INTEGER NULL", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "Tags", "ControlCapturedAt",
            "ALTER TABLE Tags ADD COLUMN ControlCapturedAt TEXT NULL", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "Tags", "ControlCapturedBy",
            "ALTER TABLE Tags ADD COLUMN ControlCapturedBy TEXT NULL", cancellationToken).ConfigureAwait(false);

        // 强制改密标记。老配置库没有这列，登录管道读主体标记前 EF 会直接报 no such column。
        await SqliteSchema.AddColumnIfMissingAsync(_db, "AspNetUsers", "MustChangePassword",
            """ALTER TABLE "AspNetUsers" ADD COLUMN "MustChangePassword" INTEGER NOT NULL DEFAULT 0""", cancellationToken).ConfigureAwait(false);

        await RemoveTagCodesAsync(cancellationToken).ConfigureAwait(false);

        await CleanupOrphanRecipeLimitsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var role in AppRoles.All)
        {
            if (!await _roles.RoleExistsAsync(role).ConfigureAwait(false))
            {
                await _roles.CreateAsync(new IdentityRole(role)).ConfigureAwait(false);
            }
        }

        if (users.DemoUsers)
        {
            await EnsureUserAsync("admin", "管理员", "Admin@123", AppRoles.Administrator).ConfigureAwait(false);
            await EnsureUserAsync("engineer", "工程师", "Engineer@123", AppRoles.Engineer).ConfigureAwait(false);
            await EnsureUserAsync("operator", "操作员", "Operator@123", AppRoles.Operator).ConfigureAwait(false);
            await EnsureUserAsync("viewer", "访客", "Viewer@123", AppRoles.Viewer).ConfigureAwait(false);
        }
        else
        {
            await EnsureBootstrapAdminAsync(users.AdminPassword).ConfigureAwait(false);
        }

        if (!await _db.SystemSettings.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            _db.SystemSettings.Add(new SystemSettings
            {
                // Entity default remains true for Dev; Production customer install passes false from Program.
                SimulatorAutoRun = simulatorAutoRunSeed
            });
        }

        if (!await _db.ConfigVersions.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            _db.ConfigVersions.Add(new ConfigVersion { Version = 1 });
        }

        if (!await _db.PlcConnections.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            SeedDemoLine();
        }
        else
        {
            await EnsureDemoStationsAsync(cancellationToken).ConfigureAwait(false);
        }

        await CollapseToSingleProductAsync(cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // 演示型号必须在点位落库拿到主键之后才能建（限值覆盖行要引用 TagId）。
        await SeedDemoRecipeAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("配置库初始化完成");
    }

    /// <summary>
    /// 生产首启动的引导管理员：口令取自配置，没配就随机生成一次并打印到日志。
    /// 两种来源都要求首次登录改密 —— 交付单或日志里的口令都只应当是一次性的。
    /// </summary>
    private async Task EnsureBootstrapAdminAsync(string? configuredPassword)
    {
        if (await _users.FindByNameAsync("admin").ConfigureAwait(false) is not null)
        {
            await FlagLegacyDefaultPasswordAsync().ConfigureAwait(false);
            return;
        }

        var generated = string.IsNullOrWhiteSpace(configuredPassword);
        var password = generated ? GenerateBootstrapPassword() : configuredPassword!;

        await EnsureUserAsync("admin", "管理员", password, AppRoles.Administrator, mustChangePassword: true)
            .ConfigureAwait(false);

        if (generated)
        {
            _logger.LogWarning(
                "已创建引导管理员 admin，本次随机口令为：{Password}；登录后必须立即修改。",
                password);
        }
    }

    /// <summary>
    /// 已经装好的现场：admin 若还挂着源码里的演示口令，补上强制改密标记。
    /// </summary>
    /// <remarks>
    /// 少了这一步，口令策略的修复就只对新装库有效 —— 升级上来的老现场照样拿着一把公开钥匙。
    /// </remarks>
    private async Task FlagLegacyDefaultPasswordAsync()
    {
        var admin = await _users.FindByNameAsync("admin").ConfigureAwait(false);
        if (admin is null || admin.MustChangePassword)
        {
            return;
        }

        if (!await _users.CheckPasswordAsync(admin, "Admin@123").ConfigureAwait(false))
        {
            return;
        }

        admin.MustChangePassword = true;
        var updated = await _users.UpdateAsync(admin).ConfigureAwait(false);
        if (updated.Succeeded)
        {
            _logger.LogWarning("admin 仍在使用初始演示口令，已要求其在下次登录时修改。");
        }
    }

    /// <summary>
    /// 生成一个一定满足 Identity 密码策略的随机口令：定长大写/小写/数字各取一位，其余补足后打散。
    /// </summary>
    private static string GenerateBootstrapPassword()
    {
        // 去掉 0/O/1/l/I 这类易混字符：口令要能被人从交付单上准确抄进去。
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string all = upper + lower + digits;

        var chars = new List<char>
        {
            upper[RandomNumberGenerator.GetInt32(upper.Length)],
            lower[RandomNumberGenerator.GetInt32(lower.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)]
        };

        while (chars.Count < 16)
        {
            chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        }

        // Fisher-Yates 打散：不然前三位的类别是固定图样，肉眼可预测。
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string([.. chars]);
    }

    private async Task EnsureUserAsync(
        string userName,
        string display,
        string password,
        string role,
        bool mustChangePassword = false)
    {
        var user = await _users.FindByNameAsync(userName).ConfigureAwait(false);
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = userName,
                Email = $"{userName}@datatrace.local",
                DisplayName = display,
                EmailConfirmed = true,
                MustChangePassword = mustChangePassword
            };
            var created = await _users.CreateAsync(user, password).ConfigureAwait(false);
            if (!created.Succeeded)
            {
                throw new InvalidOperationException(string.Join("; ", created.Errors.Select(e => e.Description)));
            }
        }

        if (!await _users.IsInRoleAsync(user, role).ConfigureAwait(false))
        {
            await _users.AddToRoleAsync(user, role).ConfigureAwait(false);
        }
    }
}

