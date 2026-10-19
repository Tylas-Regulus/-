using System.Numerics;
using Live2DPet.App;
using Live2DPet.Cubism;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Live2DPet.Rendering;

/// <summary>
/// One unique combination of clipping shapes, plus where it lives in the mask
/// buffer and how model space maps onto it.
/// </summary>
internal sealed class ClippingContext
{
    public required int[] MaskDrawables { get; init; }

    /// <summary>
    /// Drawables that use this mask, filled in once the whole model has been
    /// classified. They matter to the layout: the mask texture has to cover
    /// everything it is asked about, not just the shape that defines it.
    /// </summary>
    public int[] ClippedDrawables { get; set; } = [];

    /// <summary>
    /// How far the clipped content reached outside the mask shapes' own box on
    /// the last layout, as a fraction of that box. Diagnostic, and the evidence
    /// for why the layout is not framed on the shapes alone.
    /// </summary>
    public float ContentOverflow { get; private set; }

    /// <summary>The same number in model units, which is what actually decides
    /// how far outside the mask texture the content reaches.</summary>
    public float OverflowUnits { get; private set; }

    public float ShapeMinX { get; private set; }

    public float ShapeMinY { get; private set; }

    public float ShapeMaxX { get; private set; }

    public float ShapeMaxY { get; private set; }

    public float ContentMinX { get; private set; }

    public float ContentMinY { get; private set; }

    public float ContentMaxX { get; private set; }

    public float ContentMaxY { get; private set; }

    public int BufferIndex { get; init; }

    /// <summary>0 to 3, selecting R, G, B or A of the mask texture.</summary>
    public int ChannelIndex { get; init; }

    /// <summary>Layout of the clipping shapes in model space, padded.</summary>
    public float MinX { get; private set; }

    public float MinY { get; private set; }

    public float MaxX { get; private set; }

    public float MaxY { get; private set; }

    /// <summary>Model space to mask texture UV, for the main pass.</summary>
    public Matrix4x4 ClipMatrix { get; private set; } = Matrix4x4.Identity;

    /// <summary>Model space to the mask buffer's clip space, for the mask pass.</summary>
    public Matrix4x4 MaskProjection { get; private set; } = Matrix4x4.Identity;

    public unsafe void UpdateLayout(CubismModel model, float padding, bool frameOnContent)
    {
        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;

        Accumulate(model, MaskDrawables, ref minX, ref minY, ref maxX, ref maxY);

        var shapeMinX = minX;
        var shapeMinY = minY;
        var shapeMaxX = maxX;
        var shapeMaxY = maxY;

        // The shape decides what the mask looks like; the content decides how
        // much room the mask texture has to cover. Framing on the shape alone
        // leaves anything outside it sampling the texture's clamped edge, which
        // reads as "outside the shape" and cuts the content off along a straight
        // line. Kept switchable so the difference can be captured and compared.
        if (frameOnContent)
        {
            // Only what is actually on screen. A model can carry art its parameters
            // have hidden - this one hides twenty-odd tools and a whole second outfit
            // - and a hidden drawable is still somewhere in model space: its bounds
            // stretched every context's box to the size of the whole character, which
            // is what made those masks too coarse to cut with.
            Accumulate(model, ClippedDrawables, ref minX, ref minY, ref maxX, ref maxY, visibleOnly: true);
        }

        // A mask sampled outside its own texture clamps to the edge, and the
        // edge reads as "outside the shape" - so anything the texture does not
        // cover gets cut off along a straight line. That is the artefact this
        // bounds union exists to prevent.
        ShapeMinX = shapeMinX;
        ShapeMinY = shapeMinY;
        ShapeMaxX = shapeMaxX;
        ShapeMaxY = shapeMaxY;

        ContentMinX = minX;
        ContentMinY = minY;
        ContentMaxX = maxX;
        ContentMaxY = maxY;

        ContentOverflow = Overflow(shapeMinX, shapeMinY, shapeMaxX, shapeMaxY, minX, minY, maxX, maxY);

        OverflowUnits =
            float.IsFinite(shapeMinX) && float.IsFinite(minX)
                ? MathF.Max(
                    0f,
                    MathF.Max(
                        MathF.Max(shapeMinX - minX, shapeMinY - minY),
                        MathF.Max(maxX - shapeMaxX, maxY - shapeMaxY)))
                : 0f;

        if (!float.IsFinite(minX) || maxX - minX <= 0f || maxY - minY <= 0f)
        {
            // A degenerate layout would divide by zero. Park the context off
            // screen; anything using it ends up fully clipped, which is the
            // right answer for a mask that has collapsed to nothing.
            minX = minY = 0f;
            maxX = maxY = 1f;
        }

        // Padding keeps linear filtering from pulling the mask edge inward on
        // the pixels right at the boundary.
        var padX = (maxX - minX) * padding;
        var padY = (maxY - minY) * padding;

        MinX = minX - padX;
        MaxX = maxX + padX;
        MinY = minY - padY;
        MaxY = maxY + padY;

        var width = MaxX - MinX;
        var height = MaxY - MinY;

        // UV mapping. V is flipped: the mask buffer is a render target, so its
        // row zero is at the top, while model Y grows upwards.
        ClipMatrix =
            Matrix4x4.CreateScale(1f / width, -1f / height, 1f) *
            Matrix4x4.CreateTranslation(-MinX / width, 1f + MinY / height, 0f);

        // Clip space mapping for the mask pass. Note there is no Y flip here:
        // clip space and the render target agree, and the flip is introduced
        // above when sampling.
        MaskProjection =
            Matrix4x4.CreateScale(2f / width, 2f / height, 1f) *
            Matrix4x4.CreateTranslation(-1f - 2f * MinX / width, -1f - 2f * MinY / height, 0f);
    }

