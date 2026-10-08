namespace Live2DPet.Platform;

/// <summary>
/// Reads the cursor position and normalises it against the pet window.
///
/// Polling rather than listening for <c>WM_MOUSEMOVE</c>: a desktop pet
/// eventually becomes click-through across most of its area, at which point the
/// window stops receiving mouse messages entirely and has to ask where the
/// pointer is anyway. Asking once per frame is cheaper than the alternative the
/// Electron pet used, which pushed the position over IPC twenty times a second.
///
/// The look radius is deliberately not the window size. A pet window is small
/// relative to the desktop, so normalising by it pins the gaze at the extremes
/// almost all the time - the character ends up staring rigidly sideways. Radius
/// is measured from the window centre with the same value on both axes so the
/// response is circular, and everything beyond it clamps so the head can still
/// reach a full turn towards a far-away cursor.
/// </summary>
internal sealed class CursorTracker
{
    private readonly nint _windowHandle;
    private readonly float _lookRadius;

    public CursorTracker(nint windowHandle, float lookRadius)
    {
        _windowHandle = windowHandle;
        _lookRadius = MathF.Max(lookRadius, 1f);
    }

    public float X { get; private set; }

    public float Y { get; private set; }

    public void Sample()
    {
        if (!Win32.GetCursorPos(out var cursor) || !Win32.GetWindowRect(_windowHandle, out var window))
        {
            return;
        }

        var centreX = (window.Left + window.Right) * 0.5f;
        var centreY = (window.Top + window.Bottom) * 0.5f;

        X = Math.Clamp((cursor.X - centreX) / _lookRadius, -1f, 1f);
        Y = Math.Clamp((centreY - cursor.Y) / _lookRadius, -1f, 1f);
    }
}
