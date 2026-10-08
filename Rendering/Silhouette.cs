using Live2DPet.Cubism;

namespace Live2DPet.Rendering;

/// <summary>
/// A coarse coverage mask of where the model actually draws, used to give the
/// window a shape.
///
/// The window cannot be a 640x900 rectangle: everything else on the desktop
/// would be behind a piece of glass the size of the pet, and clicks aimed at
/// what is beside it would land on the pet instead. A window *region* is the one
/// mechanism the window manager itself enforces, so clicks outside it are routed
/// by the system to whatever is underneath rather than being handed to us and
/// dropped.
///
/// The mask is built from the mesh, not the texture: every triangle contributes
/// its bounding rectangle. That makes it a superset of the drawn pixels, which
/// is the safe direction - a region that is slightly too generous swallows a few
/// clicks that do nothing, where one that is too tight would cut into the
/// character. The precise answer to "did the user click the character" is still
/// the per-pixel alpha test in <see cref="HitTester"/>; this only decides which
/// part of the screen the window is allowed to occupy at all.
/// </summary>
internal sealed unsafe class Silhouette
{
    /// <summary>
    /// Mask resolution. Eight pixels is a compromise measured rather than
    /// guessed: at four, a rebuild cost 1.35ms and put a visible spike in a
    /// 16.7ms frame five times a second; at eight it is a quarter of the cells
    /// and the region is only coarser by a few pixels around the outline, which
    /// nobody aims a click at.
    /// </summary>
    public const int CellSize = 8;

    private readonly CubismModel _model;
    private bool[] _cells = [];

    public Silhouette(CubismModel model) => _model = model;

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    /// <summary>Fraction of the window the pet currently occupies.</summary>
    public double Coverage { get; private set; }

    /// <summary>
    /// Rebuilds the mask for the current pose. The returned array is reused
    /// between calls, so callers must copy anything they keep.
    /// </summary>
    public bool[] Build(int width, int height, in ModelFraming framing, int marginCells = 0)
    {
        Columns = Math.Max(1, width / CellSize);
        Rows = Math.Max(1, height / CellSize);

        var required = Columns * Rows;
        if (_cells.Length < required)
        {
            _cells = new bool[required];
        }
        else
        {
            Array.Clear(_cells, 0, required);
        }

        var drawables = _model.Drawables;
        var transform = framing.ToWindowTransform(width, height);

        for (var slot = 0; slot < _model.RenderSequence.Length; slot++)
        {
            var index = _model.RenderSequence[slot];
            var drawable = drawables[index];

            if (drawable.IndexCount < 3 || drawable.VertexCount == 0)
            {
                continue;
            }

            if (!_model.IsDrawableVisible(index) || _model.GetDrawableOpacity(index) <= 0.001f)
            {
                continue;
            }

            for (var i = 0; i + 2 < drawable.IndexCount; i += 3)
            {
                var a = transform.Apply(
                    drawable.Positions[drawable.Indices[i]].X,
                    drawable.Positions[drawable.Indices[i]].Y);
                var b = transform.Apply(
                    drawable.Positions[drawable.Indices[i + 1]].X,
                    drawable.Positions[drawable.Indices[i + 1]].Y);
                var c = transform.Apply(
                    drawable.Positions[drawable.Indices[i + 2]].X,
                    drawable.Positions[drawable.Indices[i + 2]].Y);

                var minX = MathF.Min(a.X, MathF.Min(b.X, c.X));
                var maxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
                var minY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
                var maxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));

                var firstColumn = Math.Clamp((int)(minX / CellSize), 0, Columns - 1);
                var lastColumn = Math.Clamp((int)(maxX / CellSize), 0, Columns - 1);
                var firstRow = Math.Clamp((int)(minY / CellSize), 0, Rows - 1);
                var lastRow = Math.Clamp((int)(maxY / CellSize), 0, Rows - 1);

                // A triangle entirely off the window clamps onto an edge cell;
                // skipping those keeps the border honest.
                if (maxX < 0 || maxY < 0 || minX > width || minY > height)
                {
                    continue;
                }

                for (var row = firstRow; row <= lastRow; row++)
                {
                    // Fill whole spans rather than testing cell by cell: the
                    // fill is the bulk of this function and this is a memset.
                    _cells.AsSpan(row * Columns + firstColumn, lastColumn - firstColumn + 1).Fill(true);
                }
            }
        }

        if (marginCells > 0)
        {
            Dilate(marginCells, required);
        }

        var filled = 0;
        foreach (var cell in _cells.AsSpan(0, required))
        {
            if (cell)
            {
                filled++;
            }
        }

        Coverage = (double)filled / required;
        return _cells;
    }

    /// <summary>
    /// Grows the mask outwards by whole cells.
    ///
    /// The window's shape is applied to the compositor, and the compositor is
    /// what clips the picture - so the shape has to be a *superset* of the
    /// artwork with room to spare, or a part that moved since the last reshape
    /// gets cut off. This is the "room to spare".
    ///
    /// Done by walking each row's runs and filling the same columns in the rows
    /// above and below, which is a handful of span fills rather than a
    /// per-cell neighbourhood test.
    /// </summary>
    private void Dilate(int cells, int required)
    {
        var source = (bool[])_cells.Clone();

        for (var row = 0; row < Rows; row++)
        {
            var offset = row * Columns;
            var column = 0;

            while (column < Columns)
            {
                if (!source[offset + column])
                {
                    column++;
                    continue;
                }

                var start = column;
                while (column < Columns && source[offset + column])
                {
                    column++;
                }

                var first = Math.Max(0, start - cells);
                var last = Math.Min(Columns - 1, column - 1 + cells);
                var firstRow = Math.Max(0, row - cells);
                var lastRow = Math.Min(Rows - 1, row + cells);

                for (var r = firstRow; r <= lastRow; r++)
                {
                    _cells.AsSpan(r * Columns + first, last - first + 1).Fill(true);
                }
            }
        }
    }
}
