using DataTrace.Application.Evaluation;
using DataTrace.Application.Reporting;
using DataTrace.Application.Runtime;
using DataTrace.Collector;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Infrastructure.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Tests;

/// <summary>
/// 影子模式：采集流水线用预先建好的基线给每条波形打分，只写偏离字段。
/// 这里最重要的断言是 —— 偏离结果<b>绝不能</b>影响 ResultCodes / Judgement / NgReason。
/// </summary>
public class ShadowModeTests
{
    [Fact]
    public async Task Shadow_deviation_never_changes_the_judgement()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        var curve = station.Curves.First();

        // 故意塞一个"正常峰值只有 1"的基线：模拟器实际峰值在 10~14，会离它极远。
        ReplaceBaseline(harness, curve.Id, "压力", recipeCode: "", center: 1, sigma: 0.01);

        LoadCycle(harness, "P0051");
        var result = await harness.RunAsync(station);

        // 硬约束：偏离再离谱，主判定链也必须一动不动。
        Assert.Equal(ResultCodes.Success, result);

        var record = await LoadRecordAsync(harness, "P0051");
        Assert.NotNull(record);
        Assert.Equal(Judgement.Ok, record!.Judgement);
        var product = Assert.Single(record.Products);
        Assert.Equal(Judgement.Ok, product.Judgement);
        Assert.Null(product.NgReason);

