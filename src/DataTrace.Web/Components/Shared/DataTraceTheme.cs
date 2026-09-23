using MudBlazor;

namespace DataTrace.Web.Components.Shared;

/// <summary>
/// 全站唯一主题：浅色底 + 中文优先字体栈。MainLayout 与 EmptyLayout 共用同一实例。
/// 语义色定义在 <see cref="DtColors"/>。
/// </summary>
public static class DataTraceTheme
{
    public static MudTheme Current { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = DtColors.Primary,
            Secondary = DtColors.Accent,
            Tertiary = DtColors.SeriesAmber,
            Info = DtColors.Info,
            Success = DtColors.Success,
            Warning = DtColors.Warning,
            Error = DtColors.Danger,
            Dark = DtColors.AppBar,

            LinesDefault = DtColors.Grid,
            LinesInputs = DtColors.Grid,
            TextPrimary = "#1f2933",
            TextSecondary = DtColors.Axis
        },
        Typography = new Typography
        {
            Default = { FontFamily = FontFamily }
        }
    };

    /// <summary>Windows 工控机与平板优先微软雅黑，缺失时再回退拉丁字体。</summary>
    public static string[] FontFamily { get; } =
    [
        "Segoe UI", "Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC", "Hiragino Sans GB",
        "Source Han Sans SC", "Noto Sans CJK SC", "Helvetica Neue", "Arial", "sans-serif"
    ];
}
