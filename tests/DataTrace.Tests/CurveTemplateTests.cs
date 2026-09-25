using DataTrace.Application.Reporting;
using DataTrace.Application.Runtime;
using DataTrace.Collector;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Infrastructure.Reporting;

namespace DataTrace.Tests;

/// <summary>
/// 波形基线：从历史合格样本建立模板，再对新曲线做多变量偏离打分。
/// 打分本身是纯函数，先把公式与边界锁死，再验证服务层的取数与筛选。
/// </summary>
public class CurveTemplateTests
{
    private const double Tolerance = 1e-9;

    // ---------- 纯函数：维度枚举 ----------

    [Fact]
    public void Every_dimension_reads_its_own_field()
    {
        // 逐维度给一个互不相同的值，漏字段或串字段会立刻暴露。
        var feature = new CurveFeature
        {
            Min = 1,
            Peak = 2,
            Mean = 3,
            StdDev = 4,
            Area = 5,
            RiseSlope = 6,
            HoldSlope = 7,
            FallRatio = 8,
            MaxStep = 9,
            Oscillations = 10,
            RiseSpan = 11,
            PeakIndex = 12,
            MinIndex = 13,
            RiseIndex = 14
        };

        var expected = new Dictionary<CurveFeatureDimension, double>
        {
            [CurveFeatureDimension.Min] = 1,
            [CurveFeatureDimension.Peak] = 2,
            [CurveFeatureDimension.Mean] = 3,
            [CurveFeatureDimension.StdDev] = 4,
            [CurveFeatureDimension.Area] = 5,
            [CurveFeatureDimension.RiseSlope] = 6,
            [CurveFeatureDimension.HoldSlope] = 7,
            [CurveFeatureDimension.FallRatio] = 8,
            [CurveFeatureDimension.MaxStep] = 9,
            [CurveFeatureDimension.Oscillations] = 10,
            [CurveFeatureDimension.RiseSpan] = 11,
            [CurveFeatureDimension.PeakIndex] = 12,
            [CurveFeatureDimension.MinIndex] = 13,
            [CurveFeatureDimension.RiseIndex] = 14
        };

        // 枚举与取值表必须一一对应：新增维度却忘了加进 All 会在这里失败。
        Assert.Equal(expected.Keys.OrderBy(k => k), CurveFeatureDimensions.All.OrderBy(d => d));

        foreach (var (dimension, value) in expected)
        {
            Assert.Equal(value, CurveFeatureDimensions.Read(feature, dimension), Tolerance);
        }
    }

    [Fact]
    public void Sample_index_and_count_dimensions_are_discrete()
    {
        // 采样序号与计数零波动时不代表过程受控，因此不算连续量（不参与常量偏离判定）。
        CurveFeatureDimension[] discrete =
        [
            CurveFeatureDimension.Oscillations,
            CurveFeatureDimension.RiseSpan,
            CurveFeatureDimension.PeakIndex,
            CurveFeatureDimension.MinIndex,
            CurveFeatureDimension.RiseIndex
        ];

        foreach (var dimension in CurveFeatureDimensions.All)
        {
            Assert.Equal(!discrete.Contains(dimension), CurveFeatureDimensions.IsContinuous(dimension));
        }
    }

    // ---------- 纯函数：建模板 ----------

