using MudBlazor;

namespace DataTrace.Web.Components.Shared;

/// <summary>
/// 全站唯一主题：浅色/深色调色板 + 中文优先字体栈。MainLayout 与 EmptyLayout 共用同一实例。
/// 语义色定义在 <see cref="DtColors"/>。
/// </summary>
public static class DataTraceTheme
{
    public static MudTheme Current { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = DtColors.Primary,
            PrimaryContrastText = "#ffffff",
            Secondary = DtColors.Accent,
            Tertiary = DtColors.SeriesAmber,
            Info = DtColors.Info,
            Success = DtColors.Success,
            Warning = DtColors.Warning,
            Error = DtColors.Danger,
            Dark = DtColors.AppBar,

            // 顶栏走主题而不是组件上的 Color="Dark"：那样会把 AppbarBackground 变成死配置，
            // 暗色下顶栏（#0d151c）和紧挨着的抽屉（#18232d）成为两种深色。
            AppbarBackground = DtColors.AppBar,
            AppbarText = "#ffffff",

            LinesDefault = DtColors.Grid,
            LinesInputs = DtColors.Grid,
            TextPrimary = "#1f2933",
            TextSecondary = DtColors.Axis
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#8ab4f8",
            PrimaryContrastText = "#142333",
            Secondary = "#80cbc4",
            Tertiary = "#ffca80",
            Info = "#90caf9",
            Success = "#81c784",
            Warning = "#ffcc80",
            Error = "#ef9a9a",
            Dark = "#0d151c",

            Background = "#111820",
            BackgroundGray = "#151e27",
            Surface = "#1a252f",
            DrawerBackground = "#18232d",
            DrawerText = "#e5edf4",
            DrawerIcon = "#aab8c5",
            AppbarBackground = "#18232d",
            AppbarText = "#e5edf4",
            TextPrimary = "#e5edf4",
            TextSecondary = "#aab8c5",
            LinesDefault = "#33424f",
            LinesInputs = "#465866",
            Divider = "#33424f"
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
