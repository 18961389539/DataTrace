using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure.Identity;
using DataTrace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DataTrace.Infrastructure.Seeding;

public sealed class DatabaseSeeder
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

    public async Task SeedAsync(bool simulatorAutoRunSeed = true, CancellationToken cancellationToken = default)
    {
        await _db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "SystemSettings", "SimulatorAutoRun",
            "ALTER TABLE SystemSettings ADD COLUMN SimulatorAutoRun INTEGER NOT NULL DEFAULT 1", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "SystemSettings", "SimulatorIntervalMs",
            "ALTER TABLE SystemSettings ADD COLUMN SimulatorIntervalMs INTEGER NOT NULL DEFAULT 2500", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "SystemSettings", "SimulatorNgPercent",
            "ALTER TABLE SystemSettings ADD COLUMN SimulatorNgPercent INTEGER NOT NULL DEFAULT 8", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.AddColumnIfMissingAsync(_db, "SystemSettings", "SimulatorPalletPool",
            "ALTER TABLE SystemSettings ADD COLUMN SimulatorPalletPool INTEGER NOT NULL DEFAULT 20", cancellationToken).ConfigureAwait(false);

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

        await CleanupOrphanRecipeLimitsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var role in AppRoles.All)
        {
            if (!await _roles.RoleExistsAsync(role).ConfigureAwait(false))
            {
                await _roles.CreateAsync(new IdentityRole(role)).ConfigureAwait(false);
            }
        }

        await EnsureUserAsync("admin", "管理员", "Admin@123", AppRoles.Administrator).ConfigureAwait(false);
        await EnsureUserAsync("engineer", "工程师", "Engineer@123", AppRoles.Engineer).ConfigureAwait(false);
        await EnsureUserAsync("operator", "操作员", "Operator@123", AppRoles.Operator).ConfigureAwait(false);
        await EnsureUserAsync("viewer", "访客", "Viewer@123", AppRoles.Viewer).ConfigureAwait(false);

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

        await CollapseToSingleProductAsync(cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // 演示型号必须在点位落库拿到主键之后才能建（限值覆盖行要引用 TagId）。
        await SeedDemoRecipeAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("配置库初始化完成");
    }

    /// <summary>
    /// 演示型号 A100：压力规格上限 20 → 16 kN、工站温度预警上限 80 → 45℃。
    /// <b>默认不激活</b>：老产线的判定行为不该因为一次升级就被悄悄改掉，
    /// 必须由人在「产品型号」页显式切换。
    /// </summary>
    private async Task SeedDemoRecipeAsync(CancellationToken cancellationToken)
    {
        if (await _db.Recipes.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var tags = await _db.Tags.ToListAsync(cancellationToken).ConfigureAwait(false);
        if (tags.Count == 0)
        {
            return;
        }

        var recipe = new Recipe
        {
            Code = "A100",
            Name = "演示型号 A100",
            Enabled = true,
            Remark = "压力规格上限收紧到 16kN；工站温度预警上限收紧到 45℃"
        };

        foreach (var tag in tags)
        {
            if (tag.Code.EndsWith("_P1", StringComparison.Ordinal))
            {
                // 只覆盖规格上限，其余字段留空 → 沿用点位默认值。
                recipe.Limits.Add(new RecipeLimit { TagId = tag.Id, UpperLimit = 16 });
            }
            else if (tag.Code.EndsWith("_TEMP", StringComparison.Ordinal))
            {
                // 温度只收紧黄线，红线仍是 80℃、不判废。
                recipe.Limits.Add(new RecipeLimit { TagId = tag.Id, WarningUpperLimit = 45 });
            }
        }

        _db.Recipes.Add(recipe);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureUserAsync(string userName, string display, string password, string role)
    {
        var user = await _users.FindByNameAsync(userName).ConfigureAwait(false);
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = userName,
                Email = $"{userName}@datatrace.local",
                DisplayName = display,
                EmailConfirmed = true
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

    private void SeedDemoLine()
    {
        var plc = new PlcConnection
        {
            Name = "模拟PLC",
            Brand = PlcBrand.Simulator,
            Host = "127.0.0.1",
            Port = 6000,
            FloatWordOrder = FloatWordOrder.CDAB,
            Enabled = true,
            Heartbeat = new HeartbeatSettings
            {
                Address = "D0",
                IntervalMs = 1000,
                Mode = HeartbeatMode.Increment,
                Enabled = true
            }
        };

        plc.Stations.Add(CreateStation(
            "ST010", "上料工站", 10, first: true, last: false,
            trigger: "D1000", pallet: "D1010",
            press: "D1100", temp: "D1110",
            y: "D2000", x: "D2400"));

        plc.Stations.Add(CreateStation(
            "ST020", "压装工站", 20, first: false, last: false,
            trigger: "D1200", pallet: "D1210",
            press: "D1300", temp: "D1310",
            y: "D3600", x: "D4000"));

        plc.Stations.Add(CreateStation(
            "ST030", "下线工站", 30, first: false, last: true,
            trigger: "D1400", pallet: "D1410",
            press: "D1500", temp: "D1510",
            y: "D5200", x: "D5600"));

        _db.PlcConnections.Add(plc);
    }

    private async Task CollapseToSingleProductAsync(CancellationToken cancellationToken)
    {
        var stations = await _db.Stations
            .Include(s => s.Positions)
            .Include(s => s.Tags)
            .Include(s => s.Curves)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var changed = false;
        foreach (var station in stations)
        {
            if (station.PositionCount != 1)
            {
                station.PositionCount = 1;
                changed = true;
            }

            foreach (var extra in station.Positions.Where(p => p.Index != 1).ToList())
            {
                _db.ProductPositions.Remove(extra);
                changed = true;
            }

            var product = station.Positions.FirstOrDefault(p => p.Index == 1);
            if (product is not null && (product.OccupiedAddress is not null || product.Name != "产品"))
            {
                product.OccupiedAddress = null;
                product.Name = "产品";
                changed = true;
            }

            foreach (var tag in station.Tags.Where(t => t.PositionIndex > 1).ToList())
            {
                _db.Tags.Remove(tag);
                changed = true;
            }

            foreach (var curve in station.Curves.Where(c => c.PositionIndex > 1).ToList())
            {
                _db.Curves.Remove(curve);
                changed = true;
            }

            foreach (var curve in station.Curves.Where(c => c.PositionIndex < 1))
            {
                curve.PositionIndex = 1;
                changed = true;
            }
        }

        if (changed)
        {
            var version = await _db.ConfigVersions.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (version is not null)
            {
                version.Version++;
            }
        }
    }

    private static Station CreateStation(
        string code,
        string name,
        int sequence,
        bool first,
        bool last,
        string trigger,
        string pallet,
        string press,
        string temp,
        string y,
        string x)
    {
        const int points = 50;
        return new Station
        {
            Code = code,
            Name = name,
            Sequence = sequence,
            IsFirstStation = first,
            IsLastStation = last,
            TriggerAddress = trigger,
            TriggerValue = 1,
            PalletCodeAddress = pallet,
            PalletCodeLength = 16,
            PalletCodeDataType = PlcDataType.String,
            PositionCount = 1,
            Enabled = true,
            Positions =
            [
                new ProductPositionDefinition { Index = 1, Name = "产品" }
            ],
            Tags =
            [
                // 演示用三级限值：规格限 0~80℃，黄线 60℃，模拟器给的温度区间是 16~64℃，
                // 所以跑一会儿就会偶发进入预警带，让「预警不判废 + 预警 Top N」开箱可见。
                new TagDefinition
                {
                    Code = $"{code}_TEMP", Name = "工站温度", Address = temp, DataType = PlcDataType.Float, Unit = "℃",
                    LowerLimit = 0, UpperLimit = 80, WarningUpperLimit = 60, TargetValue = 40, PositionIndex = 0
                },
                new TagDefinition
                {
                    Code = $"{code}_P1", Name = "压力", Address = press, DataType = PlcDataType.Float, Unit = "kN",
                    LowerLimit = 5, UpperLimit = 20, WarningLowerLimit = 6, WarningUpperLimit = 16, TargetValue = 12.5,
                    PositionIndex = 1
                }
            ],
            Curves =
            [
                CreateCurve($"{code}_PD", "位移压力曲线", points, y, x)
            ]
        };
    }

    private static CurveDefinition CreateCurve(string code, string name, int points, string yStart, string xStart)
        => new()
        {
            Code = code,
            Name = name,
            PointCount = points,
            PositionIndex = 1,
            Enabled = true,
            Series =
            [
                new CurveSeries { Name = "压力", Role = SeriesRole.Y, StartAddress = yStart, DataType = PlcDataType.Float, StrideWords = 2, Unit = "kN" },
                new CurveSeries { Name = "位移", Role = SeriesRole.X, StartAddress = xStart, DataType = PlcDataType.Float, StrideWords = 2, Unit = "mm" }
            ]
        };
    /// <summary>
    /// 一次性清理悬空/不可用的型号限值覆盖：Tag 已删，或 Tag 已改为 Bool/String。
    /// 幂等；日志打印清理行数，便于现场核对历史脏数据。
    /// </summary>
    private async Task CleanupOrphanRecipeLimitsAsync(CancellationToken cancellationToken)
    {
        var numericTypes = new[] { PlcDataType.Int16, PlcDataType.Int32, PlcDataType.Float, PlcDataType.Double };
        var validTagIds = await _db.Tags.AsNoTracking()
            .Where(t => numericTypes.Contains(t.DataType))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var valid = validTagIds.ToHashSet();

        var orphans = await _db.RecipeLimits
            .Where(l => !valid.Contains(l.TagId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (orphans.Count == 0)
        {
            _logger.LogInformation("型号限值孤儿清理：无需处理（0 行）");
            return;
        }

        _db.RecipeLimits.RemoveRange(orphans);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("型号限值孤儿清理：已删除 {Count} 行（点位不存在或已改为 Bool/String）", orphans.Count);
    }


}
