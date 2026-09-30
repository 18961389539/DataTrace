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

partial class DatabaseSeeder
{
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
            if (tag.Name == "压力")
            {
                // 只覆盖规格上限，其余字段留空 → 沿用点位默认值。
                recipe.Limits.Add(new RecipeLimit { TagId = tag.Id, UpperLimit = 16 });
            }
            else if (tag.Name == "工站温度")
            {
                // 温度只收紧黄线，红线仍是 80℃、不判废。
                recipe.Limits.Add(new RecipeLimit { TagId = tag.Id, WarningUpperLimit = 45 });
            }
        }

        _db.Recipes.Add(recipe);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
            "ST030", "下线工站", 30, first: false, last: false,
            trigger: "D1400", pallet: "D1410",
            press: "D1500", temp: "D1510",
            y: "D5200", x: "D5600"));

        foreach (var station in CreateAdditionalDemoStations())
        {
            plc.Stations.Add(station);
        }

        _db.PlcConnections.Add(plc);
    }

    private async Task EnsureDemoStationsAsync(CancellationToken cancellationToken)
    {
        var demoPlc = await _db.PlcConnections
            .Include(p => p.Stations)
            .FirstOrDefaultAsync(
                p => p.Name == "模拟PLC" && p.Brand == PlcBrand.Simulator,
                cancellationToken)
            .ConfigureAwait(false);
        if (demoPlc is null)
        {
            return;
        }

        var existingCodes = (await _db.Stations
            .Select(s => s.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        var changed = false;
        foreach (var station in CreateAdditionalDemoStations())
        {
            if (existingCodes.Add(station.Code))
            {
                demoPlc.Stations.Add(station);
                changed = true;
            }
        }

        var previousLastStation = demoPlc.Stations.FirstOrDefault(s => s.Code == "ST030");
        if (previousLastStation?.IsLastStation == true)
        {
            previousLastStation.IsLastStation = false;
            changed = true;
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

    private static Station[] CreateAdditionalDemoStations() =>
    [
        CreateStation(
            "ST040", "装配工站", 40, first: false, last: false,
            trigger: "D1600", pallet: "D1610",
            press: "D1700", temp: "D1710",
            y: "D6800", x: "D7200"),
        CreateStation(
            "ST050", "性能检测工站", 50, first: false, last: false,
            trigger: "D1800", pallet: "D1810",
            press: "D1900", temp: "D1910",
            y: "D8400", x: "D8800"),
        CreateStation(
            "ST060", "终检工站", 60, first: false, last: true,
            trigger: "D2000", pallet: "D2010",
            press: "D2100", temp: "D2110",
            y: "D10000", x: "D10400")
    ];

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
            // 只收敛名称：有料地址仍被采集端使用，启动时清掉等于把现场配的空位检测弄没。
            if (product is not null && product.Name != "产品")
            {
                product.Name = "产品";
                changed = true;
            }

            foreach (var tag in station.Tags.Where(t => t.PositionIndex > 1).ToList())
            {
                _db.Tags.Remove(tag);
                changed = true;
            }

            foreach (var tag in station.Tags.Where(t => t.PositionIndex < 1))
            {
                tag.PositionIndex = 1;
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
                    Name = "工站温度", Address = temp, DataType = PlcDataType.Float, Unit = "℃",
                    LowerLimit = 0, UpperLimit = 80, WarningUpperLimit = 60, TargetValue = 40, PositionIndex = 1
                },
                new TagDefinition
                {
                    Name = "压力", Address = press, DataType = PlcDataType.Float, Unit = "kN",
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
}
