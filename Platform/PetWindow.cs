using System.Runtime.InteropServices;
using Live2DPet.App;

namespace Live2DPet.Platform;

/// <summary>
/// The top level window the pet lives in.
///
/// Two things separate this from an ordinary Win32 window:
///
///   * <c>WS_EX_NOREDIRECTIONBITMAP</c> removes the DWM redirection surface.
///     That surface is what normally costs a full-window copy every frame and is
///     the reason a full-screen transparent overlay is so expensive. Removing it
///     means the window must be fed by DirectComposition, otherwise it shows
///     nothing at all.
///   * The window is sized to the model, not to the desktop. Compositing cost is
///     proportional to covered area, so a 640x900 pet costs roughly a tenth of a
///     1920x1080 one for exactly the same picture.
///   * The window is shaped to the pet by a region, so everything beside and
///     around the character belongs to the desktop rather than to a rectangle of
///     glass the size of the window. See <see cref="WindowRegion"/>.
///
/// It is an ordinary top level window otherwise: it appears in the task bar,
/// clicking it activates it, and Escape and Alt+F4 work. Being invisible in the
/// task bar and never taking focus sounds tidier for a pet, but it was measured
/// to be the wrong trade: those styles also take away the keystrokes and the
/// activation that make the window behave like a window.
/// </summary>
internal sealed class PetWindow : IDisposable
{
    private const string ClassName = "Live2DPetWindow";

    private const int CommandQuit = 1;
    private const int CommandTogglePassThrough = 2;
    private const int CommandToggleTopMost = 3;
    private const int CommandResetPosition = 4;
    private const int CommandToggleFocusFollow = 5;

    private readonly Win32.WndProc _wndProcHook;
    private readonly TrayIcon? _tray;
    private MenuHost? _menu;
    private readonly nint _largeIcon;
    private readonly nint _smallIcon;
    private readonly bool _ownsIcons;
    private nint _handle;
    private bool _disposed;
    private bool _topMost;
    private Win32.Point _defaultPosition;

