using System.Globalization;
using System.Text;
using DataTrace.Web.Components.Shared;
using DataTrace.Web.Services;

namespace DataTrace.Tests;

/// <summary>CSV 导出：转义规则、中文 BOM、数值与文件名约定。</summary>
public class CsvExporterTests
{
    [Fact]
    public void Build_writes_header_then_rows_with_newline_separator()
    {
        var csv = CsvExporter.Build(
            new[] { "托盘码", "结果", "压力" },
            new[]
            {
                new[] { "P0001", "OK", "12.5" },
                new[] { "P0002", "NG", "25" }
            });

        Assert.Equal("托盘码,结果,压力\nP0001,OK,12.5\nP0002,NG,25", csv);
    }

    [Fact]
    public void Build_with_no_rows_emits_only_header()
        => Assert.Equal("a,b", CsvExporter.Build(new[] { "a", "b" }, Array.Empty<string[]>()));

    [Theory]
    [InlineData("含,逗号", "\"含,逗号\"")]
    [InlineData("含\"引号", "\"含\"\"引号\"")]
    [InlineData("第一行\n第二行", "\"第一行\n第二行\"")]
    [InlineData("回车\r", "\"回车\r\"")]
    [InlineData("普通文本", "普通文本")]
    public void Build_escapes_special_characters(string value, string expected)
    {
        var csv = CsvExporter.Build(new[] { "h" }, new[] { new[] { value } });
        Assert.Equal("h\n" + expected, csv);
    }

    [Fact]
    public void Build_renders_null_as_empty_cell()
        => Assert.Equal("a,b\n1,", CsvExporter.Build(new[] { "a", "b" }, new[] { new string?[] { "1", null } }));

    [Fact]
    public void Number_uses_invariant_culture_and_trims_trailing_zeros()
    {
        Assert.Equal("12.5", CsvExporter.Number(12.5));
        Assert.Equal("12", CsvExporter.Number(12.0));
        Assert.Equal("-0.25", CsvExporter.Number(-0.25));
        Assert.Equal("0.1235", CsvExporter.Number(0.123456));
        Assert.Equal("1000", CsvExporter.Number(1000));
    }

    [Fact]
    public void Number_does_not_leak_current_culture_decimal_separator()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // 逗号小数点会把 CSV 列切错，这里锁死不变文化输出。
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1234.5", CsvExporter.Number(1234.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ToBase64_prepends_utf8_bom_for_excel()
    {
        var base64 = CsvExporter.ToBase64("托盘码,结果\nP0001,OK");
        var bytes = Convert.FromBase64String(base64);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        var text = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("\uFEFF", text);
        Assert.Contains("P0001,OK", text);
    }

    [Fact]
    public void File_name_carries_prefix_and_timestamp()
    {
        var name = CsvExporter.FileName("采集记录");

        Assert.StartsWith("采集记录_", name);
        Assert.EndsWith(".csv", name);
        Assert.Matches(@"^采集记录_\d{8}_\d{6}\.csv$", name);
    }
}

/// <summary>SVG 折线图计算：量程、抽稀、坐标映射与刻度。</summary>
public class ChartUtilTests
{
    [Fact]
    public void ChartBox_computes_plot_area_from_padding()
    {
        var box = new ChartBox(200, 100);
        Assert.Equal(52, box.Left);
        Assert.Equal(14, box.Right);
        Assert.Equal(134, box.PlotWidth);
        Assert.Equal(62, box.PlotHeight);
    }

    [Fact]
    public void ChartBox_guards_against_degenerate_sizes()
    {
        var box = new ChartBox(40, 20);
        Assert.Equal(1, box.PlotWidth);
        Assert.Equal(1, box.PlotHeight);
    }

    [Fact]
    public void ChartBox_maps_first_and_last_index_to_plot_edges()
    {
        var box = new ChartBox(200, 100);
        Assert.Equal(52, box.X(0, 5));
        Assert.Equal(119, box.X(2, 5));
        Assert.Equal(186, box.X(4, 5));
    }

    [Fact]
    public void ChartBox_centers_single_point_series()
        => Assert.Equal(52 + (200 - 52 - 14) / 2.0, new ChartBox(200, 100).X(0, 1));

    [Fact]
    public void ChartBox_maps_y_axis_top_to_max_and_bottom_to_min()
    {
        var box = new ChartBox(200, 100);
        Assert.Equal(74, box.Y(0, 0, 10));
        Assert.Equal(12, box.Y(10, 0, 10));
        Assert.Equal(43, box.Y(5, 0, 10));
    }

