using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace DataTrace.Web.Services;

/// <summary>
/// 在 Windows 通知区域放一个图标：双击打开页面，右键可以退出。
/// </summary>
/// <remarks>
/// 采集进程没有自己的窗口。控制台被隐藏、或者从资源管理器直接启动时，现场只能去任务管理器里找进程。
/// 图标挂在单独的消息线程上，挂不上（没有桌面、图标文件读不出）就记一行日志，网站照常听端口。
/// Windows 服务跑在 Session 0，调用方不应进来。
/// </remarks>
public sealed class TrayIconHost : IDisposable
{
    private const int TipLimit = 127;
    private const int WmDestroy = 0x0002;
    private const int WmNull = 0x0000;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmAppExit = 0x8001;
    private const int CallbackMessage = 0x0414;
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint NifInfo = 0x10;
    private const int NotifyIconVersion = 3;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;
    private const int IdiApplication = 32512;
    private const uint WsPopup = 0x80000000;
    private const uint MfString = 0;
    private const uint MfGrayed = 0x1;
    private const uint MfSeparator = 0x800;
    private const uint TpmLeftAlign = 0;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmBottomAlign = 0x0020;
    private const uint TpmReturnCmd = 0x0100;
    private const uint NiifInfo = 0x1;
    private const uint NiifNoSound = 0x10;
    private const nuint OpenCommand = 1;
    private const nuint ExitCommand = 2;

    private readonly string? _browserUrl;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WndProc _wndProc;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _destroyIcon;
    private CancellationTokenRegistration _stopping;
    private int _disposed;

    private TrayIconHost(string? browserUrl, IHostApplicationLifetime lifetime, ILogger logger)
    {
        _browserUrl = browserUrl;
        _lifetime = lifetime;
        _logger = logger;
        _wndProc = WindowProc;
        _thread = new Thread(Run)
        {
            IsBackground = false,
            Name = "DataTrace.Tray"
        };
    }

    /// <summary>交互式 Windows 进程、且没有被配置关掉时才挂图标。服务进程在 Session 0，不算。</summary>
    public static bool ShouldShow(bool isWindows, bool userInteractive, bool isWindowsService, bool enabled)
        => isWindows && userInteractive && !isWindowsService && enabled;

    /// <summary>鼠标悬停文字。通知区域最多收 127 个字符，超长截断，避免 Shell_NotifyIcon 直接失败。</summary>
    public static string FormatTip(string? browserUrl)
    {
        var tip = string.IsNullOrWhiteSpace(browserUrl) ? "DataTrace" : "DataTrace " + browserUrl.Trim();
        return tip.Length <= TipLimit ? tip : string.Concat(tip.AsSpan(0, TipLimit - 1), "…");
    }

