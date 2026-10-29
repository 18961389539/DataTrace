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
            Model = "A100",
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

        plc.Stations.Add(CreateOp130Station());

        plc.Stations.Add(CreateOp140Station());

        plc.Stations.Add(CreateOp150Station());

        plc.Stations.Add(CreateOp20Station());

        plc.Stations.Add(CreateOp30Station());

        plc.Stations.Add(CreateOp40Station());

        plc.Stations.Add(CreateOp50Station());

        plc.Stations.Add(CreateOp70Station());

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
                .ThenInclude(s => s.Tags)
            .Include(p => p.Stations)
                .ThenInclude(s => s.Alarms)
            .Include(p => p.Stations)
                .ThenInclude(s => s.Curves)
                    .ThenInclude(curve => curve.Series)
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
        foreach (var template in CreateAdditionalDemoStations())
        {
            var existing = demoPlc.Stations.FirstOrDefault(s => s.Code == template.Code);
            if (existing is null)
            {
                if (existingCodes.Add(template.Code))
                {
                    demoPlc.Stations.Add(template);
                    changed = true;
                }

                continue;
            }

            // 占位工站曾共用 D3000/D3010 并被禁用：启用时改成模板里的独立地址与点位。
            if (IsStubAdditionalDemoStation(existing))
            {
                RepairAdditionalDemoStation(existing, template);
                changed = true;
            }
        }


        var previousLastStation = demoPlc.Stations.FirstOrDefault(s => s.Code == "ST030");
        if (previousLastStation?.IsLastStation == true)
        {
            previousLastStation.IsLastStation = false;
            changed = true;
        }

        var legacyFirstStation = demoPlc.Stations.FirstOrDefault(s => s.Code == "ST010");
        if (legacyFirstStation is not null
            && !existingCodes.Contains("OP130")
            && IsLegacyDemoFirstStation(legacyFirstStation))
        {
            var oldTagIds = legacyFirstStation.Tags.Select(tag => tag.Id).ToArray();
            var oldLimits = await _db.RecipeLimits
                .Where(limit => oldTagIds.Contains(limit.TagId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            _db.RecipeLimits.RemoveRange(oldLimits);
            _db.Tags.RemoveRange(legacyFirstStation.Tags);
            _db.Curves.RemoveRange(legacyFirstStation.Curves);
            ConfigureOp130Station(legacyFirstStation);
            existingCodes.Remove("ST010");
            existingCodes.Add("OP130");
            changed = true;
        }

        var legacySecondStation = demoPlc.Stations.FirstOrDefault(s => s.Code == "ST020");
        if (legacySecondStation is not null
            && !existingCodes.Contains("OP140")
            && IsLegacyDemoSecondStation(legacySecondStation))
        {
            var oldTagIds = legacySecondStation.Tags.Select(tag => tag.Id).ToArray();
            var oldLimits = await _db.RecipeLimits
                .Where(limit => oldTagIds.Contains(limit.TagId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            _db.RecipeLimits.RemoveRange(oldLimits);
            _db.Tags.RemoveRange(legacySecondStation.Tags);
            _db.Curves.RemoveRange(legacySecondStation.Curves);
            ConfigureOp140Station(legacySecondStation);
            existingCodes.Remove("ST020");
            existingCodes.Add("OP140");
            changed = true;
        }

        var legacyThirdStation = demoPlc.Stations.FirstOrDefault(s => s.Code == "ST030");
        if (legacyThirdStation is not null
            && !existingCodes.Contains("OP150")
            && IsLegacyDemoThirdStation(legacyThirdStation))
        {
            var oldTagIds = legacyThirdStation.Tags.Select(tag => tag.Id).ToArray();
            var oldLimits = await _db.RecipeLimits
                .Where(limit => oldTagIds.Contains(limit.TagId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            _db.RecipeLimits.RemoveRange(oldLimits);
            _db.Tags.RemoveRange(legacyThirdStation.Tags);
            _db.Curves.RemoveRange(legacyThirdStation.Curves);
            ConfigureOp150Station(legacyThirdStation);
            existingCodes.Remove("ST030");
            existingCodes.Add("OP150");
            changed = true;
        }

        var legacyFifthStation = demoPlc.Stations.FirstOrDefault(s => s.Code == "ST050");
        if (legacyFifthStation is not null
            && !existingCodes.Contains("OP20")
            && IsLegacyDemoFifthStation(legacyFifthStation))
        {
            var oldTagIds = legacyFifthStation.Tags.Select(tag => tag.Id).ToArray();
            var oldLimits = await _db.RecipeLimits
                .Where(limit => oldTagIds.Contains(limit.TagId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            _db.RecipeLimits.RemoveRange(oldLimits);
            _db.Tags.RemoveRange(legacyFifthStation.Tags);
            _db.Curves.RemoveRange(legacyFifthStation.Curves);
            ConfigureOp20Station(legacyFifthStation);
            existingCodes.Remove("ST050");
            existingCodes.Add("OP20");
            changed = true;
        }

        if (!existingCodes.Contains("OP20") && !existingCodes.Contains("ST050"))
        {
            demoPlc.Stations.Add(CreateOp20Station());
            existingCodes.Add("OP20");
            changed = true;
        }

        var legacySixthStation = demoPlc.Stations.FirstOrDefault(s => s.Code == "ST060");
        if (legacySixthStation is not null
            && !existingCodes.Contains("OP30")
            && IsLegacyDemoSixthStation(legacySixthStation))
        {
            var oldTagIds = legacySixthStation.Tags.Select(tag => tag.Id).ToArray();
            var oldLimits = await _db.RecipeLimits
                .Where(limit => oldTagIds.Contains(limit.TagId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            _db.RecipeLimits.RemoveRange(oldLimits);
            _db.Tags.RemoveRange(legacySixthStation.Tags);
            _db.Curves.RemoveRange(legacySixthStation.Curves);
            ConfigureOp30Station(legacySixthStation);
            existingCodes.Remove("ST060");
            existingCodes.Add("OP30");
            changed = true;
        }

        if (!existingCodes.Contains("OP30") && !existingCodes.Contains("ST060"))
        {
            demoPlc.Stations.Add(CreateOp30Station());
            existingCodes.Add("OP30");
            changed = true;
        }

        if (!existingCodes.Contains("OP40") && !demoPlc.Stations.Any(s => s.Sequence == 80))
        {
            demoPlc.Stations.Add(CreateOp40Station());
            existingCodes.Add("OP40");
            changed = true;
        }

        if (demoPlc.Stations.FirstOrDefault(s => s.Code == "OP40")?.IsLastStation == true)
        {
            var formerEndStation = demoPlc.Stations.FirstOrDefault(s => s.Code == "OP30");
            if (formerEndStation?.IsLastStation == true)
            {
                formerEndStation.IsLastStation = false;
                changed = true;
            }
        }

        if (!existingCodes.Contains("OP50") && !demoPlc.Stations.Any(s => s.Sequence == 90))
        {
            demoPlc.Stations.Add(CreateOp50Station());
            existingCodes.Add("OP50");
            changed = true;
        }

        if (demoPlc.Stations.FirstOrDefault(s => s.Code == "OP50")?.IsLastStation == true)
        {
            foreach (var formerEndStation in demoPlc.Stations
                         .Where(s => (s.Code == "OP30" || s.Code == "OP40") && s.IsLastStation))
            {
                formerEndStation.IsLastStation = false;
                changed = true;
            }
        }

        if (!existingCodes.Contains("OP70") && !demoPlc.Stations.Any(s => s.Sequence == 100))
        {
            demoPlc.Stations.Add(CreateOp70Station());
            existingCodes.Add("OP70");
            changed = true;
        }

        if (demoPlc.Stations.FirstOrDefault(s => s.Code == "OP70")?.IsLastStation == true)
        {
            foreach (var formerEndStation in demoPlc.Stations
                         .Where(s => (s.Code == "OP30" || s.Code == "OP40" || s.Code == "OP50") && s.IsLastStation))
            {
                formerEndStation.IsLastStation = false;
                changed = true;
            }
        }

        // 长期下线演示站 ST040：清型号限值后删除整站（点位/曲线/报警随级联走），并不再种回。
        var retiredAssemblyStation = demoPlc.Stations.FirstOrDefault(s => s.Code == "ST040");
        if (retiredAssemblyStation is not null)
        {
            var retiredTagIds = retiredAssemblyStation.Tags.Select(tag => tag.Id).ToArray();
            if (retiredTagIds.Length > 0)
            {
                var retiredLimits = await _db.RecipeLimits
                    .Where(limit => retiredTagIds.Contains(limit.TagId))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                _db.RecipeLimits.RemoveRange(retiredLimits);
            }

            demoPlc.Stations.Remove(retiredAssemblyStation);
            _db.Stations.Remove(retiredAssemblyStation);
            existingCodes.Remove("ST040");
            changed = true;
        }
        // OP10 壳体上料顺序在 OP130 之前：启用后应作为真正首站，否则仿真会先在 OP10 建孤儿会话，
        // 再到 OP130（旧首站）触发「还没走完末站又从首站进入」的会话抢占洪流。
        var op10 = demoPlc.Stations.FirstOrDefault(s => s.Code == "OP10");
        if (op10 is { Enabled: true })
        {
            if (!op10.IsFirstStation)
            {
                op10.IsFirstStation = true;
                changed = true;
            }

            foreach (var formerFirst in demoPlc.Stations.Where(s => s.Code != "OP10" && s.IsFirstStation))
            {
                formerFirst.IsFirstStation = false;
                changed = true;
            }
        }

        // OP90 是成品下料，启用后应作为末站；放在补齐 OP40/50/70 之后，避免后加的 OP70 又把末站抢回去。
        var op90 = demoPlc.Stations.FirstOrDefault(s => s.Code == "OP90");
        if (op90 is { Enabled: true })
        {
            if (!op90.IsLastStation)
            {
                op90.IsLastStation = true;
                changed = true;
            }

            foreach (var formerEnd in demoPlc.Stations.Where(s => s.Code != "OP90" && s.IsLastStation))
            {
                formerEnd.IsLastStation = false;
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

    private static Station[] CreateAdditionalDemoStations() =>
    [
        CreateStation(
            "OP10", "壳体上料", 5, first: true, last: false,
            trigger: "D2100", pallet: "D2110",
            press: "D2200", temp: "D2210",
            y: "D14800", x: "D15200"),
        CreateStation(
            "OP60", "点胶后检测", 95, first: false, last: false,
            trigger: "D2300", pallet: "D2310",
            press: "D2400", temp: "D2410",
            y: "D15600", x: "D16000"),
        CreateStation(
            "OP80", "PIN针位置度检测", 105, first: false, last: false,
            trigger: "D2500", pallet: "D2510",
            press: "D2600", temp: "D2610",
            y: "D16400", x: "D16800"),
        CreateStation(
            "OP90", "成品自动下料", 110, first: false, last: true,
            trigger: "D2700", pallet: "D2710",
            press: "D2800", temp: "D2810",
            y: "D17200", x: "D17600"),
    ];

    /// <summary>
    /// 曾在界面上建出来的占位工站：触发/托盘都落在 D3000/D3010，且通常被禁用、没有点位。
    /// </summary>
    private static bool IsStubAdditionalDemoStation(Station station)
        => (string.Equals(station.TriggerAddress, "D3000", StringComparison.OrdinalIgnoreCase)
            && string.Equals(station.PalletCodeAddress, "D3010", StringComparison.OrdinalIgnoreCase))
           || (!station.Enabled && station.Tags.Count == 0);

    private static void RepairAdditionalDemoStation(Station station, Station template)
    {
        station.Name = template.Name;
        station.Sequence = template.Sequence;
        station.IsFirstStation = template.IsFirstStation;
        station.IsLastStation = template.IsLastStation;
        station.TriggerAddress = template.TriggerAddress;
        station.TriggerValue = template.TriggerValue;
        station.PalletCodeAddress = template.PalletCodeAddress;
        station.PalletCodeLength = template.PalletCodeLength;
        station.PalletCodeDataType = template.PalletCodeDataType;
        station.PositionCount = 1;
        station.Enabled = true;

        station.Tags.Clear();
        foreach (var tag in template.Tags)
        {
            station.Tags.Add(new TagDefinition
            {
                Name = tag.Name,
                Address = tag.Address,
                DataType = tag.DataType,
                Unit = tag.Unit,
                LowerLimit = tag.LowerLimit,
                UpperLimit = tag.UpperLimit,
                WarningLowerLimit = tag.WarningLowerLimit,
                WarningUpperLimit = tag.WarningUpperLimit,
                TargetValue = tag.TargetValue,
                PositionIndex = 1,
                Enabled = true,
                IsRequired = tag.IsRequired,
                Purpose = tag.Purpose,
                Source = tag.Source
            });
        }

        station.Curves.Clear();
        foreach (var curve in template.Curves)
        {
            var copy = new CurveDefinition
            {
                Code = curve.Code,
                Name = curve.Name,
                PointCount = curve.PointCount,
                PositionIndex = 1,
                Enabled = true
            };
            foreach (var series in curve.Series)
            {
                copy.Series.Add(new CurveSeries
                {
                    Name = series.Name,
                    Role = series.Role,
                    StartAddress = series.StartAddress,
                    DataType = series.DataType,
                    StrideWords = series.StrideWords,
                    Unit = series.Unit
                });
            }

            station.Curves.Add(copy);
        }
    }

    private static Station CreateOp130Station()
    {
        var station = new Station
        {
            Sequence = 10,
            Positions = [new ProductPositionDefinition { Index = 1, Name = "产品" }]
        };
        ConfigureOp130Station(station);
        return station;
    }

    private static Station CreateOp140Station()
    {
        var station = new Station
        {
            Sequence = 20,
            Positions = [new ProductPositionDefinition { Index = 1, Name = "产品" }]
        };
        ConfigureOp140Station(station);
        return station;
    }

    private static Station CreateOp150Station()
    {
        var station = new Station
        {
            Sequence = 30,
            Positions = [new ProductPositionDefinition { Index = 1, Name = "产品" }]
        };
        ConfigureOp150Station(station);
        return station;
    }

    private static Station CreateOp20Station()
    {
        var station = new Station
        {
            Sequence = 50,
            Positions = [new ProductPositionDefinition { Index = 1, Name = "产品" }]
        };
        ConfigureOp20Station(station);
        return station;
    }

    private static Station CreateOp30Station()
    {
        var station = new Station
        {
            Sequence = 60,
            Positions = [new ProductPositionDefinition { Index = 1, Name = "产品" }]
        };
        ConfigureOp30Station(station);
        station.IsLastStation = false;
        return station;
    }

    private static Station CreateOp40Station()
    {
        var station = new Station
        {
            Sequence = 80,
            Positions = [new ProductPositionDefinition { Index = 1, Name = "产品" }]
        };
        ConfigureOp40Station(station);
        station.IsLastStation = false;
        return station;
    }

    private static Station CreateOp50Station()
    {
        var station = new Station
        {
            Sequence = 90,
            Positions = [new ProductPositionDefinition { Index = 1, Name = "产品" }]
        };
        ConfigureOp50Station(station);
        station.IsLastStation = false;
        return station;
    }

    private static Station CreateOp70Station()
    {
        var station = new Station
        {
            Sequence = 100,
            Positions = [new ProductPositionDefinition { Index = 1, Name = "产品" }]
        };
        ConfigureOp70Station(station);
        return station;
    }

    private static void ConfigureOp130Station(Station station)
    {
        station.Code = "OP130";
        station.Name = "点胶外观检测";
        station.Sequence = 10;
        station.IsFirstStation = false;
        station.IsLastStation = false;
        station.TriggerAddress = "MW3002";
        station.TriggerValue = 1;
        station.PalletCodeAddress = "MW3001";
        station.PalletCodeLength = 1;
        station.PalletCodeDataType = PlcDataType.Int16;
        station.PositionCount = 1;
        station.Enabled = true;
        station.Tags.Clear();
        station.Tags.Add(CreateIdentityTag("数据1_配方号", "MW3000"));
        station.Tags.Add(CreateIdentityTag("托盘号", "MW3001"));

        for (var result = 1; result <= 17; result++)
        {
            station.Tags.Add(CreateDiscreteResultTag($"3D结果{result}", $"MW{3003 + result}"));
        }

        station.Curves.Clear();
    }

    private static void ConfigureOp140Station(Station station)
    {
        station.Code = "OP140";
        station.Name = "开关功能检测";
        station.Sequence = 20;
        station.IsFirstStation = false;
        station.IsLastStation = false;
        station.TriggerAddress = "MW3102";
        station.TriggerValue = 1;
        station.PalletCodeAddress = "MW3101";
        station.PalletCodeLength = 1;
        station.PalletCodeDataType = PlcDataType.Int16;
        station.PositionCount = 1;
        station.Enabled = true;
        station.Tags.Clear();
        station.Tags.Add(CreateIdentityTag("配方号", "MW3100"));
        station.Tags.Add(CreateIdentityTag("托盘号", "MW3101"));

        for (var product = 0; product <= 8; product++)
        {
            station.Tags.Add(CreateDiscreteResultTag($"产品{product}结果", $"MW{3103 + product}"));
        }

        station.Curves.Clear();
    }

    private static void ConfigureOp150Station(Station station)
    {
        station.Code = "OP150";
        station.Name = "位置度检测(左侧)";
        station.Sequence = 30;
        station.IsFirstStation = false;
        station.IsLastStation = false;
        station.TriggerAddress = "MW3202";
        station.TriggerValue = 1;
        station.PalletCodeAddress = "MW3201";
        station.PalletCodeLength = 1;
        station.PalletCodeDataType = PlcDataType.Int16;
        station.PositionCount = 1;
        station.Enabled = true;
        station.Tags.Clear();
        station.Tags.Add(CreateIdentityTag("配方号", "MW3200"));
        station.Tags.Add(CreateIdentityTag("托盘号", "MW3201"));
        for (var product = 1; product <= 4; product++)
        {
            station.Tags.Add(CreateDiscreteResultTag($"产品{product}拍基准结果", $"MW{3203 + product}"));
        }

        for (var product = 1; product <= 4; product++)
        {
            station.Tags.Add(CreateDiscreteResultTag($"产品{product}拍PIN结果", $"MW{3207 + product}"));
        }

        station.Curves.Clear();
    }

    private static void ConfigureOp20Station(Station station)
    {
        station.Code = "OP20";
        station.Name = "开关上料压装";
        station.Sequence = 50;
        station.IsFirstStation = false;
        station.IsLastStation = false;
        station.TriggerAddress = "MW2302";
        station.TriggerValue = 1;
        station.PalletCodeAddress = "MW2301";
        station.PalletCodeLength = 1;
        station.PalletCodeDataType = PlcDataType.Int16;
        station.PositionCount = 1;
        station.Enabled = true;
        station.Tags.Clear();
        station.Tags.Add(CreateIdentityTag("配方号", "MW2300"));
        station.Tags.Add(CreateIdentityTag("托盘号", "MW2301"));
        for (var press = 1; press <= 4; press++)
        {
            station.Tags.Add(CreateDiscreteResultTag($"压装{press}结果", $"MW{2303 + press}"));
        }

        for (var press = 1; press <= 4; press++)
        {
            station.Tags.Add(CreateInt16Tag($"压装{press}压力值", $"MW{2307 + press}"));
        }

        for (var press = 1; press <= 4; press++)
        {
            station.Tags.Add(CreateInt16Tag($"压装{press}位移值", $"MW{2311 + press}"));
        }

        station.Curves =
        [
            new CurveDefinition
            {
                Code = "OP20_PD",
                Name = "压装位移压力曲线",
                PointCount = 30,
                PositionIndex = 1,
                Enabled = true,
                Series =
                [
                    new CurveSeries
                    {
                        Name = "曲线位移值",
                        Role = SeriesRole.X,
                        StartAddress = "MD1700",
                        DataType = PlcDataType.Float,
                        StrideWords = 2,
                        Unit = "mm"
                    },
                    new CurveSeries
                    {
                        Name = "曲线压力值",
                        Role = SeriesRole.Y,
                        StartAddress = "%MD1730",
                        DataType = PlcDataType.Float,
                        StrideWords = 2,
                        Unit = "N"
                    }
                ]
            }
        ];
    }

    private static void ConfigureOp30Station(Station station)
    {
        station.Code = "OP30";
        station.Name = "开关上料压装";
        station.Sequence = 60;
        station.IsFirstStation = false;
        station.IsLastStation = true;
        station.TriggerAddress = "MW2402";
        station.TriggerValue = 1;
        station.PalletCodeAddress = "MW2401";
        station.PalletCodeLength = 1;
        station.PalletCodeDataType = PlcDataType.Int16;
        station.PositionCount = 1;
        station.Enabled = true;
        station.Tags.Clear();
        station.Tags.Add(CreateIdentityTag("配方号", "MW2400"));
        station.Tags.Add(CreateIdentityTag("托盘号", "MW2401"));
        for (var press = 1; press <= 4; press++)
        {
            station.Tags.Add(CreateDiscreteResultTag($"压装{press}结果", $"MW{2403 + press}"));
        }

        for (var press = 1; press <= 4; press++)
        {
            station.Tags.Add(CreateInt16Tag($"压装{press}压力值", $"MW{2407 + press}"));
        }

        for (var press = 1; press <= 4; press++)
        {
            station.Tags.Add(CreateInt16Tag($"压装{press}位移值", $"MW{2411 + press}"));
        }

        station.Curves =
        [
            new CurveDefinition
            {
                Code = "OP30_PD",
                Name = "压装位移压力曲线",
                PointCount = 30,
                PositionIndex = 1,
                Enabled = true,
                Series =
                [
                    new CurveSeries
                    {
                        Name = "曲线位移值",
                        Role = SeriesRole.X,
                        StartAddress = "MD1800",
                        DataType = PlcDataType.Float,
                        StrideWords = 2,
                        Unit = "mm"
                    },
                    new CurveSeries
                    {
                        Name = "曲线压力值",
                        Role = SeriesRole.Y,
                        StartAddress = "MD1830",
                        DataType = PlcDataType.Float,
                        StrideWords = 2,
                        Unit = "N"
                    }
                ]
            }
        ];
    }

    private static void ConfigureOp40Station(Station station)
    {
        station.Code = "OP40";
        station.Name = "开关上料压装";
        station.Sequence = 80;
        station.IsFirstStation = false;
        station.IsLastStation = true;
        station.TriggerAddress = "MW2502";
        station.TriggerValue = 1;
        station.PalletCodeAddress = "MW2501";
        station.PalletCodeLength = 1;
        station.PalletCodeDataType = PlcDataType.Int16;
        station.PositionCount = 1;
        station.Enabled = true;
        station.Tags.Clear();
        station.Tags.Add(CreateIdentityTag("配方号", "MW2500"));
        station.Tags.Add(CreateIdentityTag("托盘号", "MW2501"));
        for (var press = 1; press <= 4; press++)
        {
            station.Tags.Add(CreateDiscreteResultTag($"压装{press}结果", $"MW{2503 + press}"));
        }

        for (var press = 1; press <= 4; press++)
        {
            station.Tags.Add(CreateInt16Tag($"压装{press}压力值", $"MW{2507 + press}"));
        }

        for (var press = 1; press <= 4; press++)
        {
            station.Tags.Add(CreateInt16Tag($"压装{press}位移值", $"MW{2511 + press}"));
        }

        station.Curves =
        [
            new CurveDefinition
            {
                Code = "OP40_PD",
                Name = "压装位移压力曲线",
                PointCount = 30,
                PositionIndex = 1,
                Enabled = true,
                Series =
                [
                    new CurveSeries
                    {
                        Name = "曲线位移值",
                        Role = SeriesRole.X,
                        StartAddress = "MD1900",
                        DataType = PlcDataType.Float,
                        StrideWords = 2,
                        Unit = "mm"
                    },
                    new CurveSeries
                    {
                        Name = "曲线压力值",
                        Role = SeriesRole.Y,
                        StartAddress = "MD1930",
                        DataType = PlcDataType.Float,
                        StrideWords = 2,
                        Unit = "N"
                    }
                ]
            }
        ];
    }

    private static void ConfigureOp50Station(Station station)
    {
        station.Code = "OP50";
        station.Name = "开关上料压装";
        station.Sequence = 90;
        station.IsFirstStation = false;
        station.IsLastStation = true;
        station.TriggerAddress = "MW2602";
        station.TriggerValue = 1;
        station.PalletCodeAddress = "MW2601";
        station.PalletCodeLength = 1;
        station.PalletCodeDataType = PlcDataType.Int16;
        station.PositionCount = 1;
        station.Enabled = true;
        station.Tags.Clear();
        station.Tags.Add(CreateIdentityTag("配方号", "MW2600"));
        station.Tags.Add(CreateIdentityTag("托盘号", "MW2601"));
        for (var press = 1; press <= 4; press++)
        {
            station.Tags.Add(CreateDiscreteResultTag($"压装{press}结果", $"MW{2603 + press}"));
        }

        for (var press = 1; press <= 4; press++)
        {
            station.Tags.Add(CreateInt16Tag($"压装{press}压力值", $"MW{2607 + press}"));
        }

        for (var press = 1; press <= 4; press++)
        {
            station.Tags.Add(CreateInt16Tag($"压装{press}位移值", $"MW{2611 + press}"));
        }

        station.Curves =
        [
            new CurveDefinition
            {
                Code = "OP50_PD",
                Name = "压装位移压力曲线",
                PointCount = 30,
                PositionIndex = 1,
                Enabled = true,
                Series =
                [
                    new CurveSeries
                    {
                        Name = "曲线位移值",
                        Role = SeriesRole.X,
                        StartAddress = "MD2000",
                        DataType = PlcDataType.Float,
                        StrideWords = 2,
                        Unit = "mm"
                    },
                    new CurveSeries
                    {
                        Name = "曲线压力值",
                        Role = SeriesRole.Y,
                        StartAddress = "MD2030",
                        DataType = PlcDataType.Float,
                        StrideWords = 2,
                        Unit = "N"
                    }
                ]
            }
        ];
    }

    private static void ConfigureOp70Station(Station station)
    {
        station.Code = "OP70";
        station.Name = "开关压装检测";
        station.Sequence = 100;
        station.IsFirstStation = false;
        station.IsLastStation = true;
        station.TriggerAddress = "MW2702";
        station.TriggerValue = 1;
        station.PalletCodeAddress = "MW2701";
        station.PalletCodeLength = 1;
        station.PalletCodeDataType = PlcDataType.Int16;
        station.PositionCount = 1;
        station.Enabled = true;
        station.Tags.Clear();
        station.Tags.Add(CreateIdentityTag("配方号", "MW2700"));
        station.Tags.Add(CreateIdentityTag("托盘号", "MW2701"));
        for (var result = 1; result <= 16; result++)
        {
            station.Tags.Add(CreateDiscreteResultTag($"3D结果{result}", $"MW{2703 + result}"));
        }

        station.Curves.Clear();
    }

    private static TagDefinition CreateIdentityTag(string name, string address)
        => CreateInt16Tag(name, address, TagPurpose.Identity);

    private static TagDefinition CreateDiscreteResultTag(string name, string address)
        => CreateInt16Tag(name, address, TagPurpose.DiscreteResult);

    private static TagDefinition CreateInt16Tag(
        string name,
        string address,
        TagPurpose purpose = TagPurpose.QualityMeasurement) => new()
    {
        Name = name,
        Address = address,
        DataType = PlcDataType.Int16,
        Purpose = purpose,
        PositionIndex = 1,
        Enabled = true,
        IsRequired = true
    };

    private static bool IsLegacyDemoFirstStation(Station station)
        => station.Name == "上料工站"
           && station.TriggerAddress == "D1000"
           && station.PalletCodeAddress == "D1010"
           && station.PalletCodeDataType == PlcDataType.String
           && station.Tags.Count == 2
           && station.Tags.Any(tag => tag.Name == "工站温度" && tag.Address == "D1110")
           && station.Tags.Any(tag => tag.Name == "压力" && tag.Address == "D1100")
           && station.Curves.Count == 1
           && station.Curves.Any(curve => curve.Code == "ST010_PD");

    private static bool IsLegacyDemoSecondStation(Station station)
        => station.Name == "压装工站"
           && station.TriggerAddress == "D1200"
           && station.PalletCodeAddress == "D1210"
           && station.PalletCodeDataType == PlcDataType.String
           && station.Tags.Count == 2
           && station.Tags.Any(tag => tag.Name == "工站温度" && tag.Address == "D1310" && tag.DataType == PlcDataType.Float)
           && station.Tags.Any(tag => tag.Name == "压力" && tag.Address == "D1300" && tag.DataType == PlcDataType.Float)
           && station.Curves.Count == 1
           && station.Curves.Any(curve => curve.Code == "ST020_PD"
               && curve.Series.Any(series => series.Role == SeriesRole.Y && series.StartAddress == "D3600")
               && curve.Series.Any(series => series.Role == SeriesRole.X && series.StartAddress == "D4000"));

    private static bool IsLegacyDemoThirdStation(Station station)
        => station.Name == "下线工站"
           && station.TriggerAddress == "D1400"
           && station.PalletCodeAddress == "D1410"
           && station.PalletCodeDataType == PlcDataType.String
           && station.Tags.Count == 2
           && station.Tags.Any(tag => tag.Name == "工站温度" && tag.Address == "D1510" && tag.DataType == PlcDataType.Float)
           && station.Tags.Any(tag => tag.Name == "压力" && tag.Address == "D1500" && tag.DataType == PlcDataType.Float)
           && station.Curves.Count == 1
           && station.Curves.Any(curve => curve.Code == "ST030_PD"
               && curve.Series.Any(series => series.Role == SeriesRole.Y && series.StartAddress == "D5200")
               && curve.Series.Any(series => series.Role == SeriesRole.X && series.StartAddress == "D5600"));

    private static bool IsLegacyDemoFifthStation(Station station)
        => station.Name == "性能检测工站"
           && station.TriggerAddress == "D1800"
           && station.PalletCodeAddress == "D1810"
           && station.PalletCodeDataType == PlcDataType.String
           && station.Tags.Count == 2
           && station.Tags.Any(tag => tag.Name == "工站温度" && tag.Address == "D1910" && tag.DataType == PlcDataType.Float)
           && station.Tags.Any(tag => tag.Name == "压力" && tag.Address == "D1900" && tag.DataType == PlcDataType.Float)
           && station.Curves.Count == 1
           && station.Curves.Any(curve => curve.Code == "ST050_PD"
               && curve.Series.Any(series => series.Role == SeriesRole.Y && series.StartAddress == "D8400")
               && curve.Series.Any(series => series.Role == SeriesRole.X && series.StartAddress == "D8800"));

    private static bool IsLegacyDemoSixthStation(Station station)
        => station.Name == "终检工站"
           && station.TriggerAddress == "D2000"
           && station.PalletCodeAddress == "D2010"
           && station.PalletCodeDataType == PlcDataType.String
           && station.Tags.Count == 2
           && station.Tags.Any(tag => tag.Name == "工站温度" && tag.Address == "D2110" && tag.DataType == PlcDataType.Float)
           && station.Tags.Any(tag => tag.Name == "压力" && tag.Address == "D2100" && tag.DataType == PlcDataType.Float)
           && station.Curves.Count == 1
           && station.Curves.Any(curve => curve.Code == "ST060_PD"
               && curve.Series.Any(series => series.Role == SeriesRole.Y && series.StartAddress == "D10000")
               && curve.Series.Any(series => series.Role == SeriesRole.X && series.StartAddress == "D10400"));

    private async Task CollapseToSingleProductAsync(CancellationToken cancellationToken)
    {
        var stations = await _db.Stations
            .Include(s => s.Positions)
            .Include(s => s.Tags)
            .Include(s => s.Alarms)
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
