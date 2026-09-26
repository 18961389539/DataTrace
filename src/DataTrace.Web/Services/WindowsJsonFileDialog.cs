using System.Runtime.InteropServices;
using DataTrace.Domain.Enums;

namespace DataTrace.Web.Services;

/// <summary>弹出本机的「打开文件」对话框，把选中的数据文件路径交回页面。</summary>
public interface IJsonFileDialog
{
    /// <summary>用户取消时返回 null。对话框打不开时抛出 <see cref="InvalidOperationException"/>。</summary>
    Task<string?> PickAsync(string? currentPath, DataFileFormat format);
}

/// <summary>
/// Windows 通用文件对话框。
/// </summary>
/// <remarks>
/// 采集要的是本机路径，浏览器的文件框交不出这条路径。程序和文件在同一台电脑上，
/// 所以直接让系统对话框在这台桌面上弹出。对话框必须在 STA 线程上跑，否则 comdlg32 直接失败。
/// </remarks>
public sealed class WindowsJsonFileDialog : IJsonFileDialog
{
    public Task<string?> PickAsync(string? currentPath, DataFileFormat format)
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            throw new InvalidOperationException("打不开系统文件框，请直接填写路径。");
        }

        var owner = GetForegroundWindow();
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Show(owner, currentPath, format));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    private static string? Show(IntPtr owner, string? currentPath, DataFileFormat format)
    {
        Suggest(currentPath, out var directory, out var fileName);
        var buffer = new char[1024];
        if (!string.IsNullOrEmpty(fileName))
        {
            fileName.AsSpan().CopyTo(buffer);
        }

        var filterText = format == DataFileFormat.Csv
            ? "CSV 文件 (*.csv)\0*.csv\0\0"
            : "JSON 文件 (*.json)\0*.json\0\0";
        var filter = Marshal.StringToHGlobalUni(filterText);
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var dialog = new OpenFileName
            {
                hwndOwner = owner,
                lpstrFilter = filter,
                lpstrFile = pinned.AddrOfPinnedObject(),
                nMaxFile = buffer.Length,
                lpstrInitialDir = directory,
                lpstrTitle = "选择数据文件",
                lpstrDefExt = format == DataFileFormat.Csv ? "csv" : "json",
                Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir | OfnHideReadOnly | OfnEnableHook,
                lpfnHook = Marshal.GetFunctionPointerForDelegate(Hook)
            };
            dialog.lStructSize = Marshal.SizeOf(dialog);
            if (GetOpenFileName(ref dialog))
            {
                var end = Array.IndexOf(buffer, '\0');
                return end < 0 ? new string(buffer) : new string(buffer, 0, end);
            }

            // 0 是用户取消；其它值才是对话框自己没起来。
            if (CommDlgExtendedError() != 0)
            {
                throw new InvalidOperationException("打不开系统文件框，请直接填写路径。");
            }

            return null;
        }
        finally
        {
            pinned.Free();
            Marshal.FreeHGlobal(filter);
        }
    }

    /// <summary>已有路径时从它所在的文件夹打开，并把已存在的文件名预填上。</summary>
    private static void Suggest(string? configured, out string? directory, out string? fileName)
    {
        directory = null;
        fileName = null;
        var text = (configured ?? "").Trim().Trim('"');
        if (text.Length == 0)
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(text);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        if (File.Exists(full))
        {
            fileName = Path.GetFileName(full);
            directory = Path.GetDirectoryName(full);
            return;
        }

        var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
            {
                dir = null;
                break;
            }

            dir = parent;
        }

        directory = dir;
    }

    /// <summary>浏览器经常盖住系统对话框。对话框起来时把它放到最前。</summary>
    private static IntPtr OnInit(IntPtr hdlg, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmInitDialog)
        {
            var window = GetParent(hdlg);
            if (window == IntPtr.Zero)
            {
                window = hdlg;
            }

            SetWindowPos(window, new IntPtr(HwndTopMost), 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            SetForegroundWindow(window);
        }

        return IntPtr.Zero;
    }

    private const int OfnHideReadOnly = 0x00000004;
    private const int OfnNoChangeDir = 0x00000008;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnEnableHook = 0x00000020;
    private const int OfnExplorer = 0x00080000;
    private const uint WmInitDialog = 0x0110;
    private const int HwndTopMost = -1;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;

    private static readonly Ofnhookproc Hook = OnInit;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr Ofnhookproc(IntPtr hdlg, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetOpenFileNameW")]
    private static extern bool GetOpenFileName(ref OpenFileName dialog);

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
