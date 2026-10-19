using Live2DPet.Platform;
using Live2DPet.Rendering;
using Live2DPet.Cubism;
using System.Numerics;

namespace Live2DPet.App;

/// <summary>
/// Keeps the window shaped to the character.
///
/// Applying a region is not free - the window manager has to recompute the
/// window's shape and its place in the hit test tree, which measured ~1.2ms for
/// a 640x900 window. Doing that five times a second put a visible spike in the
/// frame time tail for no reason, because the silhouette usually has not changed:
/// breathing moves the outline by well under one mask cell.
///
/// So the mask is rebuilt on a timer and applied only when it differs from the
/// last one that was applied. Idle costs nothing, and a motion that actually
/// moves the silhouette refreshes it as fast as the timer allows.
/// </summary>
internal sealed unsafe class WindowShaper
{
    private readonly Silhouette _silhouette;
    private readonly CubismModel _model;
    private readonly PetWindow _window;
    private readonly WindowRegion _region = new();

    /// <summary>
    /// How far the window's shape is grown beyond the artwork, in pixels, and
    /// how far the model is allowed to move before the shape is rebuilt.
    ///
    /// The shape is enforced by the compositor, so anything outside it is not
    /// drawn. If a part moves further than the margin between two rebuilds it
    /// gets cut off - which is a chunk of a tail disappearing and the desktop
    /// showing through. Rebuilding when the model has moved half the margin
    /// makes that impossible rather than unlikely.
    /// </summary>
    private const int MarginPixels = 24;

    private const float RebuildDistancePixels = MarginPixels / 2f;

    private bool[] _applied = [];
    private int _appliedLength;

    private Vector2[] _positions = [];
    private bool _havePositions;

    /// <summary>The window size the current region was built for, so a resize is
    /// noticed even when the pet has not moved an inch.</summary>
    private int _width;
    private int _height;

    /// <summary>
    /// The view the region in hand was built for.
    ///
    /// A window resize is the only thing the size check above notices, and it is not
    /// the only thing that moves the picture: a zoom changes the view inside a window
    /// that never changes size. Without this, a shape built for the pet at one size
    /// stays on a pet drawn at another - which is a character with its edges cut off
    /// by a shape from a moment ago.
    /// </summary>
    private ModelFraming _framing;
    private bool _haveFraming;

    /// <summary>
    /// How much of the mask has to change before the window is reshaped, as a
    /// fraction of the window. Breathing moves the outline by a cell or two all
    /// the time, and reshaping the window for that is 1.1ms spent on a boundary
    /// nobody is aiming at. Drift accumulates, so a real movement - a motion, a
    /// resize - still lands immediately.
    /// </summary>
    private const double ReshapeThreshold = 0.01;

    public WindowShaper(Silhouette silhouette, CubismModel model, PetWindow window)
    {
        _silhouette = silhouette;
        _model = model;
        _window = window;
    }

    /// <summary>How many times the window has actually been reshaped.</summary>
    public int Updates { get; private set; }

    public double Coverage => _silhouette.Coverage;

    /// <summary>
    /// Whether a client point is on the pet, allowing a margin in pixels around
    /// it. Used to decide when the pointer has actually left - a boundary that
    /// has to be forgiving, or moving along the outline would hand focus away
    /// and back several times a second.
    /// </summary>
    public bool Contains(int clientX, int clientY, int marginPixels)
    {
        if (_appliedLength == 0)
        {
            return true;
        }

        var width = _silhouette.Columns * Silhouette.CellSize;
        var height = _silhouette.Rows * Silhouette.CellSize;

        if (clientX < -marginPixels || clientY < -marginPixels ||
            clientX >= width + marginPixels || clientY >= height + marginPixels)
        {
            return false;
        }

        var cells = Math.Max(1, marginPixels / Silhouette.CellSize);
        var firstColumn = Math.Max(0, clientX / Silhouette.CellSize - cells);
        var lastColumn = Math.Min(_silhouette.Columns - 1, clientX / Silhouette.CellSize + cells);
        var firstRow = Math.Max(0, clientY / Silhouette.CellSize - cells);
        var lastRow = Math.Min(_silhouette.Rows - 1, clientY / Silhouette.CellSize + cells);

        for (var row = firstRow; row <= lastRow; row++)
        {
            var offset = row * _silhouette.Columns;

            for (var column = firstColumn; column <= lastColumn; column++)
            {
                if (_applied[offset + column])
                {
                    return true;
                }
            }
        }

        return false;
    }