    public PetWindow(
        string title,
        int width,
        int height,
        bool topMost,
        bool transparent,
        bool clickThrough,
        bool tray = true)
    {
        Width = width;
        Height = height;

        // Keep our own delegate alive for the lifetime of the window.
        _wndProcHook = WndProc;

        var instance = Win32.GetModuleHandleW(null);

        _largeIcon = LoadAppIcon(Win32.SmCxIcon, Win32.SmCyIcon, out var largeIsOurs);
        _smallIcon = LoadAppIcon(Win32.SmCxSmIcon, Win32.SmCySmIcon, out var smallIsOurs);
        _ownsIcons = largeIsOurs || smallIsOurs;

        var wndClass = new Win32.WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WndClassEx>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcHook),
            hInstance = instance,
            hIcon = _largeIcon,
            hIconSm = _smallIcon,
            hbrBackground = 0,
            lpszClassName = ClassName,
        };

        if (Win32.RegisterClassExW(ref wndClass) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1410 /* ERROR_CLASS_ALREADY_EXISTS */)
            {
                throw new InvalidOperationException($"RegisterClassExW failed with {error}");
            }
        }

        var exStyle = 0;
        _topMost = topMost;

        if (topMost)
        {
            exStyle |= Win32.WsExTopmost;
        }

        if (transparent)
        {
            exStyle |= Win32.WsExNoRedirectionBitmap;
        }

        if (clickThrough)
        {
            exStyle |= Win32.WsExTransparent | Win32.WsExNoActivate;
        }

        // Bottom right of the primary monitor, which is where a pet belongs and
        // is at least not on top of the desktop icons. Clamped so a window larger
        // than the screen still starts somewhere visible.
        const int margin = 40;
        var x = Math.Max(0, Win32.GetSystemMetrics(Win32.SmCxScreen) - width - margin);
        var y = Math.Max(0, Win32.GetSystemMetrics(Win32.SmCyScreen) - height - margin);
        _defaultPosition = new Win32.Point { X = x, Y = y };

        _handle = Win32.CreateWindowExW(
            exStyle,
            ClassName,
            title,
            Win32.WsPopup,
            x,
            y,
            width,
            height,
            0,
            0,
            instance,
            0);

        if (_handle == 0)
        {
            throw new InvalidOperationException($"CreateWindowExW failed with {Marshal.GetLastWin32Error()}");
        }

        Log.Info($"window created: 0x{_handle:X} {width}x{height} exStyle=0x{(uint)exStyle:X8}");

        if (tray)
        {
            _tray = new TrayIcon(_handle, "Live2DPet  |  右键点我", _smallIcon);
        }

        Log.Info("no hotkeys are registered; the menu is on the right button and in the tray");
    }

    public nint Handle => _handle;

    /// <summary>
    /// The pet's own icon, from the file next to the executable, falling back to
    /// the stock one so a missing asset is a cosmetic problem rather than a
    /// startup failure. Returns the handle and whether the caller owns it - the
    /// stock icon belongs to the system and must not be destroyed.
    /// </summary>
    private static nint LoadAppIcon(int widthMetric, int heightMetric, out bool owned)
    {
        owned = false;

        var path = Path.Combine(AppContext.BaseDirectory, "assets", "live2dpet.ico");
        if (!File.Exists(path))
        {
            return Win32.LoadIconW(0, Win32.IdiApplication);
        }

        var icon = Win32.LoadImageW(
            0,
            path,
            Win32.ImageIcon,
            Win32.GetSystemMetrics(widthMetric),
            Win32.GetSystemMetrics(heightMetric),
            Win32.LrLoadFromFile);

        if (icon == 0)
        {
            Log.Warn($"could not load {path}; using the stock icon");
            return Win32.LoadIconW(0, Win32.IdiApplication);
        }

        owned = true;
        return icon;
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Set from the window procedure; the render loop consumes it.</summary>
    public bool CloseRequested { get; private set; }

    /// <summary>Raised when the client area changed and the swap chain must follow.</summary>
    public bool ResizePending { get; private set; }

    /// <summary>
    /// Published by the interaction layer so the window can show a move cursor
    /// over the character. The window itself does not know what a model is.
    /// </summary>
    public bool Hovering { get; set; }

    /// <summary>
    /// When the pet holds the keyboard focus and the pointer leaves, hand that
    /// focus to whatever is under the pointer instead of keeping it.
    ///
    /// This is the half of "focus follows the pointer" Windows will let an
    /// application do. Taking focus by hovering is not allowed - a process that
    /// is not already foreground may not make itself foreground, an
    /// anti-focus-stealing rule - so the pet still only takes focus when it is
    /// clicked.
    /// </summary>
    public bool FocusFollowPointer { get; set; }

    /// <summary>
    /// How big the pet is, as a fraction of the window it was created with: the
    /// engine sets this whenever the size changes, and reads it to remember the
    /// size. The window is what it is; this is only the note of how it compares
    /// to the size it started at.
    /// </summary>
    public float Scale { get; set; } = 1f;

    /// <summary>Whether the pet currently holds the keyboard focus.</summary>
    public bool IsForeground => Win32.GetForegroundWindow() == _handle;

    /// <summary>
    /// Whether the pet's own menu is open. While it is, the pet must not react
    /// to the mouse: the menu appears at the cursor, so a left click on a menu
    /// item is a left click over the character, and the pet has no way to tell
    /// the two apart by position alone.
    /// </summary>
    public bool MenuOpen => _menu is { IsOpen: true };

    /// <summary>
    /// Gives focus to the window under the cursor, if the pet has it. Called
    /// when focus follows the pointer is on and the pointer has left the pet.
    /// </summary>
    public bool GiveFocusBack()
    {
        if (!IsForeground || !Win32.GetCursorPos(out var cursor))
        {
            return false;
        }

        var under = Win32.GetAncestor(Win32.WindowFromPoint(cursor), Win32.GaRoot);
        if (under == 0 || under == _handle)
        {
            return false;
        }

        // One of our own windows is not "another program": the menu host is a
        // window of this process, and handing focus to it would look like the
        // pet keeping focus while actually having given it away.
        Win32.GetWindowThreadProcessId(under, out var owner);

        if (owner == (uint)Environment.ProcessId)
        {
            Log.Info($"focus hand-back skipped: 0x{under:X} is this process's own window");
            return false;
        }

        var moved = Win32.SetForegroundWindow(under);
        Log.Info(
            moved
                ? $"focus handed back to 0x{under:X} (pid {owner})"
                : $"focus hand-back refused by Windows for 0x{under:X} (pid {owner}); " +
                  "the pet keeps the keyboard until something else is clicked");

        return moved;
    }

    /// <summary>When set the whole window passes clicks through, character and
    /// all - the "leave me alone" switch for when the pet is in the way.
    ///
    /// Implemented as an empty window region rather than a hit test override,
    /// because the region is enforced by the window manager and therefore works
    /// against windows in other processes. An overridden <c>WM_NCHITTEST</c> that
    /// returns <c>HTTRANSPARENT</c> only forwards to windows in the same thread,
    /// which here means it forwards to nothing and the click is swallowed.</summary>
    public bool PassThrough
    {
        get => _passThrough;
        set
        {
            if (_passThrough == value)
            {
                return;
            }

            _passThrough = value;
            ApplyShape();

            // Entering this mode takes away the only other way to reach the
            // menu. The tray icon is the intended way back; the task bar button
            // is the one that does not depend on anything of ours working, so
            // say so when the tray is missing.
            if (value && (_tray == null || !_tray.IsAdded))
            {
                Log.Warn(
                    "click-through is on and there is no tray icon; " +
                    "use the task bar button's Close window to quit the pet");
            }
        }
    }

    private bool _passThrough;

    /// <summary>
    /// The region the window is currently shaped to, or null for the whole
    /// rectangle. Set by the host, which is the only thing that knows where the
    /// character is.
    ///
    /// Takes ownership of the region: <c>SetWindowRgn</c> hands it to the window
    /// manager, so neither side deletes it. The host builds a fresh region
    /// whenever the pose has moved enough to matter, which is also what restores
    /// the shape after a pass-through toggle.
    /// </summary>
    public void SetShape(nint region)
    {
        if (_handle == 0 || region == 0)
        {
            return;
        }

        if (_passThrough)
        {
            // Holding the shape is not ownership of anything yet: it has not been
            // given to the window, so it is ours to delete.
            Win32.DeleteObject(region);
            return;
        }

        Win32.SetWindowRgn(_handle, region, true);
    }

    private void ApplyShape()
    {
        if (_handle == 0)
        {
            return;
        }

        if (_passThrough)
        {
            Win32.SetWindowRgn(_handle, WindowRegion.Empty(), true);
            Log.Info("window shape: none, the pet takes no mouse input");
            return;
        }

        // Coming out of pass-through: the host re-applies the shape on its next
        // rebuild, which is a fifth of a second away, but until then the window
        // would have no region at all and would be a rectangle again. Clearing it
        // says "whole window for a moment" rather than "the last pet shape".
        Win32.SetWindowRgn(_handle, 0, true);
        Log.Info("window shape: whole window until the next silhouette update");
    }

    /// <summary>Logs every mouse message the window receives. Diagnostic: when
    /// clicks appear to do nothing, the first question is whether they arrived.</summary>
    public bool TraceInput { get; set; }

    /// <summary>Position the pet was created at, for the menu's reset.</summary>
    public Win32.Point DefaultPosition => _defaultPosition;

    public bool TopMost => _topMost;

    /// <summary>
    /// Menu and hotkey actions are recorded here and consumed by the render
    /// loop, so nothing in the window procedure mutates engine state directly.
    /// </summary>
    public PetCommand TakeCommand()
    {
        var command = _pendingCommand;
        _pendingCommand = PetCommand.None;
        return command;
    }

    private PetCommand _pendingCommand;

    public void Show() => Win32.ShowWindow(_handle, Win32.SwShowNoActivate);

    public void MoveTo(int x, int y) => Win32.SetWindowPos(_handle, 0, x, y, 0, 0, Win32.SwpNoSize | Win32.SwpNoZOrder | Win32.SwpNoActivate);

    /// <summary>
    /// Changes how big the pet is, keeping the middle of it where the middle was.
    ///
    /// The model is fitted to the window, so resizing the window is resizing the
    /// pet. Growing from the top-left corner instead would walk a pet that lives
    /// in the bottom right corner off the edge of the screen, and the eye is on
    /// the character rather than on the window, so the character is what stays
    /// still. The result is clamped to the screen's work area, which is why the
    /// caller is told the size that was actually granted.
    /// </summary>
    public void ResizeTo(int width, int height)
    {
        if (_handle == 0)
        {
            return;
        }

        width = Math.Max(64, width);
        height = Math.Max(64, height);

        if (!Win32.GetWindowRect(_handle, out var current))
        {
            return;
        }

        var x = current.Left + (current.Width - width) / 2;
        var y = current.Top + (current.Height - height) / 2;

        var screenWidth = Win32.GetSystemMetrics(Win32.SmCxScreen);
        var screenHeight = Win32.GetSystemMetrics(Win32.SmCyScreen);

        x = Math.Clamp(x, -width / 2, Math.Max(0, screenWidth - width / 2));
        y = Math.Clamp(y, 0, Math.Max(0, screenHeight - height / 2));

        Width = width;
        Height = height;

        Win32.SetWindowPos(
            _handle,
            0,
            x,
            y,
            width,
            height,
            Win32.SwpNoZOrder | Win32.SwpNoActivate);

        ResizePending = true;
    }

    /// <summary>
    /// Opens the right button menu at the cursor. Called by the render loop
    /// rather than from the window procedure, so the modal loop it runs starts
    /// from a known place, and so it does not depend on the window being routed
    /// mouse messages.
    /// </summary>
    public void ShowMenu() => ShowContextMenu();

    public void SetTopMost(bool topMost)
    {
        _topMost = topMost;
        Win32.SetWindowPos(
            _handle,
            topMost ? Win32.HwndTopmost : Win32.HwndNotTopmost,
            0, 0, 0, 0,
            Win32.SwpNoSize | Win32.SwpNoMove | Win32.SwpNoActivate);
    }

    /// <summary>Moves the window without resizing, re-ordering or activating it.</summary>
    public void SetPosition(int x, int y) =>
        Win32.SetWindowPos(_handle, 0, x, y, 0, 0, Win32.SwpNoSize | Win32.SwpNoZOrder | Win32.SwpNoActivate);

    /// <summary>Pumps all pending messages. Returns false when the app should exit.</summary>
    public bool PumpMessages()
    {
        while (Win32.PeekMessageW(out var message, 0, 0, 0, Win32.PmRemove))
        {
            if (message.message == Win32.WmQuit)
            {
                return false;
            }

            Win32.TranslateMessage(ref message);
            Win32.DispatchMessageW(ref message);
        }

        return !CloseRequested;
    }

    public void ClearResizeFlag() => ResizePending = false;

    public void RefreshClientSize()
    {
        if (_handle == 0)
        {
            return;
        }

        if (!GetClientRect(_handle, out var rect))
        {
            return;
        }

        var width = rect.Width;
        var height = rect.Height;
        if (width <= 0 || height <= 0 || (width == Width && height == Height))
        {
            return;
        }

        Width = width;
        Height = height;
        ResizePending = true;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hWnd, out Win32.Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ValidateRect(nint hWnd, nint rect);

    private nint WndProc(nint hWnd, uint message, nint wParam, nint lParam)
    {
        // Button messages are logged always. They are a handful of lines per
        // interaction, and when a click appears to do nothing the first question
        // is whether it arrived at all - which is otherwise unanswerable from
        // outside the process.
        if (IsButtonMessage(message) || (TraceInput && IsMouseMessage(message)))
        {
            var at = new Win32.Point
            {
                X = (short)(lParam.ToInt64() & 0xFFFF),
                Y = (short)((lParam.ToInt64() >> 16) & 0xFFFF),
            };

            Log.Info($"input: message 0x{message:X4} at screen ({at.X},{at.Y})");
        }

        switch (message)
        {
            case Win32.WmClose:
                CloseRequested = true;
                return 0;

            // The tray icon speaks through this: its lParam is the mouse
            // message that happened on the icon.
            case TrayIcon.CallbackMessage:
                if ((int)(lParam.ToInt64() & 0xFFFF) is Win32.WmRButtonUp or Win32.WmLButtonUp)
                {
                    ShowContextMenu();
                }

                return 0;

            // The menu thread reporting what the user picked.
            case MenuHost.CommandMessage:
                ApplyCommand((int)wParam);
                return 0;

            case Win32.WmDestroy:
                CloseRequested = true;
                Win32.PostQuitMessage(0);
                return 0;

            case Win32.WmSize:
            case Win32.WmDpiChanged:
            case Win32.WmDisplayChange:
                ResizePending = true;
                return 0;

            // The default cursor is the arrow; over the character a move cursor
            // is what tells the user the pet can be picked up.
            case Win32.WmSetCursor:
                Win32.SetCursor(Win32.LoadCursorW(0, Hovering ? Win32.IdcSizeAll : Win32.IdcArrow));
                return 1;

            // With no redirection bitmap there is nothing for Windows to paint.
            case Win32.WmEraseBackground:
                return 1;

            case Win32.WmPaint:
                ValidateRect(hWnd, 0);
                return 0;

            default:
                // Explorer announces its own restart; the tray icon has to be
                // re-added or it is gone for good.
                if (_tray != null && message == _tray.TaskbarCreatedMessage)
                {
                    _tray.ReAdd();
                    return 0;
                }

                return Win32.DefWindowProcW(hWnd, message, wParam, lParam);
        }
    }

    private static bool IsMouseMessage(uint message) => message switch
    {
        Win32.WmMouseMove or Win32.WmLButtonDown or Win32.WmLButtonUp or
        Win32.WmRButtonDown or Win32.WmRButtonUp or Win32.WmNcHitTest or Win32.WmSetCursor => true,
        _ => false,
    };

    private static bool IsButtonMessage(uint message) => message switch
    {
        Win32.WmLButtonDown or Win32.WmLButtonUp or Win32.WmRButtonDown or Win32.WmRButtonUp => true,
        _ => false,
    };

    /// <summary>
    /// Asks for the menu. The menu is built here, from the current state, but
    /// shown by <see cref="MenuHost"/> on its own thread - a popup menu is modal,
    /// and running it here would freeze the pet for as long as it is open.
    /// </summary>
    private void ShowContextMenu()
    {
        Log.Info("menu requested");

        // Normally at the cursor. If there is no cursor to ask about - a session
        // without one - fall back to the middle of the pet rather than refusing
        // to open the menu at all.
        var at = new Win32.Point();
        var haveCursor = Win32.GetCursorPos(out at);

        if (!haveCursor || (at.X == 0 && at.Y == 0))
        {
            if (!Win32.GetWindowRect(_handle, out var bounds))
            {
                Log.Warn("menu: no cursor and no window rectangle to fall back to");
                return;
            }

            at = new Win32.Point
            {
                X = bounds.Left + Width / 2,
                Y = bounds.Top + Height / 2,
            };

            Log.Warn("no cursor position; the menu opens at the pet's centre instead");
        }

        _menu ??= new MenuHost(_handle);

        _menu.Show(
            [
                new MenuItem("点击穿透（整只让开）", CommandTogglePassThrough, PassThrough),
                new MenuItem("始终置顶", CommandToggleTopMost, _topMost),
                new MenuItem("鼠标离开时交还焦点", CommandToggleFocusFollow, FocusFollowPointer),
                new MenuItem(string.Empty, 0, false, Separator: true),
                new MenuItem("重置位置", CommandResetPosition, false),
                new MenuItem(string.Empty, 0, false, Separator: true),
                new MenuItem("退出", CommandQuit, false),
            ],
            at.X,
            at.Y);
    }

    /// <summary>
    /// Acts on the command the menu reported. Runs on the render thread, from
    /// the message the menu sent, so the menu itself never touches engine state.
    /// </summary>
    private void ApplyCommand(int command)
    {
        Log.Info($"menu command {command}");

        switch (command)
        {
            case CommandQuit:
                _pendingCommand = PetCommand.Quit;
                break;

            case CommandTogglePassThrough:
                PassThrough = !PassThrough;
                Log.Info(PassThrough
                    ? "click-through on: the pet takes no mouse input at all; " +
                      "use the tray icon to turn it back off"
                    : "click-through off");

                if (_tray != null)
                {
                    _tray.Tooltip = PassThrough
                        ? "Live2DPet  |  点击穿透中,右键我恢复"
                        : "Live2DPet  |  右键点我";
                    _tray.Refresh();
                }

                break;

            case CommandToggleTopMost:
                SetTopMost(!_topMost);
                break;

            case CommandToggleFocusFollow:
                FocusFollowPointer = !FocusFollowPointer;
                Log.Info($"focus follows the pointer: {(FocusFollowPointer ? "on" : "off")}");
                break;

            case CommandResetPosition:
                MoveTo(_defaultPosition.X, _defaultPosition.Y);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_handle != 0)
        {
            _menu?.Dispose();
            _tray?.Dispose();
            Win32.DestroyWindow(_handle);
            _handle = 0;
        }

        if (_ownsIcons)
        {
            if (_largeIcon != 0)
            {
                Win32.DestroyIcon(_largeIcon);
            }

            if (_smallIcon != 0)
            {
                Win32.DestroyIcon(_smallIcon);
            }
        }
    }
}

/// <summary>Something the user asked for from the menu or a hotkey, for the
/// render loop to act on.</summary>
internal enum PetCommand
{
    None,
    Quit,
}