    [Fact]
    public void Baseline_follows_the_median_so_history_outliers_cannot_drag_it()
    {
        // 18 条正常（峰值 ≈ 10）+ 2 条极端（峰值 100）：这是"历史里本来就混着不良曲线"的常态。
        var samples = new List<CurveFeature>();
        for (var i = 0; i < 18; i++)
        {
            samples.Add(Feature(f => f.Peak = i % 2 == 0 ? 10.0 : 10.1));
        }

        samples.Add(Feature(f => f.Peak = 100));
        samples.Add(Feature(f => f.Peak = 100));

        var baseline = CurveTemplateBuilder.Build(samples)[CurveFeatureDimension.Peak]!;

        // 中位数不受两条离群样本影响（20 条里正常值占前 18 位）。
        Assert.Equal(10.1, baseline.Center, Tolerance);

        // 均值则被拽到 (90 + 90.9 + 200) / 20 ≈ 19 —— 拿它当基准，整个基线就是错的。
        Assert.True(baseline.Mean > 19, $"均值应为 ~19，实际 {baseline.Mean}");
        Assert.True(baseline.Mean - baseline.Center > 8);

        // 离散度也没被那两条撑爆（若用标准差，σ 会接近 20）。
        Assert.True(baseline.Sigma < 1, $"稳健离散度应远小于 1，实际 {baseline.Sigma}");
        Assert.Equal(2, baseline.OutlierCount);
        Assert.False(baseline.IsConstant);
        Assert.True(baseline.IsScorable);
    }

    [Fact]
    public void Constant_dimension_is_marked_and_excluded_from_scoring()
    {
        // 峰值恒定、均值有波动：只有均值可打分。
        var samples = new List<CurveFeature>();
        for (var i = 0; i < 25; i++)
        {
            var i1 = i;
            samples.Add(Feature(f =>
            {
                f.Peak = 10.0;
                f.Mean = 100 + i1 * 0.5;
            }));
        }

        var template = CurveTemplateBuilder.Build(samples);

        Assert.True(template[CurveFeatureDimension.Peak]!.IsConstant);
        Assert.False(template[CurveFeatureDimension.Peak]!.IsScorable);
        Assert.Equal(0, template[CurveFeatureDimension.Peak]!.Sigma, Tolerance);

        Assert.False(template[CurveFeatureDimension.Mean]!.IsConstant);
        Assert.True(template[CurveFeatureDimension.Mean]!.IsScorable);
        Assert.True(template.IsReliable);
        Assert.True(template.ScorableDimensionCount > 0);
    }

    [Fact]
    public void Empty_samples_produce_an_unreliable_template_with_an_explanation()
    {
        var template = CurveTemplateBuilder.Build([]);

        Assert.Equal(0, template.SampleCount);
        Assert.Equal(0, template.ScorableDimensionCount);
        Assert.False(template.IsReliable);
        Assert.Contains("没有可用于建立基线", template.Note);
        Assert.Equal(CurveFeatureDimensions.All.Count, template.Dimensions.Count);
        Assert.All(template.Dimensions, d => Assert.False(d.IsScorable));
    }

    [Fact]
    public void All_constant_samples_report_that_scoring_is_impossible()
    {
        // 20 条完全一样的特征：传感器可能根本没刷新，这种情况必须明说而不是给个 0 分。
        var identical = Feature(f =>
        {
            f.Peak = 10;
            f.Mean = 10;
        });
        var samples = Enumerable.Range(0, 20).Select(_ => Clone(identical)).ToList();

        var template = CurveTemplateBuilder.Build(samples);

        Assert.False(template.IsReliable);
        Assert.Equal(0, template.ScorableDimensionCount);
        Assert.Contains("无波动", template.Note);
    }

    [Fact]
    public void Too_few_samples_mark_the_template_unreliable_but_still_statistical()
    {
        var samples = Enumerable.Range(0, 5).Select(i => Feature(f => f.Peak = 10 + i * 0.5)).ToList();

        var template = CurveTemplateBuilder.Build(samples);

        Assert.Equal(5, template.SampleCount);
        Assert.False(template.IsReliable);
        Assert.Contains("少于可靠基线所需", template.Note);
        // 数字照样算出来，只是不下结论。
        Assert.True(template[CurveFeatureDimension.Peak]!.IsScorable);
    }

    // ---------- 纯函数：打分 ----------

