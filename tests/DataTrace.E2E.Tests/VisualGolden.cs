using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DataTrace.E2E.Tests;

/// <summary>
/// 像素基线（golden）比对：截图、与已提交的基线逐像素比、超阈值就失败。
/// </summary>
/// <remarks>
/// 模式由环境变量 <c>DATRACE_E2E_GOLDEN</c>（常量 <see cref="ModeVariable"/>）决定：
/// <list type="bullet">
/// <item><c>update</c>：把截图写成新基线。改完界面样式后跑一次，人眼确认过再提交。</item>
/// <item><c>compare</c>（默认）：与基线比。基线文件不存在时**判失败**而不是静默通过 ——
/// 一条什么都没比的"绿"用例比红更糟。</item>
/// <item><c>off</c>：不比。CI 目前用它：基线是在某台机器的 Edge 上录的，
/// runner 上的 Edge 版本不同就会在抗锯齿/字体度量的差别上抖出假红。
/// 想在 CI 上用，先在 windows-latest 上录一批基线再改这一行。</item>
/// </list>
/// 截图一律禁用 CSS 动画，且只挑没有实时数据的外壳区域（登录页、筛选卡、越权面板、页头），
/// 否则看板上的刷新时间会让每张图每次都不同。
/// </remarks>
[SupportedOSPlatform("windows")]   // System.Drawing 只有 Windows 后端；整套 E2E 本来就跑在本机 msedge 上。
public static class VisualGolden
{
    /// <summary>差异像素占比阈值。文本页跨浏览器版本的抗锯齿抖动通常落在 1% 以内。</summary>
    public const double MaxDiffRatio = 0.01;

    /// <summary>单像素判"不同"需要的通道差。低于它的当作渲染噪声。</summary>
    private const int ChannelTolerance = 24;

    private const string ModeVariable = "DATATRACE_E2E_GOLDEN";

    /// <summary>
    /// 基线目录：<c>tests/DataTrace.E2E.Tests/golden</c>。
    /// 不能按 AppContext.BaseDirectory 相对往上跳 —— 用 --artifacts-path 构建时产物在仓库外，
    /// 那样会把基线写进被 gitignore 的临时目录里，看着成功其实什么都没留下。
    /// </summary>
    public static string GoldenDirectory { get; } = ResolveGoldenDirectory();

    private static string ResolveGoldenDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DataTrace.slnx")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException(
                $"从 {AppContext.BaseDirectory} 往上找不到 DataTrace.slnx，无法确定基线目录。")
            : Path.Combine(directory.FullName, "tests", "DataTrace.E2E.Tests", "golden");
    }

    public static string FullPath(string name) => Path.GetFullPath(Path.Combine(GoldenDirectory, name + ".png"));

    private static string Mode => (Environment.GetEnvironmentVariable(ModeVariable) ?? "compare").Trim().ToLowerInvariant();

    public static bool Enabled => Mode != "off";

    /// <summary>
    /// 比对（或录制）一张截图。失败信息里直接给出怎么重录，省得回头翻代码。
    /// </summary>
    public static void Verify(string name, byte[] png)
    {
        var path = FullPath(name);

        if (Mode == "update")
        {
            Directory.CreateDirectory(GoldenDirectory);
            File.WriteAllBytes(path, png);
            return;
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"没有像素基线 {name}。确认界面样子后用 "
                + $"`{ModeVariable}=update` 跑一遍这组用例，看一眼 golden/ 里的图再提交。", path);
        }

        var expected = new Bitmap(path);
        try
        {
            using var actualStream = new MemoryStream(png);
            using var actual = new Bitmap(actualStream);

            if (actual.Width != expected.Width || actual.Height != expected.Height)
            {
                throw new Xunit.Sdk.XunitException(
                    $"基线 {name} 尺寸不同：期望 {expected.Width}x{expected.Height}，实际 {actual.Width}x{actual.Height}。"
                    + $"视口或布局变了；确认无误后用 {ModeVariable}=update 重录。");
            }

            var (diffPixels, worstDelta) = Compare(expected, actual);
            var ratio = (double)diffPixels / expected.Width / expected.Height;
            if (ratio > MaxDiffRatio)
            {
                throw new Xunit.Sdk.XunitException(
                    $"基线 {name} 对不上：{ratio:P2} 的像素不同（阈值 {MaxDiffRatio:P2}，最大通道差 {worstDelta}）。"
                    + $"样式真变了就用 {ModeVariable}=update 重录并检查 golden/{name}.png。");
            }
        }
        finally
        {
            expected.Dispose();
        }
    }

    private static (int DiffPixels, int WorstDelta) Compare(Bitmap expected, Bitmap actual)
    {
        var diff = 0;
        var worst = 0;
        var width = expected.Width;
        var height = expected.Height;
        var byteCount = width * height * 4;
        var left = Read32(expected);
        var right = Read32(actual);

        for (var i = 0; i < byteCount; i += 4)
        {
            var delta = Math.Max(Math.Abs(left[i] - right[i]),
                Math.Max(Math.Abs(left[i + 1] - right[i + 1]), Math.Abs(left[i + 2] - right[i + 2])));
            if (delta > ChannelTolerance)
            {
                diff++;
            }

            if (delta > worst)
            {
                worst = delta;
            }
        }

        return (diff, worst);
    }

    private static byte[] Read32(Bitmap bitmap)
    {
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buffer = new byte[Math.Abs(data.Stride) * data.Height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            return buffer;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