    private static unsafe void Accumulate(
        CubismModel model,
        int[] drawables,
        ref float minX,
        ref float minY,
        ref float maxX,
        ref float maxY,
        bool visibleOnly = false)
    {
        foreach (var index in drawables)
        {
            if ((uint)index >= (uint)model.Drawables.Length)
            {
                continue;
            }

            if (visibleOnly &&
                (!model.IsDrawableVisible(index) || model.GetDrawableOpacity(index) <= 0.001f))
            {
                continue;
            }

            var drawable = model.Drawables[index];
            for (var v = 0; v < drawable.VertexCount; v++)
            {
                var point = drawable.Positions[v];
                minX = MathF.Min(minX, point.X);
                minY = MathF.Min(minY, point.Y);
                maxX = MathF.Max(maxX, point.X);
                maxY = MathF.Max(maxY, point.Y);
            }
        }
    }

    /// <summary>
    /// How far (in model units) the content box escapes the shape box, as a
    /// fraction of the shape box's larger side. Zero when the content fits.
    /// </summary>
    private static float Overflow(
        float shapeMinX,
        float shapeMinY,
        float shapeMaxX,
        float shapeMaxY,
        float contentMinX,
        float contentMinY,
        float contentMaxX,
        float contentMaxY)
    {
        if (!float.IsFinite(shapeMinX) || !float.IsFinite(contentMinX))
        {
            return 0f;
        }

        var span = MathF.Max(MathF.Max(shapeMaxX - shapeMinX, shapeMaxY - shapeMinY), 1e-6f);

        var escaped = MathF.Max(
            MathF.Max(shapeMinX - contentMinX, shapeMinY - contentMinY),
            MathF.Max(contentMaxX - shapeMaxX, contentMaxY - shapeMaxY));

        return MathF.Max(0f, escaped) / span;
    }
}

/// <summary>
/// Owns the mask render targets and the drawable-to-context mapping.
///
/// Cubism packs several clipping shapes into one texture's channels, so a
/// 256x256 RGBA target carries four independent masks. Each context here is
/// given a whole target rather than a sub-rectangle: it wastes resolution, but
/// it removes the scissor rectangle the stock renderer needs, and resolution is
/// the cheap part. The sample model needs eight contexts, so two targets.
/// </summary>
internal sealed class ClippingMasks : IDisposable
{
    public const int BufferSize = 256;
    private const int Channels = 4;
    private const float LayoutPadding = 0.02f;

    private readonly GraphicsDevice _graphics;
    private readonly ID3D11Texture2D[] _textures;
    private readonly ID3D11RenderTargetView[] _renderTargets;
    private readonly ID3D11ShaderResourceView[] _resources;
    private readonly int[] _contextByDrawable;