    [Fact]
    public void Rms_z_is_the_root_mean_square_of_the_dimension_scores()
    {
        var template = Template(
            Dim(CurveFeatureDimension.Peak, center: 10, sigma: 2),
            Dim(CurveFeatureDimension.Mean, center: 100, sigma: 4));

        // z1 = (13-10)/2 = 1.5，z2 = (104-100)/4 = 1.0
        var score = CurveTemplateMatcher.Score(template, Feature(f =>
        {
            f.Peak = 13;
            f.Mean = 104;
        }));

        Assert.Equal(Math.Sqrt((1.5 * 1.5 + 1.0) / 2), score.RmsZ, 10);
        Assert.Equal(1.5, score.MaxAbsZ, 10);
        Assert.Equal(CurveFeatureDimension.Peak, score.WorstDimension);
        Assert.Equal(2, score.ScoredDimensionCount);
        Assert.Equal(CurveTemplateVerdict.Normal, score.Verdict);

        // 都不到 2σ，不该产生偏离明细。
        Assert.Empty(score.Deviations);
        Assert.Equal(0, score.ConstantBreachCount);
    }

    [Fact]
    public void Verdict_thresholds_are_inclusive()
    {
        var template = Template(
            Dim(CurveFeatureDimension.Peak, center: 10, sigma: 2),
            Dim(CurveFeatureDimension.Mean, center: 100, sigma: 4));

        // 两维 z 都恰好 2.0 → RMS 恰好 2.0 → 落在"可疑"这一档的下边界（含）。
        var suspicious = CurveTemplateMatcher.Score(template, Feature(f =>
        {
            f.Peak = 14;
            f.Mean = 108;
        }));
        Assert.Equal(2.0, suspicious.RmsZ, 10);
        Assert.Equal(CurveTemplateVerdict.Suspicious, suspicious.Verdict);
        Assert.Equal(2, suspicious.Deviations.Count);

        // 两维 z 都恰好 3.0 → RMS 恰好 3.0 → 异常（含）。
        var abnormal = CurveTemplateMatcher.Score(template, Feature(f =>
        {
            f.Peak = 16;
            f.Mean = 112;
        }));
        Assert.Equal(3.0, abnormal.RmsZ, 10);
        Assert.Equal(CurveTemplateVerdict.Abnormal, abnormal.Verdict);
    }

    [Fact]
    public void One_extreme_dimension_is_enough_to_call_it_abnormal()
    {
        var template = Template(
            Dim(CurveFeatureDimension.Peak, center: 10, sigma: 2),
            Dim(CurveFeatureDimension.Mean, center: 100, sigma: 4));

        // 峰值 z = 4.0，均值 z = 0 → RMS 只有 2.83（不到 3），但单维已达 4σ。
        var score = CurveTemplateMatcher.Score(template, Feature(f =>
        {
            f.Peak = 18;
            f.Mean = 100;
        }));

        Assert.Equal(Math.Sqrt(16.0 / 2), score.RmsZ, 10);
        Assert.True(score.RmsZ < CurveTemplateMatcher.AbnormalZ);
        Assert.Equal(4.0, score.MaxAbsZ, 10);
        Assert.Equal(CurveTemplateVerdict.Abnormal, score.Verdict);

        // 明细只列显著偏离的维度，按绝对 z 降序。
        var deviation = Assert.Single(score.Deviations);
        Assert.Equal(CurveFeatureDimension.Peak, deviation.Dimension);
        Assert.Equal(18d, deviation.Value, Tolerance);
        Assert.True(deviation.IsDeviating);
    }

