using System.Numerics;

namespace Live2DPet.Rendering;

/// <summary>
/// Which part of model space the viewport shows, and the mapping in both
/// directions between that space and window pixels.
///
/// The inverse direction matters as much as the forward one. A click arrives as
/// a window pixel, and deciding what was clicked means turning it back into the
/// coordinates the vertices are already in. One type owning both directions is
/// what keeps the hit test and the picture from disagreeing: they cannot drift,
/// because there is only ever one projection.
///
/// Nothing here mentions the render scale. At reduced scale the model is drawn
/// into a smaller offscreen target and then stretched over the whole window, so
/// the pixel a click arrived at maps to the same place either way.
/// </summary>
internal struct ModelFraming
{
    public float MinX;

    public float MinY;

    public float MaxX;

    public float MaxY;

    public readonly float CentreX => (MinX + MaxX) * 0.5f;

    public readonly float CentreY => (MinY + MaxY) * 0.5f;

    public readonly float Width => MathF.Max(MaxX - MinX, 1e-3f);

    public readonly float Height => MathF.Max(MaxY - MinY, 1e-3f);

    /// <summary>Model units per pixel, equal on both axes so pixels stay square.</summary>
    public readonly float UnitsPerPixel(int viewportWidth, int viewportHeight) =>
        MathF.Min(Width / viewportWidth, Height / viewportHeight);

    /// <summary>
    /// Model space to clip space. Clip space is a unit square stretched over the
    /// viewport, so the two axes need different scales unless the viewport
    /// happens to match the model's aspect.
    /// </summary>
    public readonly Matrix4x4 Projection(int viewportWidth, int viewportHeight)
    {
        var unitsPerPixel = UnitsPerPixel(viewportWidth, viewportHeight);
        var scaleX = 2f / (viewportWidth * unitsPerPixel);
        var scaleY = 2f / (viewportHeight * unitsPerPixel);

        return Matrix4x4.CreateTranslation(-CentreX, -CentreY, 0f)
               * Matrix4x4.CreateScale(scaleX, scaleY, 1f);
    }

    /// <summary>
    /// Window pixel to model space: the exact inverse of <see cref="Projection"/>,
    /// written out rather than inverted so the two cannot disagree about a sign.
    /// </summary>
    public readonly Vector2 ToModel(float x, float y, int viewportWidth, int viewportHeight)
    {
        var unitsPerPixel = UnitsPerPixel(viewportWidth, viewportHeight);

        // Window y grows downwards; clip space y grows upwards.
        var ndcX = x / viewportWidth * 2f - 1f;
        var ndcY = 1f - y / viewportHeight * 2f;

        return new Vector2(
            CentreX + ndcX * viewportWidth * unitsPerPixel * 0.5f,
            CentreY + ndcY * viewportHeight * unitsPerPixel * 0.5f);
    }

    /// <summary>
    /// Model space to window pixels, the inverse of <see cref="ToModel"/>. Used
    /// to place things against the model rather than against the window.
    /// </summary>
    public readonly Vector2 ToWindow(float modelX, float modelY, int viewportWidth, int viewportHeight) =>
        ToWindowTransform(viewportWidth, viewportHeight).Apply(modelX, modelY);

    /// <summary>
    /// The same mapping as <see cref="ToWindow"/>, precomputed into two multiplies
    /// and two adds. The per-vertex form divides four times, which is free for one
    /// click and not for fifty thousand vertices.
    /// </summary>
    public readonly WindowTransform ToWindowTransform(int viewportWidth, int viewportHeight)
    {
        var unitsPerPixel = UnitsPerPixel(viewportWidth, viewportHeight);

        // Worked out once from the projection rather than guessed: the viewport
        // width cancels, leaving one model unit mapped to 1/unitsPerPixel pixels
        // about the centre. Getting this factor wrong by two is invisible to a
        // spot check near the middle of the window and obvious in the coverage.
        var scale = 1f / unitsPerPixel;

        return new WindowTransform(
            ScaleX: scale,
            OffsetX: 0.5f * viewportWidth - CentreX * scale,
            ScaleY: -scale,
            OffsetY: 0.5f * viewportHeight + CentreY * scale);
    }

    public readonly record struct WindowTransform(float ScaleX, float OffsetX, float ScaleY, float OffsetY)
    {
        public Vector2 Apply(float modelX, float modelY) =>
            new(modelX * ScaleX + OffsetX, modelY * ScaleY + OffsetY);
    }
}
