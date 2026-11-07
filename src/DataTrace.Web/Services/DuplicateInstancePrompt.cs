using System.Runtime.InteropServices;

namespace DataTrace.Web.Services;

/// <summary>
/// 第二个实例抢不到数据目录时，给坐在机器前的人一个看得到的错误框。
/// </summary>
/// <remarks>
/// 只写日志时，双击 exe 或隐藏控制台启动的人看不到失败原因，会以为程序没反应。
/// Windows 服务在 Session 0，没有人能点这个框，仍然只记日志。
/// </remarks>
public static class DuplicateInstancePrompt
{
    public static string FormatMessage(string dataRoot, int? holderPid)
    {
        var who = holderPid is { } pid ? pid.ToString() : "未知";
        return "已有 DataTrace 正在使用这个数据目录，本进程不再启动。\n\n"
            + $"数据目录：{dataRoot}\n"
            + $"占用进程：{who}\n\n"
            + "请先退出已有实例（通知区域图标右键“退出”，或停止 Windows 服务）后再试。";
    }

    public static void Show(string dataRoot, int? holderPid)
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            return;
        }

        MessageBox(
            IntPtr.Zero,
            FormatMessage(dataRoot, holderPid),
            "DataTrace",
            MbOk | MbIconError | MbSetForeground | MbTopMost);
    }

    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;
    private const uint MbSetForeground = 0x00010000;
    private const uint MbTopMost = 0x00040000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