    [Fact]
    public void ChartBox_y_axis_tolerates_flat_range()
    {
        var box = new ChartBox(200, 100);
        // 量程为 0 时不能除零，退化为把点画在底部。
        var y = box.Y(5, 5, 5);
        Assert.True(double.IsFinite(y));
    }

    [Fact]
    public void Range_of_empty_values_returns_unit_interval()
        => Assert.Equal((0d, 1d), ChartUtil.Range(new List<float>()));

    [Fact]
    public void Range_of_flat_positive_series_pads_by_percentage()
    {
        var (min, max) = ChartUtil.Range(new List<float> { 5f, 5f, 5f });
        Assert.Equal(4.75, min, precision: 6);
        Assert.Equal(5.25, max, precision: 6);
    }

    [Fact]
    public void Range_of_flat_zero_series_pads_by_one()
    {
        var (min, max) = ChartUtil.Range(new List<float> { 0f, 0f });
        Assert.Equal(-1d, min);
        Assert.Equal(1d, max);
    }

    [Fact]
    public void Range_returns_true_extremes_for_varying_series()
    {
        var (min, max) = ChartUtil.Range(new List<float> { 3f, -2f, 7f });
        Assert.Equal(-2d, min);
        Assert.Equal(7d, max);
    }

    [Fact]
    public void Range_works_for_double_series()
    {
        var (min, max) = ChartUtil.Range(new List<double> { 1.5, 9.5 });
        Assert.Equal(1.5, min);
        Assert.Equal(9.5, max);

        var flat = ChartUtil.Range(new List<double> { 10, 10 });
        Assert.Equal(9.5, flat.Min, precision: 6);
        Assert.Equal(10.5, flat.Max, precision: 6);
    }

    [Fact]
    public void SampleEnvelope_keeps_a_spike_that_even_sampling_would_miss()
    {
        // 尖峰放在 517：等间隔取点（maxPoints=20，步长约 52.6）命不中这个下标。
        var values = Enumerable.Repeat(10f, 1000).ToArray();
        values[517] = 999f;
        var xs = Enumerable.Range(0, 1000).Select(i => (float)i).ToArray();

        var (outX, outY, min, max) = ChartUtil.SampleEnvelope(values, xs, 20);

        Assert.Equal(10d, min, precision: 4);
        Assert.Equal(999d, max, precision: 4);
        Assert.Contains(999f, outY);
        Assert.Contains(517f, outX);
    }

    [Fact]
    public void SampleEnvelope_reports_extremes_from_before_the_decimation()
    {
        // 极值必须在抽稀之前算：否则图例上的最小/最大不是真实最值，
        // Y 轴还会按抽稀后的量程画，把尖峰裁到画框外面。
        var values = Enumerable.Repeat(5f, 500).ToArray();
        values[0] = 1f;
        values[499] = 100f;
        var xs = Enumerable.Range(0, 500).Select(i => (float)i).ToArray();

        var (_, _, min, max) = ChartUtil.SampleEnvelope(values, xs, 20);

        Assert.Equal(1d, min, precision: 4);
        Assert.Equal(100d, max, precision: 4);
    }

    [Fact]
    public void SampleEnvelope_keeps_both_ends_of_the_series()
    {
        var values = Enumerable.Range(0, 400).Select(i => (float)(i % 7)).ToArray();
        var xs = Enumerable.Range(0, 400).Select(i => (float)i).ToArray();

        var (outX, _, _, _) = ChartUtil.SampleEnvelope(values, xs, 20);

        Assert.Equal(0f, outX[0]);
        Assert.Equal(399f, outX[^1]);
        Assert.True(outX.Length <= 22, $"抽稀没起作用：{outX.Length} 个点");
    }

    [Fact]
    public void SampleEnvelope_passes_short_series_through_untouched()
    {
        var values = new[] { 3f, 1f, 4f, 1f, 5f };
        var xs = new[] { 0f, 1f, 2f, 3f, 4f };

        var (outX, outY, min, max) = ChartUtil.SampleEnvelope(values, xs, 20);

        Assert.Equal(values, outY);
        Assert.Equal(xs, outX);
        Assert.Equal(1d, min, precision: 4);
        Assert.Equal(5d, max, precision: 4);
    }

    [Fact]
    public void Polyline_emits_space_separated_coordinates()
    {
        var points = ChartUtil.Polyline(new List<float> { 0f, 1f }, new ChartBox(100, 100), 0, 1);
        Assert.Equal("52,74 86,12", points);
    }

    [Fact]
    public void Polyline_of_empty_series_is_empty()
        => Assert.Equal("", ChartUtil.Polyline(new List<float>(), new ChartBox(100, 100), 0, 1));

