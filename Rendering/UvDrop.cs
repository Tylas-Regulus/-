using System.Globalization;

namespace Live2DPet.Rendering;

/// <summary>
/// A rectangle of texture coordinates, in the unit square the model's UVs use.
///
/// This exists because a drawable is not always one piece of artwork: a model
/// can carry a watermark and real artwork in the same drawable, and then it is
/// either both or neither. A patch of the texture is finer than that, and -
/// unlike a rectangle in window space - it survives every pose, because a
/// triangle keeps its UVs however the mesh is deformed.
/// </summary>
internal readonly record struct UvRect(float U0, float V0, float U1, float V1)
{
    public bool Contains(float u, float v) => u >= U0 && u <= U1 && v >= V0 && v <= V1;

    /// <summary>
    /// Reads <c>u0,v0,u1,v1</c>. The corners are put in order, so a rectangle
    /// typed right-to-left still means the patch the author was looking at.
    /// Anything that is not four finite numbers is refused: a typo should leave
    /// the model alone rather than drop arbitrary triangles.
    /// </summary>
    public static bool TryParse(string text, out UvRect rect)
    {
        rect = default;

        var fields = text.Split(',', StringSplitOptions.TrimEntries);

        if (fields.Length != 4)
        {
            return false;
        }

        Span<float> values = stackalloc float[4];

        for (var i = 0; i < values.Length; i++)
        {
            if (!float.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) ||
                !float.IsFinite(values[i]))
            {
                return false;
            }
        }

        rect = new UvRect(
            MathF.Min(values[0], values[2]),
            MathF.Min(values[1], values[3]),
            MathF.Max(values[0], values[2]),
            MathF.Max(values[1], values[3]));

        return true;
    }

    public override string ToString() =>
        string.Join(
            ',',
            U0.ToString("0.####", CultureInfo.InvariantCulture),
            V0.ToString("0.####", CultureInfo.InvariantCulture),
            U1.ToString("0.####", CultureInfo.InvariantCulture),
            V1.ToString("0.####", CultureInfo.InvariantCulture));
}

/// <summary>
/// Which drawables have patches of their artwork left out, and which patches.
///
/// Shared by the renderer and the hit tester so a click falls through a patch
/// the way the picture does. Indexed like <see cref="CubismModel.Drawables"/>;
/// a drawable with no entry costs one bounds check.
/// </summary>
internal sealed class UvDropMap
{
    public static readonly UvDropMap Empty = new([]);

    private readonly List<UvRect>?[] _rects;

    public UvDropMap(List<UvRect>?[] rects) => _rects = rects;

    /// <summary>The patches this drawable keeps out of the picture, or null.</summary>
    public IReadOnlyList<UvRect>? For(int drawableIndex) =>
        (uint)drawableIndex < (uint)_rects.Length ? _rects[drawableIndex] : null;

    /// <summary>Whether this UV of this drawable is one of the patches.</summary>
    public bool Drops(int drawableIndex, float u, float v)
    {
        var rects = For(drawableIndex);

        if (rects == null)
        {
            return false;
        }

        for (var i = 0; i < rects.Count; i++)
        {
            if (rects[i].Contains(u, v))
            {
                return true;
            }
        }

        return false;
    }
}
