using System.Runtime.InteropServices;
using Live2DPet.App;

namespace Live2DPet.Platform;

/// <summary>One row of the pet's menu.</summary>
internal readonly record struct MenuItem(string Text, int Command, bool Checked, bool Separator = false);

/// <summary>
/// Shows the pet's menu without stopping the pet.
///
/// A popup menu is modal: <c>TrackPopupMenu</c> runs its own message loop and
/// does not return until the user chooses something. Called from the render
/// loop that means the pet freezes for as long as the menu is open - which is
/// exactly what a pet must not do, and what the engine this one replaces does
/// not do either, because its menu belongs to the window manager while its
/// renderer keeps drawing.
///
/// So the menu gets its own thread and its own hidden window to own it. The
/// render thread asks for a menu and carries on; the chosen command comes back
/// as a message to the pet's window.
/// </summary>
internal sealed class MenuHost : IDisposable
{
    private const string ClassName = "Live2DPetMenuHost";

    /// <summary>Render thread: show the menu described by the payload.</summary>
    private const uint ShowMenuMessage = Win32.WmApp + 10;

    /// <summary>Menu thread: the chosen command, in wParam.</summary>
    public const uint CommandMessage = Win32.WmApp + 11;

    private readonly nint _owner;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Win32.WndProc _wndProcHook;

    private nint _window;
    private MenuItem[] _items = [];
    private int _x;
    private int _y;
    private bool _open;

    public MenuHost(nint owner)
    {
        _owner = owner;
        _wndProcHook = WndProc;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "pet-menu",
        };

        _thread.Start();

        // The window has to exist before anyone can post to it.
        _ready.Wait(2000);
    }

    /// <summary>
    /// Asks for the menu at a screen position. Returns immediately. A second
    /// request while one is already open is ignored rather than queueing a
    /// second menu.
    /// </summary>
    public void Show(IReadOnlyList<MenuItem> items, int screenX, int screenY)
    {
        if (_window == 0)
        {
            return;
        }

        lock (_ready)
        {
            if (_open)
            {
                return;
            }

            _open = true;
        }

        _items = items as MenuItem[] ?? items.ToArray();
        _x = screenX;
        _y = screenY;

        Win32.PostMessageW(_window, ShowMenuMessage, 0, 0);
    }

    /// <summary>Called by the menu thread once the menu has closed.</summary>
    public void Release()
    {
        lock (_ready)
        {
            _open = false;
        }
    }

    /// <summary>
    /// Whether the menu is on screen right now. The pet asks, because a popup
    /// opens at the cursor - which is over the character - and a left click on a
    /// menu item is otherwise indistinguishable from a left click on the pet.
    /// </summary>
    public bool IsOpen
    {
        get
        {
            lock (_ready)
            {
                return _open;
            }
        }
    }

    private void Run()
    {
        var instance = Win32.GetModuleHandleW(null);

        var windowClass = new Win32.WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WndClassEx>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcHook),
            hInstance = instance,
            hbrBackground = 0,
            lpszClassName = ClassName,
        };

        Win32.RegisterClassExW(ref windowClass);

        // A real, hidden, top level window rather than a message-only one: a
        // popup menu needs an owner that lives in the window hierarchy.
        _window = Win32.CreateWindowExW(
            Win32.WsExToolWindow,
            ClassName,
            "live2dpet-menu",
            Win32.WsPopup,
            0,
            0,
            1,
            1,
            0,
            0,
            instance,
            0);

        _ready.Set();

        if (_window == 0)
        {
            Log.Warn("menu host window could not be created; the menu will not open");
            return;
        }

        while (Win32.GetMessageW(out var message, 0, 0, 0))
        {
            Win32.TranslateMessage(ref message);
            Win32.DispatchMessageW(ref message);
        }
    }

    private nint WndProc(nint hWnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case ShowMenuMessage:
                ShowMenu(hWnd);
                return 0;

            case Win32.WmClose:
                Win32.DestroyWindow(hWnd);
                return 0;

            case Win32.WmDestroy:
                Win32.PostQuitMessage(0);
                return 0;

            default:
                return Win32.DefWindowProcW(hWnd, message, wParam, lParam);
        }
    }

    private void ShowMenu(nint hWnd)
    {
        var menu = Win32.CreatePopupMenu();

        if (menu == 0)
        {
            Release();
            return;
        }

        try
        {
            foreach (var item in _items)
            {
                if (item.Separator)
                {
                    Win32.AppendMenuW(menu, Win32.MfSeparator, 0, string.Empty);
                    continue;
                }

                Win32.AppendMenuW(
                    menu,
                    Win32.MfString | (item.Checked ? Win32.MfChecked : 0),
                    item.Command,
                    item.Text);
            }

            // A menu on a window that is not foreground does not dismiss when
            // the user clicks elsewhere.
            Win32.SetForegroundWindow(hWnd);

            // Logged on both sides: a modal menu that never closes leaves the
            // pet alive but the menu stuck, and the log says where it stopped.
            Log.Info("menu shown");

            var choice = Win32.TrackPopupMenu(
                menu,
                Win32.TpmRightButton | Win32.TpmReturnCmd | Win32.TpmNoNotify,
                _x,
                _y,
                0,
                hWnd,
                0);

            Win32.PostMessageW(hWnd, 0, 0, 0);

            Log.Info($"menu closed (choice {choice})");

            if (choice != 0)
            {
                Win32.PostMessageW(_owner, CommandMessage, choice, 0);
            }
        }
        finally
        {
            Win32.DestroyMenu(menu);
            Release();
        }
    }

    public void Dispose()
    {
        if (_window != 0)
        {
            Win32.PostMessageW(_window, Win32.WmClose, 0, 0);
            _thread.Join(500);
            _window = 0;
        }
    }
}
