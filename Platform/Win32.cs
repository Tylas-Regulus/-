using System.Runtime.InteropServices;

namespace Live2DPet.Platform;

/// <summary>The handful of user32 entry points the pet window needs.</summary>
internal static class Win32
{
    public const int WsPopup = unchecked((int)0x80000000);
    public const int WsVisible = 0x10000000;

    public const int WsExTopmost = 0x00000008;
    public const int WsExTransparent = 0x00000020;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExLayered = 0x00080000;
    public const int WsExNoActivate = 0x08000000;
    public const int WsExNoRedirectionBitmap = 0x00200000;

    public const int SwShowNoActivate = 4;

    public const uint PmRemove = 0x0001;

    public const int WmDestroy = 0x0002;
    public const int WmSize = 0x0005;
    public const int WmPaint = 0x000F;
    public const int WmClose = 0x0010;
    public const int WmEraseBackground = 0x0014;
    public const int WmKeyDown = 0x0100;
    public const int WmApp = 0x8000;
    public const int WmDisplayChange = 0x007E;
    public const int WmDpiChanged = 0x02E0;
    public const int WmQuit = 0x0012;
    public const int WmSetCursor = 0x0020;
    public const int WmNcHitTest = 0x0084;
    public const int WmHotKey = 0x0312;
    public const int WmMouseMove = 0x0200;
    public const int WmLButtonDown = 0x0201;
    public const int WmLButtonUp = 0x0202;
    public const int WmRButtonDown = 0x0204;
    public const int WmRButtonUp = 0x0205;

    public const int VkEscape = 0x1B;

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;

    public const int HwndTopmost = -1;
    public const int HwndNotTopmost = -2;

    public const nint HtClient = 0;
    public const nint HtTransparent = -1;

    public const uint MfString = 0x0000;
    public const uint MfChecked = 0x0008;
    public const uint MfSeparator = 0x0800;

    public const uint TpmRightButton = 0x0002;
    public const uint TpmNoNotify = 0x0080;
    public const uint TpmReturnCmd = 0x0100;

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModNoRepeat = 0x4000;
    public const int VkP = 0x50;
    public const int VkQ = 0x51;
    public const int VkLeftButton = 0x01;
    public const int VkRightButton = 0x02;

    public const int GaRoot = 2;

    /// <summary>Stock icon identifiers for <c>LoadIconW</c>.</summary>
    public static readonly nint IdiApplication = 32512;

    public const uint NifMessage = 0x0001;
    public const uint NifIcon = 0x0002;
    public const uint NifTip = 0x0004;

    public const uint NimAdd = 0x00000000;
    public const uint NimModify = 0x00000001;
    public const uint NimDelete = 0x00000002;

    /// <summary>Stock cursor identifiers, as passed to <c>LoadCursorW</c>.</summary>
    public static readonly nint IdcArrow = 32512;

    public static readonly nint IdcSizeAll = 32646;

    /// <summary>Primary monitor metrics, as <c>GetSystemMetrics</c> indexes them.</summary>
    public const int SmCxScreen = 0;

    public const int SmCyScreen = 1;

    /// <summary>GW_ENABLEDPOPUP: the window that takes the input for a disabled one,
    /// which is how a modal dialog is found without knowing its title or class.</summary>
    public const int GwEnabledPopup = 6;

    public const int SmCxIcon = 11;

    public const int SmCyIcon = 12;

    public const int SmCxSmIcon = 49;

    public const int SmCySmIcon = 50;

    public const uint ImageIcon = 1;

    public const uint LrLoadFromFile = 0x00000010;

    public delegate nint WndProc(nint hWnd, uint message, nint wParam, nint lParam);

    public delegate nint HookProc(int code, nint wParam, nint lParam);