    [Fact]
    public void Ticks_produce_requested_number_of_even_steps()
    {
        Assert.Equal(new[] { 0d, 2.5, 5d, 7.5, 10d }, ChartUtil.Ticks(0, 10));
        Assert.Equal(new[] { 0d, 5d, 10d }, ChartUtil.Ticks(0, 10, 3));
    }

    [Fact]
    public void Ticks_coerce_invalid_count_to_two()
    {
        Assert.Equal(new[] { -1d, 1d }, ChartUtil.Ticks(-1, 1, 1));
        Assert.Equal(new[] { -1d, 1d }, ChartUtil.Ticks(-1, 1, 0));
    }

    [Theory]
    [InlineData(100000, "1.0E+5")]
    [InlineData(250, "250")]
    [InlineData(12.34, "12.3")]
    [InlineData(0.1234, "0.123")]
    [InlineData(-0.5, "-0.500")]
    public void Label_keeps_axis_caption_short(double value, string expected)
        => Assert.Equal(expected, ChartUtil.Label(value));

    [Fact]
    public void Colors_cycle_through_the_three_series_palette()
    {
        Assert.Equal(ChartColors.Primary, ChartColors.ByIndex(0));
        Assert.Equal(ChartColors.Secondary, ChartColors.ByIndex(1));
        Assert.Equal(ChartColors.Third, ChartColors.ByIndex(2));
        Assert.Equal(ChartColors.Primary, ChartColors.ByIndex(3));
    }

    [Fact]
    public void Judgement_color_switches_to_alert_for_ng()
    {
        Assert.Equal(ChartColors.Alert, ChartColors.ForJudgement(true));
        Assert.Equal(ChartColors.Primary, ChartColors.ForJudgement(false));
    }

    [Fact]
    public void LineSeries_defaults_to_primary_color()
    {
        var series = new LineSeries { Name = "压力", Values = [1f] };
        Assert.Equal(ChartColors.Primary, series.Color);
        Assert.Null(series.XValues);
        Assert.Null(series.Unit);
    }

    [Fact]
    public void LineSeries_can_carry_custom_x_axis_and_unit()
    {
        var series = new LineSeries
        {
            Name = "位移压力",
            Values = [1f, 2f],
            XValues = [0f, 1f],
            Color = ChartColors.Third,
            Unit = "kN"
        };

        Assert.Equal("kN", series.Unit);
        Assert.Equal(ChartColors.Third, series.Color);
        Assert.Equal(new[] { 0f, 1f }, series.XValues!);
    }
}

/// <summary>
/// 界面文案映射。顶栏面包屑与"权限不足"页共用同一张路径→页名表，
/// 之前那张表埋在 MainLayout 里，改名表就等于只改一处。
/// </summary>
public class DisplayLabelsTests
{
    [Theory]
    [InlineData("", "实时看板")]
    [InlineData("/", "实时看板")]
    [InlineData("query", "数据查询")]
    [InlineData("query?from=2026-09-15", "数据查询")]
    [InlineData("query/202609/12", "数据查询 / 记录明细")]
    [InlineData("reports", "报表")]
    [InlineData("config/plc", "PLC 连接")]
    [InlineData("config/stations", "工站配置")]
    [InlineData("users", "用户")]
    [InlineData("logs", "审计日志")]
    [InlineData("simulate", "PLC 仿真")]
    public void PageTitle_maps_known_routes(string path, string expected)
        => Assert.Equal(expected, DisplayLabels.PageTitle(path));

    [Theory]
    [InlineData("bogus")]
    [InlineData("account/logout")]
    [InlineData(null)]
    public void PageTitle_returns_null_for_unknown_so_callers_can_decide(string? path)
        => Assert.Null(DisplayLabels.PageTitle(path));

    [Fact]
    public void PageTitle_is_case_insensitive()
        => Assert.Equal("用户", DisplayLabels.PageTitle("USERS"));

    [Fact]
    public void LimitRange_names_both_bounds()
        => Assert.Equal("5 ~ 20", DisplayLabels.LimitRange(5, 20));

    [Fact]
    public void LimitRange_says_unconfigured_only_when_both_bounds_are_absent()
        => Assert.Equal("未配置", DisplayLabels.LimitRange(null, null));

    [Fact]
    public void LimitRange_uses_em_dash_for_a_missing_upper_bound()
        => Assert.Equal("5 ~ —", DisplayLabels.LimitRange(5, null));

    [Fact]
    public void LimitRange_uses_em_dash_for_a_missing_lower_bound()
        => Assert.Equal("— ~ 60", DisplayLabels.LimitRange(null, 60));

    [Fact]
    public void LimitRange_formats_invariantly()
        => Assert.Equal("0.5 ~ 1234.568", DisplayLabels.LimitRange(0.5, 1234.5678));
}