    [Fact]
    public void Constant_continuous_dimension_breaking_is_flagged()
    {
        var template = Template(
            Const(CurveFeatureDimension.Peak, center: 10),
            Dim(CurveFeatureDimension.Mean, center: 100, sigma: 4));

        // 幅度 1% 以内算"没变"（浮点噪声不该报）。
        var quiet = CurveTemplateMatcher.Score(template, Feature(f =>
        {
            f.Peak = 10.05;
            f.Mean = 100;
        }));
        Assert.Equal(0, quiet.ConstantBreachCount);
        Assert.Equal(CurveTemplateVerdict.Normal, quiet.Verdict);

        // 一旦真的变了就是强信号：这个量历史上从来没动过。
        var breach = CurveTemplateMatcher.Score(template, Feature(f =>
        {
            f.Peak = 12;
            f.Mean = 100;
        }));
        Assert.Equal(1, breach.ConstantBreachCount);
        Assert.Equal(CurveTemplateVerdict.Suspicious, breach.Verdict);

        var deviation = Assert.Single(breach.Deviations);
        Assert.True(deviation.IsConstantBreach);
        // 零波动维度没有 σ，z 分数必须是 null 而不是被编造成一个巨大的数。
        Assert.Null(deviation.ZScore);
        Assert.Equal(0d, deviation.BaselineSigma, Tolerance);
    }

    [Fact]
    public void Constant_discrete_dimension_breaking_is_ignored()
    {
        // 峰值位置恒定在第 25 点：挪到第 30 点不值得报警（离散量，正常抖动）。
        var template = Template(
            Const(CurveFeatureDimension.PeakIndex, center: 25),
            Dim(CurveFeatureDimension.Peak, center: 10, sigma: 2));

        var score = CurveTemplateMatcher.Score(template, Feature(f =>
        {
            f.PeakIndex = 30;
            f.Peak = 10;
        }));

        Assert.Equal(0, score.ConstantBreachCount);
        Assert.Equal(CurveTemplateVerdict.Normal, score.Verdict);
        Assert.Empty(score.Deviations);
    }

    [Fact]
    public void Unreliable_template_never_issues_a_verdict_but_still_reports_numbers()
    {
        var template = TemplateUnreliable(Dim(CurveFeatureDimension.Peak, center: 10, sigma: 2));

        var score = CurveTemplateMatcher.Score(template, Feature(f => f.Peak = 20));

        // 5σ 的偏离照样算出来，但结论固定为"基线不可用" —— 界面据此提示"仅供参考"。
        Assert.Equal(5.0, score.RmsZ, 10);
        Assert.Equal(CurveTemplateVerdict.InsufficientBaseline, score.Verdict);
        Assert.Single(score.Deviations);
    }

    [Fact]
    public void Template_without_any_scorable_dimension_issues_no_verdict()
    {
        var template = Template(
            Const(CurveFeatureDimension.Peak, center: 10),
            Const(CurveFeatureDimension.PeakIndex, center: 25));

        var score = CurveTemplateMatcher.Score(template, Feature(f =>
        {
            f.Peak = 10;
            f.PeakIndex = 25;
        }));

        Assert.Equal(0, score.ScoredDimensionCount);
        Assert.Null(score.WorstDimension);
        Assert.Equal(CurveTemplateVerdict.InsufficientBaseline, score.Verdict);
    }

    [Fact]
    public void A_typical_healthy_waveform_stays_normal_against_its_own_baseline()
    {
        // 端到端一点：用 30 条带正常抖动的波形建模板，再逐条回测，必须全判正常。
        // 抖动刻意用确定性循环值而不是随机数 —— 测试不能因为种子不同偶发失败。
        var samples = new List<CurveFeature>();
        for (var i = 0; i < 30; i++)
        {
            var i1 = i;
            samples.Add(Feature(f =>
            {
                f.Peak = 12 + (i1 % 5 - 2) * 0.1;
                f.Mean = 7.5 + (i1 % 3 - 1) * 0.05;
                f.Area = 380 + (i1 % 7 - 3) * 2;
                f.HoldSlope = -0.05 + (i1 % 4 - 1.5) * 0.002;
            }));
        }

        var template = CurveTemplateBuilder.Build(samples);
        Assert.True(template.IsReliable);
        Assert.Equal(4, template.ScorableDimensionCount);

        foreach (var sample in samples)
        {
            var score = CurveTemplateMatcher.Score(template, sample);
            Assert.True(
                score.Verdict == CurveTemplateVerdict.Normal,
                $"正常样本被误判为 {score.Verdict}（RMS z = {score.RmsZ:F2}）");
        }
    }