        // 但影子模式的产出确实算出来并落库了。
        var feature = record.Curves.First().Features.Single(f => f.SeriesName == "压力");
        Assert.True(feature.HasDeviation);
        Assert.Equal(CurveTemplateVerdict.Abnormal, feature.DeviationVerdict);
        Assert.True(feature.DeviationRmsZ > 10, $"偏离分应极大，实际 {feature.DeviationRmsZ}");
        Assert.Equal(CurveFeatureDimension.Peak, feature.DeviationWorstDimension);
        Assert.Equal(50, feature.BaselineSampleCount);
    }

    [Fact]
    public async Task Without_a_baseline_the_row_carries_no_deviation()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);

        // 后台还没刷新过缓存 —— 采集照常进行，只是不带偏离分。
        LoadCycle(harness, "P0052");
        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var feature = (await LoadRecordAsync(harness, "P0052"))!.Curves.First()
            .Features.Single(f => f.SeriesName == "压力");

        // 不是"判成正常"，而是"没有比对" —— 两者在界面上的含义完全不同。
        Assert.Null(feature.DeviationVerdict);
        Assert.Null(feature.DeviationRmsZ);
        Assert.False(feature.HasDeviation);
        Assert.Equal(0, feature.BaselineSampleCount);
    }

    [Fact]
    public async Task Baseline_of_another_recipe_is_not_used_at_all()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        var curve = station.Curves.First();

        // 缓存属于 A100，但当前未选型号（判定型号为空）→ 整批失效。
        ReplaceBaseline(harness, curve.Id, "压力", recipeCode: "A100", center: 1, sigma: 0.01);

        LoadCycle(harness, "P0053");
        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        var feature = (await LoadRecordAsync(harness, "P0053"))!.Curves.First()
            .Features.Single(f => f.SeriesName == "压力");
        Assert.Null(feature.DeviationVerdict);
    }

    [Fact]
    public async Task Series_without_a_baseline_entry_is_skipped()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        var curve = station.Curves.First();

        // 只给 Y 序列建基线，X 序列没有条目。
        ReplaceBaseline(harness, curve.Id, "压力", recipeCode: "", center: 1, sigma: 0.01);

        LoadCycle(harness, "P0054");
        await harness.RunAsync(station);

        var features = (await LoadRecordAsync(harness, "P0054"))!.Curves.First().Features;
        Assert.NotNull(features.Single(f => f.SeriesName == "压力").DeviationVerdict);
        Assert.Null(features.Single(f => f.SeriesName == "位移").DeviationVerdict);
    }

    [Fact]
    public async Task Factory_builds_baselines_from_qualified_samples_only()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var curve = harness.Snapshot.Stations[0].Curves.First();
        var fake = new FakeRuntimeStore();
        var start = DateTime.Today.AddDays(-1).AddHours(8);

        // 25 条合格 + 5 条不合格：历史里的不良波形正是要检出的东西，不能当"正常"。
        // 峰值必须带抖动 —— 全部恒定的样本会被判成"零波动"而拒绝入缓存（那是对的，
        // 恒定往往意味着点位没在刷新），这条测试要验的是入缓存规则，不是零波动降级。
        for (var index = 0; index < 25; index++)
        {
            fake.Saved.Add(Sample(curve.Id, start.AddMinutes(index), $"P{index:000}", Judgement.Ok, "",
                12 + (index % 5 - 2) * 0.1));
        }

        for (var index = 0; index < 5; index++)
        {
            fake.Saved.Add(Sample(curve.Id, start.AddMinutes(100 + index), $"N{index:000}", Judgement.Ng, "", 40));
        }

        var factory = new CurveBaselineFactory(harness.ConfigRepository, fake);
        var snapshot = await factory.BuildAsync(DateTime.Now.AddMinutes(5));

        var template = snapshot.Find(curve.Id, "压力");
        Assert.NotNull(template);
        Assert.Equal(25, template!.SampleCount);
        Assert.True(template.IsReliable);
        // 峰值中心必须落在合格样本附近，而不是被那 5 条 40 拉高。
        Assert.Equal(12d, template[CurveFeatureDimension.Peak]!.Center, 6);

        // X 序列没有任何数据 → 不产生条目，而不是产生一个空模板。
        Assert.Null(snapshot.Find(curve.Id, "位移"));
    }

    [Fact]
    public async Task Factory_skips_series_that_lack_qualified_samples()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var curve = harness.Snapshot.Stations[0].Curves.First();
        var fake = new FakeRuntimeStore();
        var start = DateTime.Today.AddDays(-1).AddHours(8);

        // 只有 10 条，达不到可靠基线所需的 20 条。
        for (var index = 0; index < 10; index++)
        {
            fake.Saved.Add(Sample(curve.Id, start.AddMinutes(index), $"P{index:000}", Judgement.Ok, "", 12));
        }

        var factory = new CurveBaselineFactory(harness.ConfigRepository, fake);
        var snapshot = await factory.BuildAsync(DateTime.Now.AddMinutes(5));

        // 不入缓存，采集侧就不会给出一个"仅供参考"的分数。
        Assert.Empty(snapshot.Templates);
    }

    [Fact]
    public async Task Factory_ignores_samples_from_other_recipes()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var curve = harness.Snapshot.Stations[0].Curves.First();
        var fake = new FakeRuntimeStore();
        var start = DateTime.Today.AddDays(-1).AddHours(8);

        for (var index = 0; index < 25; index++)
        {
            fake.Saved.Add(Sample(curve.Id, start.AddMinutes(index), $"P{index:000}", Judgement.Ok, "",
                12 + (index % 5 - 2) * 0.1));
        }

        // 另外 25 条属于 A100：当前未选型号，必须被排除。
        for (var index = 0; index < 25; index++)
        {
            fake.Saved.Add(Sample(curve.Id, start.AddMinutes(200 + index), $"B{index:000}", Judgement.Ok, "A100",
                9 + (index % 5 - 2) * 0.1));
        }

        var factory = new CurveBaselineFactory(harness.ConfigRepository, fake);
        var snapshot = await factory.BuildAsync(DateTime.Now.AddMinutes(5));

        Assert.Equal("", snapshot.RecipeCode);
        var template = snapshot.Find(curve.Id, "压力");
        Assert.Equal(25, template!.SampleCount);
        Assert.Equal(12d, template[CurveFeatureDimension.Peak]!.Center, 6);
    }

    [Fact]
    public async Task Shadow_comparison_crosses_the_deviation_with_the_actual_judgement()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var curve = harness.Snapshot.Stations[0].Curves.First();
        var fake = new FakeRuntimeStore();
        var start = DateTime.Today.AddDays(-1).AddHours(8);

        // 20 条合格且带正常抖动 —— 要打得出分，基线就不能是零波动。
        for (var index = 0; index < 20; index++)
        {
            var jitter = (index % 5 - 2) * 0.1;
            fake.Saved.Add(Sample(curve.Id, start.AddMinutes(index), $"P{index:000}", Judgement.Ok, "", 12 + jitter));
        }

        // 四条样本正好落进四格。
        fake.Saved.Add(Sample(curve.Id, start.AddMinutes(30), "T1", Judgement.Ng, "", 60));
        fake.Saved.Add(Sample(curve.Id, start.AddMinutes(31), "T2", Judgement.Ok, "", 60));
        fake.Saved.Add(Sample(curve.Id, start.AddMinutes(32), "T3", Judgement.Ng, "", 12));
        fake.Saved.Add(Sample(curve.Id, start.AddMinutes(33), "T4", Judgement.Ok, "", 12));

        var service = new CurveTemplateService(fake, harness.ConfigRepository);
        var report = await service.GetBaselineAsync(
            curve.Id, "压力", DateTime.Today.AddDays(-2), DateTime.Today.AddDays(1));

        Assert.NotNull(report);
        var shadow = report!.Shadow;

        // 24 条都拿到了偏离结论。T2 是合格样本会参与建基线，
        // 但中位数 + MAD 让它影响不到基线中心。
        Assert.Equal(24, shadow.Checked);
        Assert.Equal(1, shadow.TruePositive);
        Assert.Equal(1, shadow.FalsePositive);
        Assert.Equal(1, shadow.FalseNegative);
        Assert.Equal(21, shadow.TrueNegative);

        // 报警中有真问题、不良中被抓到，各只有一条。
        Assert.Equal(0.5, shadow.Precision!.Value, 10);
        Assert.Equal(0.5, shadow.Recall!.Value, 10);
    }

    [Fact]
    public async Task Shadow_comparison_reports_no_ratio_when_nothing_was_checkable()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var curve = harness.Snapshot.Stations[0].Curves.First();
        var fake = new FakeRuntimeStore();

        var service = new CurveTemplateService(fake, harness.ConfigRepository);
        var report = await service.GetBaselineAsync(
            curve.Id, "压力", DateTime.Today.AddDays(-2), DateTime.Today.AddDays(1));

        Assert.Equal(0, report!.Shadow.Checked);
        // 分母为 0 时给 null 而不是 NaN —— 界面显示"—"，不显示一个假的 0%。
        Assert.Null(report.Shadow.Precision);
        Assert.Null(report.Shadow.Recall);
        Assert.Null(report.Shadow.FalsePositiveRate);
    }

    // ---------- 夹具 ----------

    private static void LoadCycle(CollectHarness harness, string palletCode)
        => SimulatorScenario.LoadStationCycle(
            harness.Simulator,
            harness.Plc,
            harness.Station(0),
            palletCode,
            new SimulatedCycleOptions { Random = new Random(palletCode.GetHashCode()), InjectNg = false });

    private static async Task<CollectRecord?> LoadRecordAsync(CollectHarness harness, string palletCode)
    {
        var item = (await harness.QueryAsync(palletCode)).Items.LastOrDefault();
        if (item is null)
        {
            return null;
        }

        // 列表查询不带 Curves / Features，必须按主键回查才拿得到波形特征。
        var store = harness.Scope.ServiceProvider.GetRequiredService<IRuntimeStore>();
        return await store.GetRecordAsync(item.MonthKey, item.Record.Id);
    }

    private static void ReplaceBaseline(
        CollectHarness harness,
        int curveDefinitionId,
        string seriesName,
        string recipeCode,
        double center,
        double sigma)
    {
        var cache = harness.Scope.ServiceProvider.GetRequiredService<ICurveBaselineCache>();
        cache.Replace(new CurveBaselineSnapshot
        {
            RecipeCode = recipeCode,
            RefreshedAt = DateTime.Now,
            Templates = new Dictionary<CurveBaselineKey, CurveTemplate>
            {
                [new CurveBaselineKey(curveDefinitionId, seriesName)] = new()
                {
                    SampleCount = 50,
                    ScorableDimensionCount = 1,
                    IsReliable = true,
                    Dimensions =
                    [
                        new CurveDimensionBaseline
                        {
                            Dimension = CurveFeatureDimension.Peak,
                            Count = 50,
                            Center = center,
                            Sigma = sigma,
                            Mean = center,
                            IsConstant = false
                        }
                    ]
                }
            }
        });
    }

    private static CollectSaveRequest Sample(
        int curveDefinitionId,
        DateTime time,
        string palletCode,
        Judgement judgement,
        string recipeCode,
        double peak)
        => new()
        {
            MonthKey = time.ToString("yyyyMM"),
            Record = new CollectRecord
            {
                SerialNo = palletCode,
                PalletCode = palletCode,
                StationId = 10,
                StationCode = "ST010",
                TriggerTime = time,
                Judgement = judgement,
                RecipeCode = recipeCode
            },
            Curves =
            [
                new CurvePayloadWrite
                {
                    Record = new CurveRecord
                    {
                        CurveDefinitionId = curveDefinitionId,
                        CurveCode = "ST010_PD",
                        CurveName = "位移压力曲线",
                        PointCount = 50
                    },
                    Payload = new CurvePayload { PointCount = 50, Series = [] },
                    Features =
                    [
                        new CurveFeature
                        {
                            SeriesName = "压力",
                            Role = SeriesRole.Y,
                            PointCount = 50,
                            Peak = peak
                        }
                    ]
                }
            ]
        };
}
