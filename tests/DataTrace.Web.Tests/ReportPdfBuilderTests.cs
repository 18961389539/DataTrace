using System.Text;
using DataTrace.Web.Services;

namespace DataTrace.Web.Tests;

/// <summary>
/// 报表 PDF 生成器的最小闭环：样本数据能产出合法 PDF（%PDF 魔数）、
/// 空 Section 的模型不炸（直通率页签经常单独有数据）、文件名带时间戳与 .pdf 后缀。
/// 排版本身在浏览器里人工验收，这里只锁"能不能生出来"。
/// </summary>
public class ReportPdfBuilderTests
{
    private static ReportPdfModel SampleModel() => new()
    {
        GeneratedAt = new DateTime(2026, 10, 11, 18, 0, 0),
        GeneratedBy = "tester",
        RangeText = "2026-10-05 ~ 2026-10-11",
        StationText = "ST010 压装",
        RecipeText = "A100 · 示例型号",
        TotalRecords = 120,
        YieldRows =
        [
            new ReportPdfModel.YieldRow("白班", 60, 55, 4, 1, 0.917),
            new ReportPdfModel.YieldRow("夜班", 60, 60, 0, 0, 1.0)
        ],
        YieldTotal = 120,
        YieldOk = 115,
        YieldAverage = 0.958,
        NgSourceText = "质量不合格 3 件，采集失败 1 件。",
        RecipeRows = [new ReportPdfModel.RecipeRow("A100 · 示例型号", 120, 115, 4, 1, 0.958)],
        DefectRows = [new ReportPdfModel.IssueRow("ST010_PD · 压装力", 4, 0.4)],
        DefectTotal = 10,
        MissingRows = [new ReportPdfModel.IssueRow("ST010_PD · 压装力", 2, 1.0)],
        MissingTotal = 2,
        WarningRows = [new ReportPdfModel.IssueRow("ST010_PD · 压装力", 2, 0.5)],
        WarningTotal = 4,
        Capability = new ReportPdfModel.CapabilityBlock(
            "压装力", "kN", "12", false,
            [new ReportPdfModel.CapabilitySegmentRow(
                "10-05 08:00 ~ 10-11 20:00", "11.5 ~ 12.5", 500,
                "1.42", "1.31", "1.28", "过程能力勉强 1.00 ≤ Cpk < 1.33")],
            ["#b45309"], 2),
        Trend = new ReportPdfModel.TrendBlock(
            "压装力", "kN", 3, "12.01", "11.9", "12.2", "10-05 08:00 ~ 10-11 20:00", false,
            [
                new ReportPdfModel.TrendSample(new DateTime(2026, 10, 5, 8, 0, 0), 12.0),
                new ReportPdfModel.TrendSample(new DateTime(2026, 10, 8, 8, 0, 0), 12.1),
                new ReportPdfModel.TrendSample(new DateTime(2026, 10, 11, 8, 0, 0), 11.95)
            ])
    };

    [Fact]
    public void Build_ProducesNonEmptyPdf()
    {
        var bytes = ReportPdfBuilder.Build(SampleModel());

        Assert.True(bytes.Length > 1000);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public void Build_WithOnlyYieldSections_StillProducesPdf()
    {
        // 直通率页签常常是唯一有数据的页签：其余 Section 全空也不能炸。
        var model = SampleModel() with
        {
            RecipeRows = [],
            DefectRows = [],
            MissingRows = [],
            WarningRows = [],
            Capability = null,
            Trend = null
        };

        var bytes = ReportPdfBuilder.Build(model);

        Assert.True(bytes.Length > 1000);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public void Build_WithLongTrend_DownsamplesAndProducesPdf()
    {
        // 趋势上限是 2 万点：验证分桶抽稀路径不炸、产出合法 PDF。
        var start = new DateTime(2026, 10, 1, 0, 0, 0);
        var series = Enumerable.Range(0, 20_000)
            .Select(i => new ReportPdfModel.TrendSample(start.AddSeconds(i), 10 + Math.Sin(i / 100.0)))
            .ToList();
        var model = SampleModel() with
        {
            Trend = new ReportPdfModel.TrendBlock("压装力", "kN", series.Count, "10.0", "9.0", "11.0",
                "10-01 00:00 ~ 10-01 05:33", true, series)
        };

        var bytes = ReportPdfBuilder.Build(model);

        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public void FileName_UsesPdfExtensionAndTimestamp()
    {
        var name = ReportPdfBuilder.FileName();

        Assert.StartsWith("datatrace_report_", name);
        Assert.EndsWith(".pdf", name);
    }
}