    [Fact]
    public void A_waveform_with_a_lost_hold_pressure_stands_out()
    {
        // 保压保持不住：峰值正常，但保压段斜率由 -0.05 掉到 -0.6。
        var samples = new List<CurveFeature>();
        for (var i = 0; i < 30; i++)
        {
            var i1 = i;
            samples.Add(Feature(f =>
            {
                f.Peak = 12 + (i1 % 3 - 1) * 0.05;
                f.HoldSlope = -0.05 + (i1 % 4 - 1.5) * 0.002;
            }));
        }

        var template = CurveTemplateBuilder.Build(samples);

        var bad = CurveTemplateMatcher.Score(template, Feature(f =>
        {
            f.Peak = 12.05;
            f.HoldSlope = -0.6;
        }));

        Assert.Equal(CurveTemplateVerdict.Abnormal, bad.Verdict);
        // 必须指名道姓地说是保压段斜率出了问题，而不是只给一个总分。
        Assert.Equal(CurveFeatureDimension.HoldSlope, bad.WorstDimension);
        Assert.Contains(bad.Deviations, d => d.Dimension == CurveFeatureDimension.HoldSlope);

        // 同一批数据里的一条正常波形仍然安静 —— 证明报警来自波形本身而不是基线太窄。
        var healthy = CurveTemplateMatcher.Score(template, samples[0]);
        Assert.Equal(CurveTemplateVerdict.Normal, healthy.Verdict);
    }

    // ---------- 服务层 ----------

    [Fact]
    public async Task Baseline_uses_only_qualified_samples_of_the_active_recipe()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var curve = harness.Snapshot.Stations[0].Curves.First();
        var fake = new FakeRuntimeStore();
        var start = DateTime.Today.AddDays(-1).AddHours(8);

        // 20 条合格 + 5 条不合格：不合格的波形正是要检出的东西，不能拿来当"正常"。
        for (var i = 0; i < 20; i++)
        {
            fake.Saved.Add(Sample(curve.Id, "压力", SeriesRole.Y, start.AddMinutes(i), $"P{i:000}", Judgement.Ok, "", f => f.Peak = 12));
        }

        for (var i = 0; i < 5; i++)
        {
            fake.Saved.Add(Sample(curve.Id, "压力", SeriesRole.Y, start.AddMinutes(100 + i), $"N{i:000}", Judgement.Ng, "", f => f.Peak = 40));
        }

        var service = new CurveTemplateService(fake, harness.ConfigRepository);

        var report = await service.GetBaselineAsync(
            curve.Id, "压力", DateTime.Today.AddDays(-2), DateTime.Today.AddDays(1));

        Assert.NotNull(report);
        Assert.Equal(20, report!.BaselineSampleCount);
        Assert.Equal(25, report.TotalSampleCount);
        Assert.Equal(5, report.NgCount);
        Assert.Equal(0, report.MismatchedRecipeCount);
        Assert.Equal("", report.RecipeCode);

        // 基线只看合格样本 → 峰值中心必须接近 12，而不是被那 5 条 40 拉高。
        Assert.Equal(12d, report.Template[CurveFeatureDimension.Peak]!.Center, 6);

