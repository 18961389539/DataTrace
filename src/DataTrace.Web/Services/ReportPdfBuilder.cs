using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;

namespace DataTrace.Web.Services;

/// <summary>
/// 报表页整份 PDF 的数据源。全部是排版就绪的扁平数据（数字已格式化成字符串、颜色已算好），
/// 生成器只管画，不懂业务 —— 这样报表页改口径时只动映射，不碰排版。
/// </summary>
public sealed record ReportPdfModel
{
    public DateTime GeneratedAt { get; init; }
    public string GeneratedBy { get; init; } = "";
    public string RangeText { get; init; } = "";
    public string StationText { get; init; } = "全部工站";
    public string RecipeText { get; init; } = "全部型号";
    public int TotalRecords { get; init; }

    public IReadOnlyList<YieldRow> YieldRows { get; init; } = [];
    public int YieldTotal { get; init; }
    public int YieldOk { get; init; }
    public double YieldAverage { get; init; }

    /// <summary>NG 来源说明（质量不合格 / 采集失败各多少件）。没有 NG 时留空，PDF 里就不渲染这一行。</summary>
    public string NgSourceText { get; init; } = "";

    public IReadOnlyList<RecipeRow> RecipeRows { get; init; } = [];

    public IReadOnlyList<IssueRow> DefectRows { get; init; } = [];
    public int DefectTotal { get; init; }
    public IReadOnlyList<IssueRow> MissingRows { get; init; } = [];
    public int MissingTotal { get; init; }
    public IReadOnlyList<IssueRow> WarningRows { get; init; } = [];
    public int WarningTotal { get; init; }

    public CapabilityBlock? Capability { get; init; }
    public TrendBlock? Trend { get; init; }

    /// <summary>一行班次直通率。Yield 是 0~1 的小数，渲染时按 P1 格式化并按阈值着色。</summary>
    public sealed record YieldRow(string Label, int Total, int Ok, int Ng, int None, double Yield);

    public sealed record RecipeRow(string Recipe, int Total, int Ok, int Ng, int Pending, double PassRate);

    /// <summary>不良/预警/没读到数共用一行：名称 + 次数 + 占比（0~1）。</summary>
    public sealed record IssueRow(string Name, int Count, double Share);

    /// <summary>一段过程能力，指标全部是报表页用 <c>SpcCalculator.Format</c> 排好版的字符串。</summary>
    public sealed record CapabilitySegmentRow(
        string Range, string Limits, int Count, string Cp, string Cpk, string Ppk, string Verdict);

    /// <summary>VerdictColors 与段一一对应（按 SpcVerdict 阈值着色，空串表示不着色）。</summary>
    public sealed record CapabilityBlock(
        string TagName, string? Unit, string Target, bool Truncated,
        IReadOnlyList<CapabilitySegmentRow> Segments, IReadOnlyList<string> VerdictColors, int ViolationCount);

    public sealed record TrendSample(DateTime Time, double Value);

    public sealed record TrendBlock(
        string TagName, string? Unit, int Points, string Average, string Min, string Max,
        string Range, bool Truncated, IReadOnlyList<TrendSample> Series);
}

/// <summary>
/// 报表页「导出 PDF 报告」的生成器：把当前筛选区间的直通率、型号汇总、不良与预警榜、
/// 过程能力与趋势摘要排成一份 A4 存档文档。
/// </summary>
/// <remarks>
/// 字体用微软雅黑：部署目标是 Windows 车间机（系统必带）。若未来跑进 Linux 容器，
/// 需要给容器装 CJK 字体，否则中文会渲染成方块（生成不报错，只是字没了）。
/// QuestPDF Community 许可必须在任何生成动作之前声明，放在静态构造里保证测试与运行时都覆盖。
/// </remarks>
public static class ReportPdfBuilder
{
    private const string CjkFont = "Microsoft YaHei";

    /// <summary>CJK 字体候选，按优先级排列；启动时解析出本机第一个真实可用的家族。</summary>
    private static readonly string[] CjkFontCandidates =
    [
        "Microsoft YaHei", "Noto Sans CJK SC", "WenQuanYi Zen Hei", "Noto Sans", "SimSun"
    ];

    /// <summary>本机解析出的字体家族：注册或系统里真实存在的那一个（回退链里只要有一个缺失就会终止生成）。</summary>
    private static string _cjkFontFamily = CjkFont;

