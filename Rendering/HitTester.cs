using System.Numerics;
using Live2DPet.Cubism;

namespace Live2DPet.Rendering;

/// <summary>
/// Answers "what did the user just click on" against the model's own geometry.
///
/// Not the bounding box, and not a second copy of the mesh: the same deformed
/// vertices and UVs the renderer uploaded this frame, and the same texture atlas,
/// tested for coverage. A pet whose clicks are answered by a rectangle feels
/// like a rectangle, and one whose clicks are answered by stale geometry drifts
/// out of agreement with what is on screen - so this reads the live data and
/// nothing else.
///
/// The test runs front to back, against the render sequence, because that
/// sequence is painter's order: the last thing drawn is the thing on top, and it
/// is the first thing a click should find.
/// </summary>
internal sealed unsafe class HitTester
{
    /// <summary>
    /// Below this the texel counts as empty. Not zero: a bilinear-filtered edge
    /// leaves a fringe of near-transparent pixels, and treating those as solid
    /// makes the character's outline a few pixels larger than it looks.
    /// </summary>
    private const byte DefaultAlphaThreshold = 16;

    private readonly CubismModel _model;
    private readonly ModelTexture[] _textures;

    public HitTester(CubismModel model, ModelTexture[] textures)
    {
        _model = model;
        _textures = textures;
    }

    public byte AlphaThreshold { get; set; } = DefaultAlphaThreshold;

    /// <summary>A drawable has to reach this opacity to be clickable, matching
    /// the renderer's own culling so invisible things cannot be hit.</summary>
    public float MinimumOpacity { get; set; } = 0.001f;

    /// <summary>Drawables the renderer has been told to leave out. A click falls
    /// through them to whatever is behind, the same way the picture does.</summary>
    public bool[] HiddenDrawables { get; set; } = [];

    /// <summary>Patches of a drawable the renderer leaves out by UV. A click falls
    /// through a patch to whatever is behind, the same way the picture does; the
    /// rest of that drawable still answers.</summary>
    public UvDropMap DropUv { get; set; } = UvDropMap.Empty;

    /// <summary>
    /// Tests a window pixel against the model. Returns the frontmost drawable
    /// whose geometry covers the point and whose texture is opaque there.
    /// </summary>
    public bool TryHit(
        in ModelFraming framing,
        float windowX,
        float windowY,
        int viewportWidth,
        int viewportHeight,
        out int drawableIndex)
    {
        drawableIndex = -1;

        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return false;
        }

        var point = framing.ToModel(windowX, windowY, viewportWidth, viewportHeight);
        var drawables = _model.Drawables;

        for (var slot = _model.RenderSequence.Length - 1; slot >= 0; slot--)
        {
            var index = _model.RenderSequence[slot];
            var drawable = drawables[index];

            if (drawable.IndexCount < 3 || drawable.VertexCount == 0)
            {
                continue;
            }

            if (!_model.IsDrawableVisible(index) || _model.GetDrawableOpacity(index) <= MinimumOpacity)
            {
                continue;
            }

            if ((uint)index < (uint)HiddenDrawables.Length && HiddenDrawables[index])
            {
                continue;
            }

            if (!TryInterpolateUv(index, drawable, point, out var uv))
            {
                continue;
            }

            // Cubism UVs put the origin at the bottom left; the alpha copy is
            // indexed like the texture, from the top left.
            if (_textures[drawable.TextureIndex].AlphaAt(uv.X, 1f - uv.Y) <= AlphaThreshold)
            {
                continue;
            }

            drawableIndex = index;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether any triangle of the drawable covers the point, with no regard for
    /// the texture. Used to test a hit area's shape, which is normally invisible
    /// artwork and so has no alpha worth consulting.
    /// </summary>
    public static bool CoversPoint(in DrawableInfo drawable, Vector2 point) =>
        TryFindTriangle(drawable, point, null, -1, out _, out _);

    /// <summary>
    /// Finds the point's UV by locating the triangle that contains it and
    /// interpolating with the same barycentric weights. Testing the triangles
    /// rather than the bounding box is what lets a click pass between the
    /// character's hair and its shoulder.
    /// </summary>
    private bool TryInterpolateUv(int drawableIndex, in DrawableInfo drawable, Vector2 point, out Vector2 uv)
    {
        uv = default;

        if (!TryFindTriangle(drawable, point, DropUv, drawableIndex, out var corners, out var weights))
        {
            return false;
        }

        uv = ToVector2(drawable.Uvs[corners.X]) * weights.X
             + ToVector2(drawable.Uvs[corners.Y]) * weights.Y
             + ToVector2(drawable.Uvs[corners.Z]) * weights.Z;

        return true;
    }

    /// <summary>
    /// The triangle containing the point, as its three vertex indices and the
    /// barycentric weights of the point inside it.
    /// </summary>
    private static bool TryFindTriangle(
        in DrawableInfo drawable,
        Vector2 point,
        UvDropMap? dropUv,
        int drawableIndex,
        out Vector3Int corners,
        out Vector3 weights)
    {
        corners = default;
        weights = default;

        for (var i = 0; i + 2 < drawable.IndexCount; i += 3)
        {
            var i0 = drawable.Indices[i];
            var i1 = drawable.Indices[i + 1];
            var i2 = drawable.Indices[i + 2];

            var a = ToVector2(drawable.Positions[i0]);
            var b = ToVector2(drawable.Positions[i1]);
            var c = ToVector2(drawable.Positions[i2]);

            if (!TryBarycentric(point, a, b, c, out weights))
            {
                continue;
            }

            if (dropUv != null)
            {
                var ua = drawable.Uvs[i0];
                var ub = drawable.Uvs[i1];
                var uc = drawable.Uvs[i2];

                if (dropUv.Drops(
                        drawableIndex,
                        (ua.X + ub.X + uc.X) / 3f,
                        (ua.Y + ub.Y + uc.Y) / 3f))
                {
                    continue;
                }
            }

            corners = new Vector3Int(i0, i1, i2);
            return true;
        }

        return false;
    }

    private readonly record struct Vector3Int(int X, int Y, int Z);

    private static bool TryBarycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out Vector3 weights)
    {
        weights = default;

        var v0 = b - a;
        var v1 = c - a;
        var v2 = p - a;

        var denominator = v0.X * v1.Y - v1.X * v0.Y;
        if (MathF.Abs(denominator) < 1e-12f)
        {
            return false;
        }

        var v = (v2.X * v1.Y - v1.X * v2.Y) / denominator;
        var w = (v0.X * v2.Y - v2.X * v0.Y) / denominator;
        var u = 1f - v - w;

        // A small negative tolerance keeps points on a shared edge from falling
        // through the gap between two triangles that both claim not to contain
        // them.
        const float tolerance = -1e-5f;
        if (u < tolerance || v < tolerance || w < tolerance)
        {
            return false;
        }

        weights = new Vector3(u, v, w);
        return true;
    }

    private static Vector2 ToVector2(CubismNative.Vector2 value) => new(value.X, value.Y);
}
