using Live2DPet.Platform;
using Live2DPet.Rendering;

namespace Live2DPet.App;

/// <summary>
/// Turns the mouse into pet behaviour: pick the character up and it moves with
/// the pointer, click it and it reacts.
///
/// Everything here is polled: the cursor position and the button states are read
/// once a frame and compared with the previous frame. No mouse message is needed
/// and none is trusted.
///
/// That is not a stylistic choice. The first version took the button state from
/// <c>WM_LBUTTONDOWN</c> and it did not work on the machine this was written
/// for: the cursor changed over the character and clicks did nothing, which
/// means <c>WM_SETCURSOR</c> arrived and the button messages did not. Whatever
/// caused that, an interaction that depends on being routed mouse messages is
/// fragile in a way a pet cannot afford - it is supposed to be picked up, and
/// being unable to pick it up is not a small defect. Polling cannot be starved
/// by hit test routing, window styles or activation.
///
/// It also means no mouse capture: the pointer position is global, so a drag
/// keeps working after the cursor leaves the window, which is what a drag does.
///
/// A press only becomes a drag if it landed on the character. Grabbing the empty
/// space around the pet would make the window feel like a window rather than
/// like a pet, and it is the alpha hit test that draws that line.
/// </summary>
internal sealed class PetInteraction
{
    /// <summary>Movement under this many pixels is still a click.</summary>
    private const int ClickSlopPixels = 4;

    /// <summary>
    /// How long after the right button moves the pet still treats a left press
    /// as part of the same gesture - the one that opens its menu.
    ///
    /// The menu is opened with the right button and appears under the cursor,
    /// which is over the character; anything the user then does with the left
    /// button is aimed at the menu. The pet cannot tell the two apart by
    /// position, but it can tell them apart by timing: a left press in the
    /// shadow of a right press belongs to the menu, not to the pet.
    /// </summary>
    private const double RightGestureSeconds = 0.3;

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private readonly PetWindow _window;
    private readonly HitTester _hitTester;

    private bool _pressed;
    private bool _dragging;
    private bool _moved;
    private bool _rightPressed;
    private bool _menuRequested;
    private int _pressCursorX;
    private int _pressCursorY;
    private int _pressWindowX;
    private int _pressWindowY;
    private int _clickedDrawable = -1;
    private int _clickClientX;
    private int _clickClientY;
    private int _lastTestX = int.MinValue;
    private int _lastTestY = int.MinValue;

    /// <summary>
    /// Whether the press currently being tracked started on the pet. A press
    /// that started somewhere else - on the pet's own menu, most often - must
    /// not become a click or a drag when the button comes back up, even though
    /// the cursor is over the character by then.
    /// </summary>
    private bool _pressIsOurs;

    /// <summary>When the right button last changed state, on the same clock the
    /// gesture guard measures against.</summary>
    private double _rightMoved = double.MinValue;

    /// <summary>The right button, as of this frame. Read by the decision logic,
    /// which is handed the left button only.</summary>
    private bool _rightDown;

    public PetInteraction(PetWindow window, HitTester hitTester)
    {
        _window = window;
        _hitTester = hitTester;
    }

    /// <summary>The drawable under the cursor, or -1.</summary>
    public int HoveredDrawable { get; private set; } = -1;

    public bool Hovering => HoveredDrawable >= 0;

    public bool Dragging => _dragging;

    /// <summary>
    /// Runs once per frame, after the deform, so the hit test and the picture
    /// are looking at the same pose.
    /// </summary>
    public void Update(in ModelFraming framing, int viewportWidth, int viewportHeight)
    {
        if (!Win32.GetCursorPos(out var cursor))
        {
            return;
        }

        var leftDown = IsDown(Win32.VkLeftButton);
        var rightDown = IsDown(Win32.VkRightButton);

        // The right button opens the menu on the way down rather than on the way
        // up: a menu that appears after you release feels late, and it means a
        // press that wanders is still unambiguous.
        if (rightDown && !_rightPressed && Hovering)
        {
            _menuRequested = true;
        }

        if (rightDown != _rightPressed)
        {
            _rightMoved = _clock.Elapsed.TotalSeconds;
        }

        _rightPressed = rightDown;
        _rightDown = rightDown;

        Step(cursor, leftDown, framing, viewportWidth, viewportHeight);
    }