    private readonly PhaseTimer _maskTimer = new("mask");
    private readonly PhaseTimer _applyTimer = new("apply");

    /// <summary>Where the reshape time goes, for the periodic log.</summary>
    public string TakePhaseSummary() => $"{_maskTimer.TakeSummary()} | {_applyTimer.TakeSummary()}";

    /// <summary>Rebuilds the mask and reshapes the window if the pet moved.</summary>
    public void Update(int width, int height, in ModelFraming framing)
    {
        // A resize moves the goalposts without moving the vertices: the region
        // belongs to the window, so a window that has changed size needs a new one
        // whether or not the pet moved. Asking only about the vertices left the
        // shape behind at the size before, and the model was then cut by a
        // staircase that no longer matched where it was.
        var resized = width != _width || height != _height;
        var reframed = !_haveFraming ||
            framing.MinX != _framing.MinX || framing.MinY != _framing.MinY ||
            framing.MaxX != _framing.MaxX || framing.MaxY != _framing.MaxY;

        // How far the artwork has moved since the shape was last built. Below
        // half the margin, the shape cannot be cutting anything off.
        //
        // Vertices are in model units and the margin is in pixels, so this has
        // to be converted - comparing the two directly made the threshold about
        // eight hundred times too large, which is the same as having none.
        var unitsPerPixel = framing.UnitsPerPixel(width, height);
        var moved = MaxMovement() / unitsPerPixel;

        if (_havePositions && !resized && !reframed && moved < RebuildDistancePixels)
        {
            return;
        }

        _width = width;
        _height = height;
        _framing = framing;
        _haveFraming = true;

        Snapshot();

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var mask = _silhouette.Build(
            width,
            height,
            framing,
            (int)MathF.Ceiling(MarginPixels / (float)Silhouette.CellSize));
        _maskTimer.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        var length = _silhouette.Columns * _silhouette.Rows;
        var span = mask.AsSpan(0, length);

        var applied = _applied.AsSpan(0, _appliedLength);
        if (_applied.Length < length)
        {
            _applied = new bool[length];
        }

        span.CopyTo(_applied);
        _appliedLength = length;
        Updates++;

        _applyTimer.Measure(() => _window.SetShape(_region.FromMask(
            mask,
            _silhouette.Columns,
            _silhouette.Rows,
            Silhouette.CellSize,
            width,
            height)));
    }

    /// <summary>
    /// The largest distance any vertex has travelled since the last snapshot.
    ///
    /// A bounding box would be cheaper and wrong: the tail can swing out of a
    /// shape without the overall box growing. This walks the vertices the mask
    /// is built from, which is the same set that has to stay inside it.
    /// </summary>
    private float MaxMovement()
    {
        if (!_havePositions)
        {
            return float.MaxValue;
        }

        var cursor = 0;
        var worst = 0f;
        var drawables = _model.Drawables;

        for (var i = 0; i < drawables.Length; i++)
        {
            var drawable = drawables[i];

            for (var v = 0; v < drawable.VertexCount; v++)
            {
                var point = drawable.Positions[v];
                var previous = _positions[cursor++];
                var dx = point.X - previous.X;
                var dy = point.Y - previous.Y;
                worst = MathF.Max(worst, dx * dx + dy * dy);
            }
        }

        return MathF.Sqrt(worst);
    }

    /// <summary>Remembers where every vertex was when the shape was built.</summary>
    private void Snapshot()
    {
        var total = 0;
        foreach (var drawable in _model.Drawables)
        {
            total += drawable.VertexCount;
        }

        if (_positions.Length < total)
        {
            _positions = new Vector2[total];
        }

        var cursor = 0;
        foreach (var drawable in _model.Drawables)
        {
            for (var v = 0; v < drawable.VertexCount; v++)
            {
                var point = drawable.Positions[v];
                _positions[cursor++] = new Vector2(point.X, point.Y);
            }
        }

        _havePositions = true;
    }
}