        // 最近样本按时间升序，且不合格样本也会被打分（正是要观察它们离基线有多远）。
        Assert.True(report.Recent.Count <= 30);
        Assert.True(report.Recent.Zip(report.Recent.Skip(1)).All(p => p.First.Time <= p.Second.Time));
        Assert.Contains(report.Recent, s => s.IsNg);
    }

    [Fact]
    public async Task Baseline_ignores_samples_from_other_recipes()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var curve = harness.Snapshot.Stations[0].Curves.First();
        var fake = new FakeRuntimeStore();
        var start = DateTime.Today.AddDays(-1).AddHours(8);

        // 当前未选型号（RecipeCode = ""）时，A100 的样本必须被排除：
        // 不同型号的压力规格本来就不同，混在一起建基线会让切换型号后每条都报警。
        for (var i = 0; i < 10; i++)
        {
            fake.Saved.Add(Sample(curve.Id, "压力", SeriesRole.Y, start.AddMinutes(i), $"P{i:000}", Judgement.Ok, "", f => f.Peak = 12));
        }

        for (var i = 0; i < 4; i++)
        {
            fake.Saved.Add(Sample(curve.Id, "压力", SeriesRole.Y, start.AddMinutes(20 + i), $"B{i:000}", Judgement.Ok, "A100", f => f.Peak = 9));
        }

        var service = new CurveTemplateService(fake, harness.ConfigRepository);

        var report = await service.GetBaselineAsync(
            curve.Id, "压力", DateTime.Today.AddDays(-2), DateTime.Today.AddDays(1));

        Assert.Equal(10, report!.BaselineSampleCount);
        Assert.Equal(4, report.MismatchedRecipeCount);
        Assert.DoesNotContain(report.Recent, s => s.PalletCode.StartsWith("B", StringComparison.Ordinal));
        Assert.Equal(12d, report.Template[CurveFeatureDimension.Peak]!.Center, 6);
    }

    /// <summary>
    /// 型号过滤必须先于 take：先取"最新 N 条"再按型号筛的话，另一种型号最近产量大一点
    /// 就会把本型号的样本整段挤出去，基线直接空掉、采集端那 5 分钟也不打分。
    /// </summary>
    [Fact]
    public async Task Recipe_filter_applies_before_the_sample_cap()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var curve = harness.Snapshot.Stations[0].Curves.First();
        var fake = new FakeRuntimeStore();
        var start = DateTime.Today.AddDays(-1).AddHours(8);

        // 本型号 20 条（较早），另一种型号 60 条（更近）：按 take 截断后本型号一条不剩。
        for (var i = 0; i < 20; i++)
        {
            fake.Saved.Add(Sample(curve.Id, "压力", SeriesRole.Y, start.AddMinutes(i), $"P{i:000}", Judgement.Ok, "", f => f.Peak = 12));
        }

        for (var i = 0; i < 60; i++)
        {
            fake.Saved.Add(Sample(curve.Id, "压力", SeriesRole.Y, start.AddHours(2).AddMinutes(i), $"B{i:000}", Judgement.Ok, "OTHER", f => f.Peak = 9));
        }

        var service = new CurveTemplateService(fake, harness.ConfigRepository);

        // maxSamples 收窄到 30：截断窗口里全是 OTHER，只有"先过滤再取"才能拿到本型号样本。
        var report = await service.GetBaselineAsync(
            curve.Id, "压力", DateTime.Today.AddDays(-2), DateTime.Today.AddDays(1),
            recentCount: 30, maxSamples: 30);

        Assert.NotNull(report);
        Assert.Equal(20, report!.BaselineSampleCount);
        Assert.Equal(20, report.Recent.Count);
        Assert.DoesNotContain(report.Recent, s => s.PalletCode.StartsWith("B", StringComparison.Ordinal));

        // "取到多少条样本"是区间真数（80），不受打分窗口上限影响。
        Assert.Equal(80, report.TotalSampleCount);
        Assert.Equal(60, report.MismatchedRecipeCount);
        Assert.Equal(12d, report.Template[CurveFeatureDimension.Peak]!.Center, 6);
    }

    /// <summary>
    /// 改过编码的型号：历史样本仍写旧码，页面必须和采集端一样把旧码算作本型号，
    /// 否则记录上带着偏离分，这个页面却说一条样本都没有。
    /// </summary>
    [Fact]
    public async Task Baseline_treats_previous_recipe_codes_as_the_same_model()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var curve = harness.Snapshot.Stations[0].Curves.First();

        // 把 A100 改名成 B300：改名后 PreviousCodes 记着 A100，历史样本仍写着旧码。
        var recipe = harness.Snapshot.Recipes.Single(r => r.Code == "A100");
        await harness.ConfigRepository.SetActiveRecipeAsync(recipe.Id);
        recipe.Code = "B300";
        await harness.ConfigRepository.SaveRecipeAsync(recipe);
        await harness.RefreshSnapshotAsync();
        Assert.Equal("A100", harness.Snapshot.ActiveRecipe!.PreviousCodes);

        var fake = new FakeRuntimeStore();
        var start = DateTime.Today.AddDays(-1).AddHours(8);
        // 峰值带抖动：全恒定的样本会被判成"零波动"而拒绝建基线（那是对的），
        // 这条要验的是型号归属，不是零波动降级。
        for (var i = 0; i < 25; i++)
        {
            var jitter = (i % 5 - 2) * 0.1;
            fake.Saved.Add(Sample(curve.Id, "压力", SeriesRole.Y, start.AddMinutes(i), $"O{i:000}", Judgement.Ok, "A100", f => f.Peak = 12 + jitter));
        }

        var service = new CurveTemplateService(fake, harness.ConfigRepository);

        var report = await service.GetBaselineAsync(
            curve.Id, "压力", DateTime.Today.AddDays(-2), DateTime.Today.AddDays(1));

        // 旧码算作本型号：基线建得起来，也不会被报成"型号不匹配"。
        Assert.Equal("B300", report!.RecipeCode);
        Assert.Equal(25, report.BaselineSampleCount);
        Assert.Equal(0, report.MismatchedRecipeCount);
        Assert.Equal(25, report.Recent.Count);
        Assert.True(report.Template.IsReliable);
        Assert.Null(report.EmptyReason);
    }

    [Fact]
    public async Task Empty_series_selector_falls_back_to_the_primary_series()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var curve = harness.Snapshot.Stations[0].Curves.First();
        var fake = new FakeRuntimeStore();
        var start = DateTime.Today.AddDays(-1).AddHours(8);

        for (var i = 0; i < 25; i++)
        {
            // 同一条曲线记录里再挂一条 X 序列，验证按序列名精确过滤。
            fake.Saved.Add(Sample(curve.Id, "压力", SeriesRole.Y, start.AddMinutes(i), $"P{i:000}", Judgement.Ok, "",
                f => f.Peak = 12,
                companion: new CurveFeature { SeriesName = "位移", Role = SeriesRole.X, Peak = 5 }));
        }

        var service = new CurveTemplateService(fake, harness.ConfigRepository);

        // 留空 → 落到主序列（Y 优先），与曲线判据的约定一致。
        var report = await service.GetBaselineAsync(
            curve.Id, null, DateTime.Today.AddDays(-2), DateTime.Today.AddDays(1));

        Assert.NotNull(report);
        Assert.Equal("压力", report!.SeriesName);
        Assert.Equal(SeriesRole.Y, report.Role);
        Assert.Equal(25, report.TotalSampleCount);
        Assert.Equal(12d, report.Template[CurveFeatureDimension.Peak]!.Center, 6);
    }

    [Fact]
    public async Task Unknown_curve_returns_null_and_missing_samples_explain_themselves()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var curve = harness.Snapshot.Stations[0].Curves.First();
        var fake = new FakeRuntimeStore();
        var service = new CurveTemplateService(fake, harness.ConfigRepository);

        Assert.Null(await service.GetBaselineAsync(999_999, null, DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1)));

        var empty = await service.GetBaselineAsync(
            curve.Id, "压力", DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1));

        Assert.NotNull(empty);
        Assert.Equal(0, empty!.BaselineSampleCount);
        Assert.False(empty.Template.IsReliable);
        Assert.Contains("没有采样数据", empty.EmptyReason);
    }

    [Fact]
    public async Task Recipe_mismatch_is_explained_rather_than_shown_as_an_empty_chart()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var curve = harness.Snapshot.Stations[0].Curves.First();
        var fake = new FakeRuntimeStore();
        var start = DateTime.Today.AddDays(-1).AddHours(8);

        for (var i = 0; i < 8; i++)
        {
            fake.Saved.Add(Sample(curve.Id, "压力", SeriesRole.Y, start.AddMinutes(i), $"B{i:000}", Judgement.Ok, "OTHER", f => f.Peak = 12));
        }

        var service = new CurveTemplateService(fake, harness.ConfigRepository);
        var report = await service.GetBaselineAsync(
            curve.Id, "压力", DateTime.Today.AddDays(-2), DateTime.Today.AddDays(1));

        Assert.NotNull(report);
        Assert.Equal(0, report!.BaselineSampleCount);
        Assert.Equal(8, report.MismatchedRecipeCount);
        Assert.Contains("没有属于本型号", report.EmptyReason);
    }

    // ---------- 夹具 ----------

    private static CurveFeature Feature(Action<CurveFeature> setup)
    {
        var feature = new CurveFeature { SeriesName = "压力", Role = SeriesRole.Y, PointCount = 50 };
        setup(feature);
        return feature;
    }

    private static CurveFeature Clone(CurveFeature source) => new()
    {
        SeriesName = source.SeriesName,
        Role = source.Role,
        PointCount = source.PointCount,
        Min = source.Min,
        MinIndex = source.MinIndex,
        Peak = source.Peak,
        PeakIndex = source.PeakIndex,
        Mean = source.Mean,
        StdDev = source.StdDev,
        Area = source.Area,
        RiseSlope = source.RiseSlope,
        HoldSlope = source.HoldSlope,
        RiseIndex = source.RiseIndex,
        RiseSpan = source.RiseSpan,
        FallRatio = source.FallRatio,
        MaxStep = source.MaxStep,
        Oscillations = source.Oscillations
    };

    /// <summary>手工构造有波动的维度基线，把打分公式与建模板逻辑隔离开。</summary>
    private static CurveDimensionBaseline Dim(CurveFeatureDimension dimension, double center, double sigma, int count = 50)
        => new()
        {
            Dimension = dimension,
            Count = count,
            Center = center,
            Sigma = sigma,
            Mean = center,
            Min = center - sigma,
            Max = center + sigma,
            IsConstant = false
        };

    /// <summary>手工构造零波动维度基线。</summary>
    private static CurveDimensionBaseline Const(CurveFeatureDimension dimension, double center, int count = 50)
        => new()
        {
            Dimension = dimension,
            Count = count,
            Center = center,
            Sigma = 0,
            Mean = center,
            IsConstant = true
        };

    private static CurveTemplate Template(params CurveDimensionBaseline[] dimensions)
        => new()
        {
            SampleCount = 50,
            ScorableDimensionCount = dimensions.Count(d => d.IsScorable),
            IsReliable = true,
            Dimensions = dimensions
        };

    /// <summary>样本不足以建立可靠基线时的模板。</summary>
    private static CurveTemplate TemplateUnreliable(params CurveDimensionBaseline[] dimensions)
        => new()
        {
            SampleCount = 5,
            ScorableDimensionCount = dimensions.Count(d => d.IsScorable),
            IsReliable = false,
            Dimensions = dimensions
        };

    private static CollectSaveRequest Sample(
        int curveDefinitionId,
        string seriesName,
        SeriesRole role,
        DateTime time,
        string palletCode,
        Judgement judgement,
        string recipeCode,
        Action<CurveFeature> setup,
        CurveFeature? companion = null)
    {
        var feature = new CurveFeature { SeriesName = seriesName, Role = role, PointCount = 50 };
        setup(feature);

        // Features 是 init-only，必须在构造时一次给全（同一条曲线可以有多条序列）。
        IReadOnlyList<CurveFeature> features = companion is null ? [feature] : [feature, companion];

        return new CollectSaveRequest
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
                    Features = features
                }
            ]
        };
    }
}