    private const string ColorText = "#111827";
    private const string ColorMuted = "#64748b";
    private const string ColorBorder = "#cbd5e1";
    private const string ColorRowLine = "#e2e8f0";
    private const string ColorHeaderBg = "#f1f5f9";
    private const string ColorHeadBand = "#eff6ff";
    private const string ColorGood = "#15803d";
    private const string ColorWarn = "#b45309";
    private const string ColorBad = "#dc2626";
    private const string ColorLine = "#2563eb";

    static ReportPdfBuilder()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        // 2026.9 起默认不扫描系统字体、缺字直接终止生成（不再静默替换）。
        // 中文必须走 CJK 字体：Windows 显式注册微软雅黑（部署目标系统必带）；
        // Linux（CI/容器）依赖系统字体发现，需装有 Noto Sans CJK，缺字时生成会抛出明确清单。
        QuestPDF.Settings.UseSystemFonts = true;
        RegisterWindowsCjkFont();
        _cjkFontFamily = ResolveCjkFontFamily();
    }

    /// <summary>从已注册与系统字体里选出第一个真实可用的 CJK 候选；一个都没有就退回默认名，让生成阶段的异常给出完整清单。</summary>
    private static string ResolveCjkFontFamily()
    {
        foreach (var candidate in CjkFontCandidates)
        {
            var registered = FontManager.GetRegisteredFonts()
                .Any(f => string.Equals(f.FamilyName, candidate, StringComparison.OrdinalIgnoreCase));
            if (registered)
            {
                return candidate;
            }

            var installed = FontManager.GetSystemFonts()
                .Any(f => string.Equals(f.FamilyName, candidate, StringComparison.OrdinalIgnoreCase));
            if (installed)
            {
                return candidate;
            }
        }

        return CjkFont;
    }

    /// <summary>
    /// 显式注册 Windows 自带的微软雅黑。系统字体开关在部分环境下扫不到 .ttc 集合文件，
    /// 手动注册一次最稳；全失败时不吞异常 —— 生成阶段会抛出缺字体异常并给出清单。
    /// </summary>
    private static void RegisterWindowsCjkFont()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var name in new[] { "msyh.ttc", "msyh.ttf", "simsun.ttc" })
        {
            var path = Path.Combine(windowsDirectory, "Fonts", name);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                FontManager.RegisterFontFromFile(path);
                return;
            }
            catch
            {
                // 试下一个候选；全失败时交给生成阶段的缺字体异常兜底。
            }
        }
    }

    public static string FileName() => $"datatrace_report_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";

    public static byte[] Build(ReportPdfModel model)
    {
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(x => x
                    .FontFamily(_cjkFontFamily)
                    .FontSize(9.5f).LineHeight(1.45f).FontColor(ColorText));

                page.Footer().Row(row =>
                {
                    row.RelativeItem().Text(t =>
                    {
                        t.Span("第 ").FontColor(ColorMuted).FontSize(8f);
                        t.CurrentPageNumber().FontColor(ColorMuted).FontSize(8f);
                        t.Span(" / ").FontColor(ColorMuted).FontSize(8f);
                        t.TotalPages().FontColor(ColorMuted).FontSize(8f);
                        t.Span(" 页 · DataTrace").FontColor(ColorMuted).FontSize(8f);
                    });
                });

                page.Content().PaddingTop(4).Column(column =>
                {
                    column.Spacing(12);
                    RenderHead(column, model);
                    RenderYield(column, model);
                    RenderRecipe(column, model);
                    RenderIssues(column, model);
                    RenderCapability(column, model);
                    RenderTrend(column, model);
                });
            });
        }).GeneratePdf();
    }

    // ---------- 各节 ----------

    private static void RenderHead(ColumnDescriptor column, ReportPdfModel model)
    {
        column.Item().Background(ColorHeadBand).Padding(10).Column(head =>
        {
            head.Spacing(3);
            head.Item().Text("DataTrace 质检报告")
                .FontSize(16f).SemiBold();
            head.Item().Text(t =>
            {
                t.Span($"统计区间 {model.RangeText}").FontColor(ColorMuted).FontSize(8.5f);
                t.Span($"　·　工站 {model.StationText}").FontColor(ColorMuted).FontSize(8.5f);
                t.Span($"　·　型号 {model.RecipeText}").FontColor(ColorMuted).FontSize(8.5f);
                t.Span($"　·　共 {model.TotalRecords:N0} 条记录").FontColor(ColorMuted).FontSize(8.5f);
            });
            head.Item().Text(t =>
            {
                t.Span($"生成于 {model.GeneratedAt:yyyy-MM-dd HH:mm:ss}").FontColor(ColorMuted).FontSize(8.5f);
                if (!string.IsNullOrWhiteSpace(model.GeneratedBy))
                {
                    t.Span($"　·　生成人 {model.GeneratedBy}").FontColor(ColorMuted).FontSize(8.5f);
                }
            });
        });
    }

    private static void RenderYield(ColumnDescriptor column, ReportPdfModel model)
    {
        column.Item().Text("1. 直通率").FontSize(12f).SemiBold();

        column.Item().Text(t =>
        {
            t.Span($"区间合计 {model.YieldTotal:N0} 件，OK {model.YieldOk:N0} 件，平均直通率 {model.YieldAverage:P1}" +
                   "（任一站不合格整件算不合格；按末站完成时间落在所选区间统计）。").FontColor(ColorMuted).FontSize(8.5f);
        });

        if (model.NgSourceText.Length > 0)
        {
            column.Item().Text(t =>
            {
                t.Span(model.NgSourceText).FontColor(ColorMuted).FontSize(8.5f);
            });
        }

        RenderTable(
            column,
            [("班次", 2.2f), ("总数（件）", 1f), ("OK", 1f), ("NG", 1f), ("未判定", 1f), ("直通率", 1.2f)],
            model.YieldRows.Select(r => new PdfRow(
                [r.Label, r.Total.ToString("N0"), r.Ok.ToString("N0"), r.Ng.ToString("N0"), r.None.ToString("N0"), r.Yield.ToString("P1")],
                AccentColumn: 5,
                AccentColor: YieldColor(r.Yield))).ToList());
    }

    private static void RenderRecipe(ColumnDescriptor column, ReportPdfModel model)
    {
        if (model.RecipeRows.Count == 0)
        {
            return;
        }

        column.Item().Text("2. 按产品型号").FontSize(12f).SemiBold();
        RenderTable(
            column,
            [("型号", 2.2f), ("产量（件）", 1f), ("OK", 1f), ("NG", 1f), ("未判定", 1f), ("直通率", 1.2f)],
            model.RecipeRows.Select(r => new PdfRow(
                [r.Recipe, r.Total.ToString("N0"), r.Ok.ToString("N0"), r.Ng.ToString("N0"), r.Pending.ToString("N0"), r.PassRate.ToString("P1")],
                AccentColumn: 5,
                AccentColor: YieldColor(r.PassRate))).ToList());
    }

    private static void RenderIssues(ColumnDescriptor column, ReportPdfModel model)
    {
        if (model.DefectRows.Count == 0 && model.MissingRows.Count == 0 && model.WarningRows.Count == 0)
        {
            return;
        }

        column.Item().Text("3. 不良与预警 Top N").FontSize(12f).SemiBold();

        if (model.DefectRows.Count > 0)
        {
            RenderTable(
                column,
                [("点位（超规格）", 3.5f), ("次数", 1f), ("占比", 1f)],
                model.DefectRows.Select(r => new PdfRow([r.Name, r.Count.ToString("N0"), r.Share.ToString("P1")])).ToList());
            column.Item().Text(t =>
            {
                t.Span($"区间共 {model.DefectTotal:N0} 次超规格，占比以全部次数为分母。").FontColor(ColorMuted).FontSize(8f);
            });
        }

        if (model.MissingRows.Count > 0)
        {
            column.Item().PaddingTop(4).Text("没读到数").FontSize(10f).SemiBold();
            RenderTable(
                column,
                [("点位（没读到数）", 3.5f), ("次数", 1f), ("占比", 1f)],
                model.MissingRows.Select(r => new PdfRow([r.Name, r.Count.ToString("N0"), r.Share.ToString("P1")])).ToList());
            column.Item().Text(t =>
            {
                t.Span($"区间共 {model.MissingTotal:N0} 次没读到数，不计入超规格榜。").FontColor(ColorMuted).FontSize(8f);
            });
        }

        if (model.WarningRows.Count > 0)
        {
            column.Item().PaddingTop(4).Text("预警 Top N").FontSize(10f).SemiBold();
            RenderTable(
                column,
                [("点位（进入预警带）", 3.5f), ("次数", 1f), ("占比", 1f)],
                model.WarningRows.Select(r => new PdfRow([r.Name, r.Count.ToString("N0"), r.Share.ToString("P1")])).ToList());
            column.Item().Text(t =>
            {
                t.Span($"区间共 {model.WarningTotal:N0} 次进入预警带，落在黄区、尚未判废。").FontColor(ColorMuted).FontSize(8f);
            });
        }
    }

    private static void RenderCapability(ColumnDescriptor column, ReportPdfModel model)
    {
        var spc = model.Capability;
        if (spc is null)
        {
            return;
        }

        column.Item().Text("4. 过程能力（I-MR）").FontSize(12f).SemiBold();
        column.Item().Text(t =>
        {
            t.Span($"点位 {spc.TagName}").FontColor(ColorMuted).FontSize(8.5f);
            if (!string.IsNullOrWhiteSpace(spc.Unit))
            {
                t.Span($"（单位 {spc.Unit}）").FontColor(ColorMuted).FontSize(8.5f);
            }
            t.Span($"　·　目标值 {spc.Target}").FontColor(ColorMuted).FontSize(8.5f);
            if (spc.Truncated)
            {
                t.Span("　·　采样点超上限，只统计最近一批").FontColor(ColorMuted).FontSize(8.5f);
            }
        });

        RenderTable(
            column,
            [("段", 0.6f), ("时间范围", 1.6f), ("规格限", 1.2f), ("点数", 0.7f), ("Cp", 0.7f), ("Cpk", 0.7f), ("Ppk", 0.7f), ("结论", 2f)],
            spc.Segments.Select((s, i) =>
            {
                var color = i < spc.VerdictColors.Count ? spc.VerdictColors[i] : "";
                return new PdfRow(
                    [(i + 1).ToString(), s.Range, s.Limits, s.Count.ToString("N0"), s.Cp, s.Cpk, s.Ppk, s.Verdict],
                    AccentColumn: 7,
                    AccentColor: color);
            }).ToList());

        column.Item().Text(t =>
        {
            t.Span(spc.ViolationCount > 0
                ? $"区间内触发判异 {spc.ViolationCount} 条（Nelson 规则 1–5：超 3σ、同侧 9 点、6 点趋势、14 点振荡、3 中 2 超 2σ）。"
                : "区间内没有触发任何判异准则。").FontColor(ColorMuted).FontSize(8f);
        });
    }

    private static void RenderTrend(ColumnDescriptor column, ReportPdfModel model)
    {
        var trend = model.Trend;
        if (trend is null || trend.Series.Count == 0)
        {
            return;
        }

        column.Item().Text("5. 参数趋势摘要").FontSize(12f).SemiBold();
        column.Item().Text(t =>
        {
            t.Span($"点位 {trend.TagName}").FontColor(ColorMuted).FontSize(8.5f);
            if (!string.IsNullOrWhiteSpace(trend.Unit))
            {
                t.Span($"（单位 {trend.Unit}）").FontColor(ColorMuted).FontSize(8.5f);
            }
            t.Span($"　·　采样点 {trend.Points:N0}").FontColor(ColorMuted).FontSize(8.5f);
            t.Span($"　·　均值 {trend.Average}　最小 {trend.Min}　最大 {trend.Max}").FontColor(ColorMuted).FontSize(8.5f);
            if (trend.Range.Length > 0)
            {
                t.Span($"　·　{trend.Range}").FontColor(ColorMuted).FontSize(8.5f);
            }
        });

        if (trend.Truncated)
        {
            column.Item().Text(t =>
            {
                t.Span("区间内采样点超过单次上限，本图只画最近一批样本。").FontColor(ColorWarn).FontSize(8f);
            });
        }

        // 趋势图走官方 SkiaSharp 集成的动态位图方案：payload 给出设备像素尺寸（高分屏是 2x），
        // 按可用空间等比缩放后手绘折线，保证 180pt 高度内清晰不失真。
        column.Item().PaddingTop(2).Height(180).Image(payload =>
        {
            var availableWidth = (float)payload.AvailableSpace.Width;
            var availableHeight = (float)payload.AvailableSpace.Height;
            using var bitmap = new SKBitmap(payload.ImageSize.Width, payload.ImageSize.Height);
            using var canvas = new SKCanvas(bitmap);
            var scale = availableWidth > 0 ? payload.ImageSize.Width / availableWidth : 1f;
            canvas.Scale(scale);
            DrawTrendChart(canvas, availableWidth, availableHeight, trend.Series);
            canvas.Flush();
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 90);
            return png.ToArray();
        });
    }

    // ---------- 通用表格 ----------

    /// <summary>一行单元格；AccentColumn/AccentColor 给"直通率/结论"这类需要着色的列用。</summary>
    private sealed record PdfRow(IReadOnlyList<string> Cells, int? AccentColumn = null, string? AccentColor = null);

    private static void RenderTable(ColumnDescriptor column, (string Label, float Width)[] columns, IReadOnlyList<PdfRow> rows)
    {
        column.Item().Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                foreach (var (_, width) in columns)
                {
                    cols.RelativeColumn(width);
                }
            });

            table.Header(header =>
            {
                foreach (var (label, _) in columns)
                {
                    header.Cell().Background(ColorHeaderBg).Border(0.5f).BorderColor(ColorBorder).Padding(4)
                        .Text(t => t.Span(label).SemiBold().FontSize(8.5f));
                }
            });

            foreach (var row in rows)
            {
                for (var i = 0; i < row.Cells.Count; i++)
                {
                    var cell = table.Cell().BorderBottom(0.5f).BorderColor(ColorRowLine)
                        .PaddingVertical(3).PaddingHorizontal(4);
                    var value = row.Cells[i];
                    if (row.AccentColumn == i && !string.IsNullOrEmpty(row.AccentColor))
                    {
                        cell.Text(t => t.Span(value).FontSize(8.5f).SemiBold().FontColor(row.AccentColor));
                    }
                    else
                    {
                        cell.Text(t => t.Span(value).FontSize(8.5f));
                    }
                }
            }
        });
    }

    /// <summary>直通率着色阈值与报表页 YieldColor 一致：≥99% 绿、≥95% 黄、其余红。</summary>
    private static string YieldColor(double value) => value switch
    {
        >= 0.99 => ColorGood,
        >= 0.95 => ColorWarn,
        _ => ColorBad
    };

    // ---------- 趋势图（Skia 手绘折线） ----------

    /// <summary>
    /// 在给定画布上画趋势折线：边框 + 中位参考线 + 采样折线。
    /// 均值/最小/最大已经写在图上方的统计行里，画布内不再叠文字（SkiaSharp 文本 API 版本差异大，纯形状最稳）。
    /// </summary>
    private static void DrawTrendChart(SKCanvas canvas, float width, float height, IReadOnlyList<ReportPdfModel.TrendSample> points)
    {
        canvas.Clear(SKColors.White);

        var left = 8f;
        var top = 8f;
        var right = width - 8f;
        var bottom = height - 8f;
        if (right - left < 20 || bottom - top < 20 || points.Count == 0)
        {
            return;
        }

        double min = points.Min(p => p.Value);
        double max = points.Max(p => p.Value);
        if (Math.Abs(max - min) < 1e-9)
        {
            min -= 0.5;
            max += 0.5;
        }
        var pad = (max - min) * 0.12;
        min -= pad;
        max += pad;

        using var borderPaint = new SKPaint { Color = SKColor.Parse(ColorBorder), IsStroke = true, StrokeWidth = 1f };
        canvas.DrawRect(left, top, right - left, bottom - top, borderPaint);

        using var gridPaint = new SKPaint { Color = SKColor.Parse(ColorRowLine), IsStroke = true, StrokeWidth = 1f };
        var midY = top + (bottom - top) / 2;
        canvas.DrawLine(left, midY, right, midY, gridPaint);

        // 一屏最多 900 个像素列：超长序列按列分桶取均值，形状不变、开销有界。
        const int MaxColumns = 900;
        var step = Math.Max(1, (int)Math.Ceiling(points.Count / (double)MaxColumns));
        var plotWidth = right - left;
        var plotHeight = bottom - top;
        var span = max - min;

        using var linePaint = new SKPaint { Color = SKColor.Parse(ColorLine), IsStroke = true, StrokeWidth = 1.5f, IsAntialias = true };
        SKPoint? prev = null;
        for (var i = 0; i < points.Count; i += step)
        {
            var end = Math.Min(i + step, points.Count);
            double sum = 0;
            for (var j = i; j < end; j++)
            {
                sum += points[j].Value;
            }

            var v = sum / (end - i);
            var x = left + plotWidth * (points.Count == 1 ? 0.5f : (float)i / (points.Count - 1));
            var y = top + plotHeight * (float)(1 - (v - min) / span);
            var point = new SKPoint(x, y);
            if (prev is { } last)
            {
                canvas.DrawLine(last, point, linePaint);
            }
            prev = point;
        }
    }
}