    /// <summary>
    /// What a low-level keyboard hook is handed for each key event. Only the
    /// first two fields are read here, which is why nothing else is declared.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardHookData
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public nint extraInfo;
    }

    public const int VkControl = 0x11;
    public const int VkShift = 0x10;
    public const int VkMenu = 0x12;
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookExW(int hookId, HookProc callback, nint module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Message
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public Point pt;
        public uint lPrivate;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WndClassEx wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern nint DefWindowProcW(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern nint SetCapture(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern nint SetCursor(nint cursor);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint LoadCursorW(nint instance, nint cursorName);

    [DllImport("user32.dll")]
    public static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenuW(nint menu, uint flags, nint item, string text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenu(
        nint menu,
        uint flags,
        int x,
        int y,
        int reserved,
        nint window,
        nint rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowRgn(nint hWnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    public static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    public static extern nint GetAncestor(nint hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint LoadIconW(nint instance, nint iconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint LoadImageW(nint instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowTextW(nint hWnd, string text);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessageW(
        out Message message,
        nint hWnd,
        uint filterMin,
        uint filterMax,
        uint removeMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMessageW(out Message message, nint hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint DispatchMessageW(ref Message message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(nint hWnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        nint hWnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(nint dpiContext);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint GetWindow(nint hWnd, int command);

    /// <summary>Finds a top level window by title. Diagnostic only, and it says so:
    /// matching a window by its caption is not something the engine does to make
    /// anything work.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint FindWindowW(string? className, string? windowName);

    /// <summary>
    /// The desktop's vertical refresh rate in Hz, or 0 when the driver will not
    /// say.
    ///
    /// Asked of the display rather than assumed to be 60: the pet's default
    /// frame ceiling is what the monitor actually draws, and a 165 Hz panel told
    /// "60" would be capped at a third of what it can show. <c>VREFRESH</c>
    /// answers 1 for "whatever the hardware defaults to", which is reported here
    /// as unknown rather than as a one-frame-per-second pet.
    /// </summary>
    public static int DesktopRefreshRate()
    {
        var dc = GetDC(0);

        if (dc == 0)
        {
            return 0;
        }

        try
        {
            var rate = GetDeviceCaps(dc, VRefresh);
            return rate > 1 ? rate : 0;
        }
        finally
        {
            _ = ReleaseDC(0, dc);
        }
    }

    /// <summary>VREFRESH: the vertical refresh rate of the display, an index of
    /// <c>GetDeviceCaps</c>.</summary>
    private const int VRefresh = 116;

    [DllImport("user32.dll")]
    public static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint hWnd, nint dc);

    [DllImport("gdi32.dll")]
    public static extern int GetDeviceCaps(nint dc, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    /// <summary>Button state, read directly rather than inferred from messages.</summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hWnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(nint hWnd, ref Point point);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("kernel32.dll")]
    public static extern nint GetCurrentProcess();

    /// <summary>
    /// Raises the calling thread's priority against everything else running on
    /// the same processor.
    ///
    /// The loop wakes on a high resolution timer, and on a machine with a spare
    /// core that wake is immediate. On a machine with one processor it is not:
    /// the thread is ready, but the processor is inside somebody else's slice, so
    /// a frame that needs 10ms occasionally starts 3ms late. Measured at one
    /// logical processor: the median frame was exactly the pacing interval and
    /// the 95th percentile was 2.2ms over it, with nothing in the renderer to
    /// account for the difference.
    ///
    /// Above normal rather than highest or time critical: a pet that outranks the
    /// compositor would starve the very thing that puts its frames on screen.
    /// </summary>
    public static bool RaiseThreadPriority(int priority) =>
        SetThreadPriority(GetCurrentThread(), priority);

    public const int ThreadPriorityNormal = 0;
    public const int ThreadPriorityAboveNormal = 1;
    public const int ThreadPriorityHighest = 2;

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(nint thread, int priority);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessAffinityMask(nint process, nint mask);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    /// <summary>
    /// PER_MONITOR_AWARE_V2. Without this the DWM rescales the window, so every
    /// pixel we render gets resampled: blurry output for full cost.
    /// </summary>
    public static readonly nint DpiAwarenessPerMonitorV2 = unchecked((nint)(-4));
}
