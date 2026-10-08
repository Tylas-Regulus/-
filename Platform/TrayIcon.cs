using Live2DPet.App;

namespace Live2DPet.Platform;

/// <summary>
/// The notification area icon, which is the pet's home when it cannot be
/// clicked.
///
/// This is not decoration. A sticky click-through mode - "step aside, I am in
/// the middle of something" - takes away the only other way to reach the pet's
/// menu, so there has to be a way back that does not involve clicking the pet.
/// The reference implementation this project learned from does the same thing,
/// for the same reason.
///
/// The icon is re-added when the shell restarts, because losing it while
/// click-through is on would leave the pet with no exit at all.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    public const uint CallbackMessage = Win32.WmApp + 1;
    private const uint IconId = 1;

    private readonly nint _owner;
    private readonly uint _taskbarCreated;
    private bool _added;

    public TrayIcon(nint owner, string tooltip, nint icon)
    {
        _owner = owner;
        _icon = icon;
        Tooltip = tooltip;

        // Explorer broadcasts this after it restarts; without it the icon
        // silently disappears and the user is left with no menu.
        _taskbarCreated = Win32.RegisterWindowMessageW("TaskbarCreated");

        Add();
    }

    public string Tooltip { get; set; }

    private readonly nint _icon;

    public uint TaskbarCreatedMessage => _taskbarCreated;

    /// <summary>Whether the shell actually accepted the icon. The object can
    /// exist and the icon still not be there, which is the difference between a
    /// way back from click-through and no way back.</summary>
    public bool IsAdded => _added;

    private void Add()
    {
        var data = Build(Win32.NifMessage | Win32.NifIcon | Win32.NifTip);

        _added = Win32.Shell_NotifyIconW(Win32.NimAdd, ref data);

        if (_added)
        {
            Log.Info("tray icon added");
        }
        else
        {
            // Not fatal: the pet still works, it just cannot offer its menu
            // while click-through is on.
            Log.Warn("could not add a tray icon; the menu is only reachable by right-clicking the pet");
        }
    }

    /// <summary>Called when the shell announces it has restarted.</summary>
    public void ReAdd()
    {
        if (!_added)
        {
            return;
        }

        Add();
    }

    /// <summary>Pushes a changed tooltip to the shell.</summary>
    public void Refresh()
    {
        if (!_added)
        {
            return;
        }

        var data = Build(Win32.NifIcon | Win32.NifTip);
        Win32.Shell_NotifyIconW(Win32.NimModify, ref data);
    }

    private Win32.NotifyIconData Build(uint flags) => new()
    {
        Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.NotifyIconData>(),
        Window = _owner,
        Id = IconId,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = Tooltip,
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    public void Dispose()
    {
        if (!_added)
        {
            return;
        }

        var data = Build(0);
        Win32.Shell_NotifyIconW(Win32.NimDelete, ref data);
        _added = false;
    }
}
