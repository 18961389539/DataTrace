namespace DataTrace.Web.Components.Shared;

/// <summary>
/// 全站语义色，唯一取值来源：Mud 主题、图表序列色与 app.css 的 --dt-* 变量都对齐到这里。
/// 产线看板长时间盯屏，语义色一律取深色档，保证白底正文的对比度。
/// app.css 里的 --dt-chart-* 与本类的 <see cref="Axis"/>/<see cref="Grid"/>/<see cref="PlotBackground"/> 同值，改动需同步。
/// 单独成文件是因为测试工程只按文件链接取用 <see cref="ChartUtil"/>，不能把 MudBlazor 依赖带进去。
/// </summary>
public static class DtColors
{
    public const string Primary = "#1b6ec2";
    public const string Accent = "#0f7a8c";
    public const string Info = "#0288d1";
    public const string Success = "#2e7d32";
    public const string Warning = "#b36a00";
    public const string Danger = "#c62828";

    /// <summary>图表第三条序列色，与 Success 同值：曲线与状态用同一支绿，读图时不会分裂。</summary>
    public const string SeriesGreen = Success;

    public const string SeriesAmber = "#e07b00";

    public const string AppBar = "#22303f";
    public const string Axis = "#4a5568";
    public const string Grid = "#e3e8ee";
    public const string PlotBackground = "#f7f9fb";
}