    public unsafe ClippingMasks(GraphicsDevice graphics, CubismModel model)
    {
        _graphics = graphics;

        _contextByDrawable = new int[model.Drawables.Length];
        Array.Fill(_contextByDrawable, -1);

        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var contexts = new List<ClippingContext>();

        for (var i = 0; i < model.Drawables.Length; i++)
        {
            var drawable = model.Drawables[i];
            if (drawable.MaskCount <= 0)
            {
                continue;
            }

            var masks = new int[drawable.MaskCount];
            for (var m = 0; m < drawable.MaskCount; m++)
            {
                masks[m] = drawable.Masks[m];
            }

            // Two drawables clip identically if the same shapes are involved,
            // regardless of the order they were listed in.
            var ordered = masks.OrderBy(index => index).ToArray();
            var key = string.Join(',', ordered);

            if (!byKey.TryGetValue(key, out var contextIndex))
            {
                contextIndex = contexts.Count;
                contexts.Add(new ClippingContext
                {
                    MaskDrawables = ordered,
                    BufferIndex = contextIndex / Channels,
                    ChannelIndex = contextIndex % Channels,
                });
                byKey[key] = contextIndex;
            }

            _contextByDrawable[i] = contextIndex;
        }

        Contexts = contexts.ToArray();

        // Second pass: a context now knows which drawables it masks, and the
        // layout needs that to size the mask texture over everything it will be
        // asked about.
        for (var c = 0; c < Contexts.Length; c++)
        {
            var users = new List<int>();

            for (var i = 0; i < _contextByDrawable.Length; i++)
            {
                if (_contextByDrawable[i] == c)
                {
                    users.Add(i);
                }
            }

            Contexts[c].ClippedDrawables = users.ToArray();
        }

        var bufferCount = Math.Max(1, (Contexts.Length + Channels - 1) / Channels);

        _textures = new ID3D11Texture2D[bufferCount];
        _renderTargets = new ID3D11RenderTargetView[bufferCount];
        _resources = new ID3D11ShaderResourceView[bufferCount];

        var description = new Texture2DDescription
        {
            Width = BufferSize,
            Height = BufferSize,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        for (var i = 0; i < bufferCount; i++)
        {
            _textures[i] = graphics.Device.CreateTexture2D(description);
            _renderTargets[i] = graphics.Device.CreateRenderTargetView(_textures[i]);
            _resources[i] = graphics.Device.CreateShaderResourceView(_textures[i]);
        }

        if (Contexts.Length > 0)
        {
            Log.Info(
                $"clipping: {Contexts.Length} contexts across {bufferCount} mask buffer(s)");
        }
    }

    public ClippingContext[] Contexts { get; }

    public int BufferCount => _textures.Length;

    public int ContextForDrawable(int drawableIndex) => _contextByDrawable[drawableIndex];

    public ID3D11RenderTargetView RenderTarget(int bufferIndex) => _renderTargets[bufferIndex];

    public ID3D11ShaderResourceView Resource(int bufferIndex) => _resources[bufferIndex];

    /// <summary>The mask texture itself, for the diagnostic that copies it out.
    /// Nothing in the render path should need this.</summary>
    public ID3D11Texture2D Texture(int bufferIndex) => _textures[bufferIndex];

    public void UpdateLayouts(CubismModel model, bool frameOnContent = false)
    {
        var needingRoom = 0;
        var worst = 0f;
        var worstContext = -1;

        foreach (var context in Contexts)
        {
            context.UpdateLayout(model, LayoutPadding, frameOnContent);

            if (context.ContentOverflow > 0.005f)
            {
                needingRoom++;
            }

            if (context.ContentOverflow > worst)
            {
                worst = context.ContentOverflow;
                worstContext = Array.IndexOf(Contexts, context);
            }
        }

        ContextsNeedingMoreRoom = needingRoom;
        WorstOverflow = worst;
        WorstOverflowContext = worstContext;
    }

    /// <summary>Contexts whose masked content reaches outside the mask shapes'
    /// own bounding box on this frame. If this is ever non-zero, the layout has
    /// to include the content - see <see cref="ClippingContext.UpdateLayout"/>.</summary>
    public int ContextsNeedingMoreRoom { get; private set; }

    public float WorstOverflow { get; private set; }

    public int WorstOverflowContext { get; private set; } = -1;

    /// <summary>
    /// Prints what each context is made of and how far its content reaches
    /// compared with its shapes. Diagnostic: the sizes involved are not
    /// something to reason about from a ratio alone.
    /// </summary>
    public void ReportLayouts(CubismModel model, Action<string> write)
    {
        for (var c = 0; c < Contexts.Length; c++)
        {
            var context = Contexts[c];

            write(
                $"  context {c}: buffer {context.BufferIndex} channel {context.ChannelIndex}, " +
                $"shape box ({context.ShapeMinX:F4},{context.ShapeMinY:F4})-({context.ShapeMaxX:F4},{context.ShapeMaxY:F4}) " +
                $"= {context.ShapeMaxX - context.ShapeMinX:F4} x {context.ShapeMaxY - context.ShapeMinY:F4}, " +
                $"content box ({context.ContentMinX:F4},{context.ContentMinY:F4})-({context.ContentMaxX:F4},{context.ContentMaxY:F4}) " +
                $"= {context.ContentMaxX - context.ContentMinX:F4} x {context.ContentMaxY - context.ContentMinY:F4}, " +
                $"escape {context.OverflowUnits:F4} units ({context.ContentOverflow:P0})");

            foreach (var index in context.MaskDrawables)
            {
                write($"      shape   [{index,3}] {model.Drawables[index].Id} " +
                      $"(opacity {model.GetDrawableOpacity(index):F2}, visible {model.IsDrawableVisible(index)})");
            }

            foreach (var index in context.ClippedDrawables)
            {
                write($"      content [{index,3}] {model.Drawables[index].Id} " +
                      $"(opacity {model.GetDrawableOpacity(index):F2}, visible {model.IsDrawableVisible(index)})");
            }
        }
    }

    /// <summary>The channel selector a masked drawable has to be told to read.</summary>
    public static Vector4 ChannelFlag(int channelIndex) => channelIndex switch
    {
        0 => new Vector4(1f, 0f, 0f, 0f),
        1 => new Vector4(0f, 1f, 0f, 0f),
        2 => new Vector4(0f, 0f, 1f, 0f),
        _ => new Vector4(0f, 0f, 0f, 1f),
    };

    public void Dispose()
    {
        foreach (var resource in _resources)
        {
            resource.Dispose();
        }

        foreach (var target in _renderTargets)
        {
            target.Dispose();
        }

        foreach (var texture in _textures)
        {
            texture.Dispose();
        }
    }
}