    private static bool IsDown(int virtualKey) => (Win32.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>Reports a right click once, for the caller to open the menu.</summary>
    public bool TryTakeMenuRequest()
    {
        if (!_menuRequested)
        {
            return false;
        }

        _menuRequested = false;
        return true;
    }

    /// <summary>
    /// The decision logic, with the pointer handed in rather than read.
    ///
    /// Separated so a diagnostic can drive a whole drag with scripted positions:
    /// there is no way to move a real mouse from inside this process, and a drag
    /// that is merely believed to work is the kind of thing that is discovered
    /// to be wrong by a user.
    /// </summary>
    public void Step(
        Win32.Point cursor,
        bool buttonDown,
        in ModelFraming framing,
        int viewportWidth,
        int viewportHeight)
    {
        if (!Win32.GetWindowRect(_window.Handle, out var bounds))
        {
            return;
        }

        // The window is a borderless popup, so its rect is its client area.
        var clientX = cursor.X - bounds.Left;
        var clientY = cursor.Y - bounds.Top;

        // The test walks every visible drawable's triangles, so it is worth not
        // running it when nothing about the question changed. A still cursor over
        // a slowly animating pet does not need a new answer, and a press always
        // re-tests anyway.
        if (clientX != _lastTestX || clientY != _lastTestY)
        {
            _lastTestX = clientX;
            _lastTestY = clientY;

            HoveredDrawable = _hitTester.TryHit(framing, clientX, clientY, viewportWidth, viewportHeight, out var hit)
                ? hit
                : -1;
        }

        _window.Hovering = Hovering;

        var down = buttonDown;

        if (down && !_pressed)
        {
            _pressed = true;
            var sinceRight = _clock.Elapsed.TotalSeconds - _rightMoved;
            var inRightGesture = _rightDown || sinceRight < RightGestureSeconds;

            // What is *known* decides: our own menu being open, or the right
            // button being part of the same gesture. What is merely *observed* -
            // which window is under the cursor - only gets written down.
            //
            // It used to be a veto too, and that was one check too many: the
            // window is reshaped to the silhouette several times a second, so a
            // point on the edge of the character can be inside the window on one
            // frame and outside it on the next. A veto that fires on that turn of
            // the animation is a pet that ignores a real click for no reason the
            // user can see. The menu case it was added for is covered by
            // MenuOpen above, which cannot be wrong about whether the menu is up.
            _pressIsOurs = !inRightGesture && !_window.MenuOpen;

            if (_pressIsOurs && !IsPetUnder(cursor))
            {
                Log.Info(
                    "press: the window under the cursor is not the pet " +
                    "(reshaping, or something on top); still counted as the pet's");
            }

            if (!_pressIsOurs)
            {
                // Logged once per press: when a click does nothing, the first
                // question is whether it was the pet's click at all, and the
                // answer is not visible from outside the process.
                Log.Info(
                    "press ignored: " +
                    (inRightGesture
                        ? $"the right button is part of this gesture (right down: {_rightDown}, " +
                          $"{sinceRight * 1000:F0}ms ago)"
                        : _window.MenuOpen
                            ? "the pet's menu is open"
                            : "another window is under the cursor") +
                    ", so this click was not the pet's");

                _dragging = false;
                _moved = false;
                return;
            }

            _dragging = Hovering;
            _moved = false;
            _pressCursorX = cursor.X;
            _pressCursorY = cursor.Y;
            _pressWindowX = bounds.Left;
            _pressWindowY = bounds.Top;
            return;
        }

        if (!down && _pressed)
        {
            // A press that never travelled is a click, and only counts if it is
            // still over the character on release.
            if (_pressIsOurs && _dragging && !_moved && HoveredDrawable >= 0)
            {
                _clickedDrawable = HoveredDrawable;
                _clickClientX = clientX;
                _clickClientY = clientY;
            }

            _pressed = false;
            _dragging = false;
            _pressIsOurs = false;
            return;
        }

        if (down && _dragging)
        {
            var deltaX = cursor.X - _pressCursorX;
            var deltaY = cursor.Y - _pressCursorY;

            if (!_moved && (Math.Abs(deltaX) > ClickSlopPixels || Math.Abs(deltaY) > ClickSlopPixels))
            {
                _moved = true;
            }

            if (_moved)
            {
                _window.SetPosition(_pressWindowX + deltaX, _pressWindowY + deltaY);
            }
        }
    }

    /// <summary>
    /// Whether the pet is the window the click would actually go to.
    ///
    /// Position alone is not enough: the pet is a shaped window with a menu that
    /// opens underneath the cursor, so "the cursor is over the model" and "the
    /// pet received the click" are different questions. This is the second one,
    /// and it is what stops the pet reacting to its own menu.
    /// </summary>
    private bool IsPetUnder(Win32.Point cursor)
    {
        var window = Win32.WindowFromPoint(cursor);
        return window == _window.Handle;
    }

    /// <summary>
    /// Reports a completed click once, with the client pixel it landed on - the
    /// caller needs the position to ask which named region was poked.
    /// </summary>
    public bool TryTakeClick(out int drawableIndex, out int clientX, out int clientY)
    {
        drawableIndex = _clickedDrawable;
        clientX = _clickClientX;
        clientY = _clickClientY;

        if (_clickedDrawable < 0)
        {
            return false;
        }

        _clickedDrawable = -1;
        return true;
    }
}