    /// <summary>图标挂上后返回可释放的宿主；失败返回 null，调用方继续跑网站。</summary>
    public static TrayIconHost? TryStart(string? browserUrl, IHostApplicationLifetime lifetime, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var host = new TrayIconHost(browserUrl, lifetime, logger);
        // STA 只在 Windows 上有意义；放在 IsWindows 判断之后，避免在其它平台上编译期被当成必达调用。
        host._thread.SetApartmentState(ApartmentState.STA);
        host._thread.Start();
        if (!host._ready.Wait(TimeSpan.FromSeconds(5)) || host._hwnd == IntPtr.Zero)
        {
            logger.LogWarning("通知区域图标没有在时限内挂上，网页服务继续运行。");
            host.Dispose();
            return null;
        }

        logger.LogInformation("通知区域图标已显示。");
        return host;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopping.Dispose();
        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WmAppExit, IntPtr.Zero, IntPtr.Zero);
        }

        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(3));
        }

        _ready.Dispose();
    }

    private void Run()
    {
        try
        {
            var instance = GetModuleHandle(null);
            var klass = new WndClass
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = instance,
                lpszClassName = "DataTrace.Tray.Window"
            };
            if (RegisterClass(ref klass) == 0 && Marshal.GetLastWin32Error() != 1410)
            {
                _logger.LogWarning("注册托盘窗口类失败（{Error}）。", Marshal.GetLastWin32Error());
                return;
            }

            // 不 ShowWindow：没有任务栏按钮。消息窗口（HWND_MESSAGE）在部分系统上收不到托盘回调。
            _hwnd = CreateWindowEx(0, klass.lpszClassName, "DataTrace", WsPopup, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                _logger.LogWarning("创建托盘窗口失败（{Error}）。", Marshal.GetLastWin32Error());
                return;
            }

            _icon = LoadTrayIcon();
            var data = new NotifyIconData
            {
                cbSize = Marshal.SizeOf<NotifyIconData>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NifMessage | NifIcon | NifTip | NifInfo,
                uCallbackMessage = CallbackMessage,
                hIcon = _icon,
                szTip = FormatTip(_browserUrl),
                szInfo = "双击图标打开页面，右键可以退出。",
                szInfoTitle = "DataTrace 正在运行",
                dwInfoFlags = NiifInfo | NiifNoSound
            };
            if (!ShellNotifyIcon(NimAdd, ref data))
            {
                _logger.LogWarning("添加通知区域图标失败（{Error}）。", Marshal.GetLastWin32Error());
                CleanupWindow();
                return;
            }

            data.uTimeoutOrVersion = NotifyIconVersion;
            data.uFlags = 0;
            ShellNotifyIcon(NimSetVersion, ref data);
            _stopping = _lifetime.ApplicationStopping.Register(static state =>
            {
                var self = (TrayIconHost)state!;
                if (self._hwnd != IntPtr.Zero)
                {
                    PostMessage(self._hwnd, WmAppExit, IntPtr.Zero, IntPtr.Zero);
                }
            }, this);

            // 循环要一直转到进程退出。先放行启动方，否则它会一直等到超时再把图标拆掉。
            _ready.Set();
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "通知区域图标中途退出，网页服务继续运行。");
        }
        finally
        {
            RemoveIcon();
            CleanupWindow();
            _ready.Set();
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            var mouse = unchecked((int)lParam.ToInt64());
            if (mouse == WmLButtonDblClk)
            {
                OpenBrowser();
            }
            else if (mouse == WmRButtonUp)
            {
                ShowMenu(hWnd);
            }

            return IntPtr.Zero;
        }

        if (msg == WmAppExit)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        if (msg == WmDestroy)
        {
            PostQuitMessage(0);
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu(IntPtr hWnd)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var openFlags = string.IsNullOrWhiteSpace(_browserUrl) ? MfString | MfGrayed : MfString;
            AppendMenu(menu, openFlags, OpenCommand, "打开页面");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, ExitCommand, "退出");
            GetCursorPos(out var point);
            // 菜单不设为前台窗口时，点别处不会收起。
            SetForegroundWindow(hWnd);
            var command = TrackPopupMenu(
                menu,
                TpmLeftAlign | TpmRightButton | TpmBottomAlign | TpmReturnCmd,
                point.X,
                point.Y,
                0,
                hWnd,
                IntPtr.Zero);
            PostMessage(hWnd, WmNull, IntPtr.Zero, IntPtr.Zero);
            if ((nuint)command == OpenCommand)
            {
                OpenBrowser();
            }
            else if ((nuint)command == ExitCommand)
            {
                _lifetime.StopApplication();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void OpenBrowser()
    {
        if (string.IsNullOrWhiteSpace(_browserUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_browserUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "无法从托盘图标打开页面 {Url}", _browserUrl);
        }
    }

    private void RemoveIcon()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1
        };
        ShellNotifyIcon(NimDelete, ref data);
    }

    private void CleanupWindow()
    {
        if (_destroyIcon && _icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
        }

        _icon = IntPtr.Zero;
        _destroyIcon = false;
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private IntPtr LoadTrayIcon()
    {
        var path = MaterializeIcon();
        if (path is not null)
        {
            var loaded = LoadImage(
                IntPtr.Zero,
                path,
                ImageIcon,
                GetSystemMetrics(SmCxSmIcon),
                GetSystemMetrics(SmCySmIcon),
                LrLoadFromFile);
            if (loaded != IntPtr.Zero)
            {
                _destroyIcon = true;
                return loaded;
            }

            _logger.LogWarning("读取托盘图标文件失败（{Error}），改用系统默认图标。", Marshal.GetLastWin32Error());
        }

        return LoadIcon(IntPtr.Zero, new IntPtr(IdiApplication));
    }

    private static string? MaterializeIcon()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "wwwroot", "icons", "datatrace.ico");
        if (File.Exists(beside))
        {
            return beside;
        }

        var stream = typeof(TrayIconHost).Assembly.GetManifestResourceStream("DataTrace.Web.datatrace.ico");
        if (stream is null)
        {
            return null;
        }

        using (stream)
        {
            var directory = Path.Combine(Path.GetTempPath(), "DataTrace");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "datatrace.ico");
            using var file = File.Create(path);
            stream.CopyTo(file);
            return path;
        }
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WndClass lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool ShellNotifyIcon(uint dwMessage, ref NotifyIconData lpData);
}
