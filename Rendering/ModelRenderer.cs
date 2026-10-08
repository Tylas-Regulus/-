using System.Numerics;
using System.Runtime.InteropServices;
using Live2DPet.App;
using Live2DPet.Cubism;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Live2DPet.Rendering;

/// <summary>
/// Draws a <see cref="CubismModel"/> into a render target.
///
/// Geometry lives in two vertex streams, split by how often it changes:
///
///   * stream 0 - position and drawable opacity, rewritten every frame;
///   * stream 1 - UV and both tint colours, written once at load.
///
/// The split exists because milestone 5 moved the three per-drawable colours out
/// of the constant buffer and into vertex data. Once a draw call no longer
/// carries per-drawable state, every drawable that shares a texture, a clipping
/// context and a blend mode can be submitted together, which is the only lever
/// that touches the cost the GPU measurement actually found: per-draw overhead.
///
/// The two streams also make the per-frame upload smaller than it used to be.
/// Positions change every frame and tints do not, so uploading 12 bytes per
/// vertex instead of 16 is a small win on top of the batching.
///
/// Batching changes the order draws are issued in, and Live2D has no depth
/// buffer: order <em>is</em> the layering. See <see cref="Place"/> for what keeps
/// that honest.
/// </summary>
internal sealed unsafe class ModelRenderer : IDisposable
{
    /// <summary>Per frame: position (float2) and the drawable's opacity (float).</summary>
    private const int DynamicVertexStride = 12;

    /// <summary>Written once: uv (float2), multiply (float4), screen (float4).</summary>
    private const int StaticVertexStride = 40;

    private const int StaticFloatsPerVertex = StaticVertexStride / sizeof(float);

    private readonly GraphicsDevice _graphics;
    private readonly CubismModel _model;
    private readonly ModelTexture[] _textures;

    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11PixelShader _maskSetupShader;
    private readonly ID3D11PixelShader _maskedPixelShader;
    private readonly ID3D11PixelShader _maskedInvertedPixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11Buffer _vertexBuffer;
    private readonly ID3D11Buffer _staticVertexBuffer;
    private readonly ID3D11Buffer _indexBuffer;
    private readonly ID3D11Buffer _maskIndexBuffer;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11BlendState _normalBlend;
    private readonly ID3D11BlendState _additiveBlend;
    private readonly ID3D11BlendState _multiplicativeBlend;
    private readonly ID3D11BlendState _maskBlend;
    private readonly ID3D11RasterizerState _rasterizer;
    private readonly ID3D11DepthStencilState _depthStencil;
    private readonly ClippingMasks _masks;
    private readonly OffscreenUpscaler _upscaler;
    private readonly GpuTimer _gpuTimer;

    public GpuSummary TakeGpuSummary() => _gpuTimer.TakeSummary();

    // Cached so a run of drawables sharing a shader, mask or texture does not
    // re-set it. Reset after the mask pass, which binds its own.
    private ID3D11PixelShader? _boundPixelShader;
    private ID3D11ShaderResourceView? _boundMask;
    private ID3D11ShaderResourceView? _boundTexture;

    private readonly int[] _vertexStart;
    private readonly int[] _indexStart;
    private readonly float[] _staticData;
    private readonly uint[] _indexData;
    private readonly uint[] _maskIndexData;
    private readonly int _vertexBufferSize;

    private readonly List<Batch> _batches = [];
    private readonly List<Batch> _batchPool = [];
    private readonly List<MaskBatch> _maskBatches = [];

    /// <summary>
    /// The vertices the mask pass rasterises, in a fixed order, and where they
    /// were when the mask buffers were last drawn. See <see cref="MasksNeedDraw"/>.
    /// </summary>
    private (int Drawable, int Vertex)[] _maskSample = [];
    private CubismNative.Vector2[] _maskSamplePositions = [];
    private bool _masksDrawn;

    /// <summary>
    /// How far a clipping shape has to move, in mask texels, before the buffers
    /// are drawn again.
    ///
    /// Half a texel is under a screen pixel of edge movement at full scale - and
    /// the mask is a smooth coverage map sampled with bilinear filtering, so half
    /// a texel of staleness is a quarter of a pixel of edge softness that nobody
    /// can see. It is deliberately not "every other frame": a frame-count rule
    /// would spend the same budget on a model that is holding still and one that
    /// is mid-jump, and only the second one needs it.
    /// </summary>
    private const float MaskMovementTexels = 0.5f;

    // Where the model pass spends its CPU time. "The draw is slow" is not
    // actionable; "the index build is slow" is.
    private readonly PhaseTimer _planTimer = new("plan");
    private readonly PhaseTimer _indexTimer = new("indices");
    private readonly PhaseTimer _maskTimer = new("mask");
    private readonly PhaseTimer _mainTimer = new("submit");

    private bool _staticDataDirty = true;
    private bool _highlightMasked;

    /// <summary>Whether the last frame drew the mask buffers, and how far the
    /// clipping shapes had moved when it was last asked. Diagnostics: the pass
    /// this expensive has to be visible when it is skipped.</summary>
    public bool MaskPassSkipped { get; private set; }

    public float MaskMovementUnits { get; private set; }

    /// <summary>How far the shapes had moved, in mask texels, when the pass was
    /// last considered - against the <see cref="MaskMovementTexels"/> limit.</summary>
    public float MaskMovementInTexels { get; private set; }

    /// <summary>Frames since the mask buffers were actually drawn.</summary>
    public int MaskFramesSinceDraw { get; private set; }

    /// <summary>Draws the mask buffers every frame regardless of movement. Only a
    /// measurement needs this: it is how the skip is proved to change nothing on
    /// a still pose, by comparing the two captures byte for byte.</summary>
    public bool DrawEveryFrameMasks { get; set; }

    private int _mainIndexCount;
    private int _maskIndexCount;

    // Framing, in model units, computed from the default pose. Shared with the
    // hit tester so a click lands where the picture says it should.
    private ModelFraming _framing;

    /// <summary>Draw calls issued by the most recent frame's model pass.</summary>
    public int LastDrawCallCount { get; private set; }

    /// <summary>Draw calls issued by the most recent frame's clipping pass.</summary>
    public int LastMaskDrawCallCount { get; private set; }

    /// <summary>Drawables that survived visibility and opacity culling.</summary>
    public int LastVisibleDrawableCount { get; private set; }

    /// <summary>
    /// Whether drawables sharing a render state are merged into one draw call.
    /// Turning it off keeps the data layout identical and only changes how many
    /// draws are issued, which makes it a controlled experiment rather than a
    /// comparison against a different revision.
    /// </summary>
    public bool Batching { get; set; } = true;

    /// <summary>
    /// Skips mask and model geometry but keeps selecting and clearing the render
    /// target. Diagnostic: it separates the cost of the pass itself - binding a
    /// composition swap chain back buffer and clearing it - from the cost of the
    /// geometry submitted into it.
    /// </summary>
    public bool SkipModelPass { get; set; }

    /// <summary>
    /// Skips the whole pass, clear included. Diagnostic: the GPU timer should
    /// then read zero, which is what makes the other readings mean something.
    /// </summary>
    public bool SkipRenderPass { get; set; }

    /// <summary>Skips the clipping pass but keeps the model pass. Diagnostic:
    /// it says how much of the GPU time the mask buffers own. Masked drawables
    /// then read stale masks, so the picture is wrong on purpose.</summary>
    public bool SkipClippingPass { get; set; }

    /// <summary>Draws masked ArtMeshes in a flat magenta so the parts affected
    /// by the clipping implementation can be seen. Diagnostic only.</summary>
    public bool HighlightMaskedDrawables
    {
        get => _highlightMasked;
        set
        {
            if (_highlightMasked == value)
            {
                return;
            }

            _highlightMasked = value;
            // The debug colours are baked into the static stream, so a change
            // has to be re-uploaded rather than applied per draw.
            _staticDataDirty = true;
        }
    }

    /// <summary>Linear resolution multiplier for the model pass. 1.0 renders
    /// straight to the swap chain; below that the model is drawn smaller and
    /// stretched, which is the cheapest quality to give up.</summary>
    public float RenderScale { get; set; } = 1f;

    /// <summary>The projection the most recent frame used, also used to turn a
    /// click back into model space.</summary>
    public ModelFraming Framing => _framing;

    /// <summary>Indices of drawables that act as clipping shapes for something
    /// else. Tinted green when <see cref="HighlightMaskedDrawables"/> is on, so
    /// the clip shape and the clipped content can be compared in one frame.</summary>
    public HashSet<int> MaskSourceDrawables { get; } = [];

    /// <summary>
    /// Drawables to pick out of the picture: they are tinted and everything else
    /// is dimmed so that one part of a 269 drawable model can be recognised.
    ///
    /// The editor is the reason this exists. This model's parts are called
    /// `Part14` and `ArtMesh37_Skinning`, and no name like that tells anyone what
    /// it is - but lighting it up on the model does, immediately, and it is the
    /// same picture the user is looking at rather than a diagram of it.
    /// </summary>
    public HashSet<int>? HighlightedDrawables { get; private set; }

    /// <summary>Sets the highlighted part of the picture. Null clears it.</summary>
    public void Highlight(IEnumerable<int>? drawables)
    {
        HighlightedDrawables = drawables == null ? null : [.. drawables];

        // The tint rides in the static vertex attributes, so they have to be
        // rebuilt for it to be visible this frame.
        _staticDataDirty = true;
    }

    public ModelRenderer(GraphicsDevice graphics, CubismModel model, ModelTexture[] textures)
    {
        _graphics = graphics;
        _model = model;
        _textures = textures;

        var drawables = model.Drawables;
        _vertexStart = new int[drawables.Length];
        _indexStart = new int[drawables.Length];

        var vertexCursor = 0;
        var indexCursor = 0;

        for (var i = 0; i < drawables.Length; i++)
        {
            _vertexStart[i] = vertexCursor;
            _indexStart[i] = indexCursor;

            vertexCursor += drawables[i].VertexCount;
            indexCursor += drawables[i].IndexCount;
        }

        _vertexBufferSize = Math.Max(DynamicVertexStride, vertexCursor * DynamicVertexStride);
        _staticData = new float[Math.Max(StaticFloatsPerVertex, vertexCursor * StaticFloatsPerVertex)];
        _indexData = new uint[Math.Max(1, model.TotalIndexCount)];

        var vertexBytecode = Compile("VsMain", "vs_5_0");
        var maskedVertexBytecode = Compile("VsMasked", "vs_5_0");

        // VsMasked is used for every draw; it only computes a mask coordinate
        // that the unmasked pixel shaders ignore. Both vertex shaders declare the
        // same inputs, so one input layout serves both passes.
        _vertexShader = graphics.Device.CreateVertexShader(maskedVertexBytecode.Span);
        _pixelShader = graphics.Device.CreatePixelShader(Compile("PsMain", "ps_5_0").Span);
        _maskSetupShader = graphics.Device.CreatePixelShader(Compile("PsMaskSetup", "ps_5_0").Span);
        _maskedPixelShader = graphics.Device.CreatePixelShader(Compile("PsMainMasked", "ps_5_0").Span);
        _maskedInvertedPixelShader = graphics.Device.CreatePixelShader(Compile("PsMainMaskedInverted", "ps_5_0").Span);
        _inputLayout = graphics.Device.CreateInputLayout(
            [
                // Stream 0, rewritten every frame.
                new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                new InputElementDescription("OPACITY", 0, Format.R32_Float, 8, 0),

                // Stream 1, uploaded once.
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 0, 1),
                new InputElementDescription("TEXCOORD", 1, Format.R32G32B32A32_Float, 8, 1),
                new InputElementDescription("TEXCOORD", 2, Format.R32G32B32A32_Float, 24, 1),
            ],
            maskedVertexBytecode.Span);

        _masks = new ClippingMasks(graphics, model);
        _maskSample = BuildMaskSample();
        _maskSamplePositions = new CubismNative.Vector2[_maskSample.Length];
        _upscaler = new OffscreenUpscaler(graphics);
        _gpuTimer = new GpuTimer(graphics);

        // A context draws all of its shapes in one call, so the merged index
        // buffer has to hold every context's shapes at once. Shapes are shared
        // between contexts, so the capacity is a sum rather than a maximum.
        var maskIndexCapacity = 0;
        foreach (var clipping in _masks.Contexts)
        {
            foreach (var drawableIndex in clipping.MaskDrawables)
            {
                maskIndexCapacity += model.Drawables[drawableIndex].IndexCount;
            }
        }

        _maskIndexData = new uint[Math.Max(1, maskIndexCapacity)];

        _constantBuffer = graphics.Device.CreateBuffer(
            new BufferDescription(
                (uint)Marshal.SizeOf<Live2DConstants>(),
                BindFlags.ConstantBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write));

        _vertexBuffer = graphics.Device.CreateBuffer(
            new BufferDescription(
                (uint)_vertexBufferSize,
                BindFlags.VertexBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write));

        // Dynamic rather than immutable so the debug tint can be re-uploaded
        // when it is toggled; in normal use it is written once, before the first
        // frame.
        _staticVertexBuffer = graphics.Device.CreateBuffer(
            new BufferDescription(
                (uint)(_staticData.Length * sizeof(float)),
                BindFlags.VertexBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write));

        _indexBuffer = graphics.Device.CreateBuffer(
            new BufferDescription(
                (uint)Math.Max(4, _indexData.Length * sizeof(uint)),
                BindFlags.IndexBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write));

        _maskIndexBuffer = graphics.Device.CreateBuffer(
            new BufferDescription(
                (uint)Math.Max(4, _maskIndexData.Length * sizeof(uint)),
                BindFlags.IndexBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write));

        _sampler = graphics.Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });

        _normalBlend = CreateBlendState(
            Blend.One, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha);
        _additiveBlend = CreateBlendState(
            Blend.One, Blend.One, Blend.Zero, Blend.One);
        _multiplicativeBlend = CreateBlendState(
            Blend.DestinationColor, Blend.InverseSourceAlpha, Blend.Zero, Blend.One);

        // The mask buffer is cleared to white and the shapes are multiplied
        // down to black, so that "inside the shape" reads as zero.
        _maskBlend = CreateBlendState(
            Blend.Zero, Blend.InverseSourceColor, Blend.Zero, Blend.InverseSourceAlpha);

        // Live2D relies on painter's order, not depth, so depth testing is off
        // and culling is disabled outright (double sidedness is a per drawable
        // flag, and honouring it is a later refinement).
        _rasterizer = graphics.Device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            FrontCounterClockwise = false,
            DepthClipEnable = false,
            ScissorEnable = false,
            MultisampleEnable = false,
            AntialiasedLineEnable = false,
        });

        _depthStencil = graphics.Device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = false,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Always,
            StencilEnable = false,
        });

        SetFramingToDefaultPose();
    }

    /// <summary>
    /// Frames the model using the bounding box it actually occupies in its
    /// default pose. That is independent of how the artist laid the canvas out,
    /// which matters because Live2D canvases often carry a lot of headroom.
    /// </summary>
    public void SetFramingToDefaultPose(float margin = 0.96f)
    {
        var (minX, minY, maxX, maxY) = _model.ComputeDefaultPoseBounds();

        if (!float.IsFinite(minX) || maxX - minX <= 0 || maxY - minY <= 0)
        {
            // Fall back to the declared canvas so a bad measurement still shows
            // something instead of an empty screen.
            var halfWidth = _model.CanvasWidth * 0.5f;
            var halfHeight = _model.CanvasHeight * 0.5f;
            minX = -halfWidth;
            maxX = halfWidth;
            minY = -halfHeight;
            maxY = halfHeight;
            Log.Warn("default pose bounds unusable; framing with the canvas size instead");
        }

        var centreX = (minX + maxX) * 0.5f;
        var centreY = (minY + maxY) * 0.5f;
        var halfSpanX = MathF.Max((maxX - minX) * 0.5f, 1e-3f);
        var halfSpanY = MathF.Max((maxY - minY) * 0.5f, 1e-3f);

        _framing = new ModelFraming
        {
            MinX = centreX - halfSpanX / margin,
            MinY = centreY - halfSpanY / margin,
            MaxX = centreX + halfSpanX / margin,
            MaxY = centreY + halfSpanY / margin,
        };

        Log.Info(
            $"default pose bounds: x[{minX:F1}, {maxX:F1}] y[{minY:F1}, {maxY:F1}] " +
            $"({maxX - minX:F1} x {maxY - minY:F1} model units)");
    }

    /// <summary>
    /// Rewrites the per-frame stream: every vertex's position plus the opacity
    /// of the drawable it belongs to. The static stream is uploaded here too on
    /// the first frame, and again if the debug tint is toggled.
    /// </summary>
    public void UploadGeometry()
    {
        if (_staticDataDirty)
        {
            UploadStaticAttributes();
        }

        var mapped = _graphics.Context.Map(_vertexBuffer, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var destination = (byte*)mapped.DataPointer;
            var drawables = _model.Drawables;

            for (var i = 0; i < drawables.Length; i++)
            {
                var drawable = drawables[i];
                var opacity = _model.GetDrawableOpacity(i);
                var target = destination + (long)_vertexStart[i] * DynamicVertexStride;

                for (var v = 0; v < drawable.VertexCount; v++)
                {
                    var position = drawable.Positions[v];
                    var vertex = (float*)(target + (long)v * DynamicVertexStride);
                    vertex[0] = position.X;
                    vertex[1] = position.Y;
                    vertex[2] = opacity;
                }
            }
        }
        finally
        {
            _graphics.Context.Unmap(_vertexBuffer);
        }
    }

    private void UploadStaticAttributes()
    {
        var drawables = _model.Drawables;
        var cursor = 0;

        for (var i = 0; i < drawables.Length; i++)
        {
            var drawable = drawables[i];
            var multiply = SelectDebugColor(drawable);
            var screen = ToVector4(drawable.ScreenColor, Vector4.Zero);

            for (var v = 0; v < drawable.VertexCount; v++)
            {
                var uv = drawable.Uvs[v];

                _staticData[cursor + 0] = uv.X;
                _staticData[cursor + 1] = uv.Y;
                _staticData[cursor + 2] = multiply.X;
                _staticData[cursor + 3] = multiply.Y;
                _staticData[cursor + 4] = multiply.Z;
                _staticData[cursor + 5] = multiply.W;
                _staticData[cursor + 6] = screen.X;
                _staticData[cursor + 7] = screen.Y;
                _staticData[cursor + 8] = screen.Z;
                _staticData[cursor + 9] = screen.W;

                cursor += StaticFloatsPerVertex;
            }
        }

        var mapped = _graphics.Context.Map(_staticVertexBuffer, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bytes = (long)cursor * sizeof(float);
            fixed (float* source = _staticData)
            {
                Buffer.MemoryCopy(source, (void*)mapped.DataPointer, bytes, bytes);
            }
        }
        finally
        {
            _graphics.Context.Unmap(_staticVertexBuffer);
        }

        _staticDataDirty = false;
    }

    /// <summary>Per-stage CPU breakdown of the model pass, for the periodic log.</summary>
    public string TakePhaseSummary() =>
        $"{_planTimer.TakeSummary()} | {_indexTimer.TakeSummary()} | " +
        $"{_maskTimer.TakeSummary()} | {_mainTimer.TakeSummary()}";

    /// <summary>
    /// How much of the clipping work is being driven by content rather than by
    /// the mask shapes alone, for the periodic log. A non-zero number is the
    /// measure of a bug that used to cut visible chunks out of the model.
    /// </summary>
    public string DescribeClipping() =>
        _masks.Contexts.Length == 0
            ? "no clipping contexts"
            : $"{_masks.ContextsNeedingMoreRoom}/{_masks.Contexts.Length} contexts need room for " +
              $"their content, worst {_masks.WorstOverflow:P1} (context {_masks.WorstOverflowContext})";

    /// <summary>Per-context clipping detail, for the diagnostic dump.</summary>
    public void ReportClipping(Action<string> write) => _masks.ReportLayouts(_model, write);

    public void Draw(ID3D11RenderTargetView renderTarget, int viewportWidth, int viewportHeight)
    {
        var context = _graphics.Context;
        _gpuTimer.BeginFrame();

        if (SkipRenderPass)
        {
            _gpuTimer.EndFrame();
            return;
        }

        if (!SkipModelPass)
        {
            _planTimer.Measure(PlanBatches);
            _indexTimer.Measure(UploadIndexData);
        }

        if (_masks.Contexts.Length > 0 && !SkipClippingPass && !SkipModelPass)
        {
            MaskPassSkipped = false;

            if (MasksNeedDraw())
            {
                _masks.UpdateLayouts(_model);
                _maskTimer.Measure(RenderMaskBuffers);
                InvalidateStateCache();
                RememberMaskPositions();
                MaskFramesSinceDraw = 0;
            }
            else
            {
                // Nothing was submitted for the masks this frame, and the report
                // says so rather than repeating last frame's count.
                LastMaskDrawCallCount = 0;
                MaskPassSkipped = true;
                MaskFramesSinceDraw++;
            }
        }

        // At reduced scale the model goes to an offscreen target first and is
        // stretched onto the swap chain afterwards.
        var target = renderTarget;
        var targetWidth = viewportWidth;
        var targetHeight = viewportHeight;

        if (RenderScale < 0.999f &&
            _upscaler.EnsureTarget(viewportWidth, viewportHeight, RenderScale) is { } scaled)
        {
            target = scaled;
            targetWidth = _upscaler.Width;
            targetHeight = _upscaler.Height;
        }

        // Premultiplied black: fully transparent yet still well defined.
        context.ClearRenderTargetView(target, new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f));

        context.OMSetRenderTargets(target, null);
        context.RSSetViewport(0, 0, targetWidth, targetHeight);
        context.RSSetState(_rasterizer);
        context.OMSetDepthStencilState(_depthStencil);

        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetInputLayout(_inputLayout);
        context.IASetVertexBuffer(0, _vertexBuffer, DynamicVertexStride, 0);
        context.IASetVertexBuffer(1, _staticVertexBuffer, StaticVertexStride, 0);
        context.IASetIndexBuffer(_indexBuffer, Format.R32_UInt, 0);

        context.VSSetShader(_vertexShader);
        context.PSSetShader(_pixelShader);
        context.VSSetConstantBuffer(0, _constantBuffer);
        context.PSSetConstantBuffer(0, _constantBuffer);
        context.PSSetSampler(0, _sampler);

        var projection = _framing.Projection(targetWidth, targetHeight);

        if (!SkipModelPass)
        {
            _mainTimer.Measure(() => IssueBatches(projection));
        }

        if (!ReferenceEquals(target, renderTarget))
        {
            _upscaler.Blit(renderTarget, viewportWidth, viewportHeight);
        }

        _gpuTimer.EndFrame();
    }

    /// <summary>
    /// Walks the render sequence and sorts every visible drawable into a batch.
    ///
    /// Batching reorders draws, and with no depth buffer the order is the
    /// layering, so a merged draw is only correct when nothing about the picture
    /// changes. The rule here is the narrow one: a drawable may join a batch that
    /// was opened earlier in the sequence only if it does not overlap anything it
    /// would then be drawn behind. Overlap is tested on the model space bounding
    /// box, which is conservative - it can refuse a merge that would have been
    /// fine, never accept one that would have been wrong.
    /// </summary>
    private void PlanBatches()
    {
        _batches.Clear();
        var drawables = _model.Drawables;
        var visible = 0;

        for (var slot = 0; slot < _model.RenderSequence.Length; slot++)
        {
            var index = _model.RenderSequence[slot];
            var drawable = drawables[index];

            if (drawable.IndexCount == 0 || drawable.VertexCount == 0)
            {
                continue;
            }

            if (!_model.IsDrawableVisible(index))
            {
                continue;
            }

            if (_model.GetDrawableOpacity(index) <= 0.001f)
            {
                continue;
            }

            visible++;

            var key = new BatchKey(
                drawable.TextureIndex,
                _masks.ContextForDrawable(index),
                BlendKind(drawable.ConstantFlags),
                (drawable.ConstantFlags & CubismNative.ConstantFlags.IsInvertedMask) != 0);

            var (minX, minY, maxX, maxY) = ComputeBounds(drawable);
            Place(index, key, minX, minY, maxX, maxY);
        }

        LastVisibleDrawableCount = visible;
    }

    private void Place(int index, BatchKey key, float minX, float minY, float maxX, float maxY)
    {
        if (Batching)
        {
            // Nearest matching batch first, so a drawable moves as short a
            // distance forward as the picture allows.
            for (var b = _batches.Count - 1; b >= 0; b--)
            {
                var candidate = _batches[b];

                if (candidate.Key != key)
                {
                    continue;
                }

                var blocked = false;
                for (var t = b + 1; t < _batches.Count; t++)
                {
                    if (_batches[t].Overlaps(minX, minY, maxX, maxY))
                    {
                        blocked = true;
                        break;
                    }
                }

                if (blocked)
                {
                    continue;
                }

                candidate.Members.Add(index);
                candidate.Include(minX, minY, maxX, maxY);
                return;
            }
        }

        var batch = RentBatch(key);
        batch.Members.Add(index);
        batch.Include(minX, minY, maxX, maxY);
    }

    private Batch RentBatch(BatchKey key)
    {
        if (_batches.Count < _batchPool.Count)
        {
            var reused = _batchPool[_batches.Count];
            reused.Reset(key);
            _batches.Add(reused);
            return reused;
        }

        var created = new Batch(key);
        _batchPool.Add(created);
        _batches.Add(created);
        return created;
    }

    /// <summary>
    /// Flattens the plan into index data. The single draw call per batch needs
    /// absolute vertex indices, because <c>BaseVertexLocation</c> can only carry
    /// one offset per draw and a batch contains many drawables. Baking the
    /// offsets in also lets a batch's geometry be non-contiguous in the vertex
    /// buffer, which is what makes merging possible at all.
    /// </summary>
    private void UploadIndexData()
    {
        var drawables = _model.Drawables;
        var cursor = 0;

        foreach (var batch in _batches)
        {
            batch.IndexStart = cursor;

            foreach (var member in batch.Members)
            {
                var drawable = drawables[member];
                var baseVertex = _vertexStart[member];

                for (var k = 0; k < drawable.IndexCount; k++)
                {
                    _indexData[cursor++] = (uint)(baseVertex + drawable.Indices[k]);
                }
            }

            batch.IndexCount = cursor - batch.IndexStart;
        }

        _mainIndexCount = cursor;

        if (cursor > 0)
        {
            var mapped = _graphics.Context.Map(_indexBuffer, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var bytes = (long)cursor * sizeof(uint);
                fixed (uint* source = _indexData)
                {
                    Buffer.MemoryCopy(source, (void*)mapped.DataPointer, bytes, bytes);
                }
            }
            finally
            {
                _graphics.Context.Unmap(_indexBuffer);
            }
        }

        ScheduleMaskPass();
    }

    /// <summary>
    /// One draw per clipping context per texture.
    ///
    /// Order inside the mask pass does not matter the way it does in the model
    /// pass: the shapes are combined with a commutative multiply into their own
    /// channel and never sample each other. A context still splits by texture
    /// because the shape coverage comes from the texture's alpha channel.
    /// </summary>
    private void ScheduleMaskPass()
    {
        _maskBatches.Clear();
        var drawables = _model.Drawables;
        var cursor = 0;

        for (var c = 0; c < _masks.Contexts.Length; c++)
        {
            var clipping = _masks.Contexts[c];

            var textureIndex = -1;
            var start = 0;

            foreach (var drawableIndex in clipping.MaskDrawables)
            {
                var drawable = drawables[drawableIndex];

                if (drawable.IndexCount == 0 || drawable.VertexCount == 0)
                {
                    continue;
                }

                // Deliberately no visibility test here. A clipping shape is
                // usually invisible as artwork - that is what makes it a shape -
                // and it still has to be drawn into the mask. The Cubism
                // renderer skips a shape only when its vertices did not move, as
                // an optimisation that relies on the mask buffer surviving the
                // frame; this engine clears and redraws the buffers every frame,
                // so it skips nothing.

                if (drawable.TextureIndex != textureIndex)
                {
                    if (textureIndex >= 0)
                    {
                        _maskBatches.Add(new MaskBatch(c, textureIndex, start, cursor - start));
                    }

                    textureIndex = drawable.TextureIndex;
                    start = cursor;
                }

                var baseVertex = _vertexStart[drawableIndex];
                for (var k = 0; k < drawable.IndexCount; k++)
                {
                    _maskIndexData[cursor++] = (uint)(baseVertex + drawable.Indices[k]);
                }
            }

            if (textureIndex >= 0)
            {
                _maskBatches.Add(new MaskBatch(c, textureIndex, start, cursor - start));
            }
        }

        _maskIndexCount = cursor;

        if (cursor > 0)
        {
            var mapped = _graphics.Context.Map(_maskIndexBuffer, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var bytes = (long)cursor * sizeof(uint);
                fixed (uint* source = _maskIndexData)
                {
                    Buffer.MemoryCopy(source, (void*)mapped.DataPointer, bytes, bytes);
                }
            }
            finally
            {
                _graphics.Context.Unmap(_maskIndexBuffer);
            }
        }
    }

    private void IssueBatches(Matrix4x4 projection)
    {
        var context = _graphics.Context;
        LastDrawCallCount = 0;

        foreach (var batch in _batches)
        {
            if (batch.IndexCount == 0)
            {
                continue;
            }

            var clipMatrix = Matrix4x4.Identity;
            var channelFlag = Vector4.One;
            var pixelShader = _pixelShader;
            ID3D11ShaderResourceView? maskView = null;

            if (batch.Key.ContextIndex >= 0)
            {
                var clipping = _masks.Contexts[batch.Key.ContextIndex];
                clipMatrix = clipping.ClipMatrix;
                channelFlag = ClippingMasks.ChannelFlag(clipping.ChannelIndex);
                maskView = _masks.Resource(clipping.BufferIndex);
                pixelShader = batch.Key.Inverted ? _maskedInvertedPixelShader : _maskedPixelShader;
            }

            UpdateConstants(new Live2DConstants
            {
                ProjectMatrix = projection,
                ClipMatrix = clipMatrix,
                ChannelFlag = channelFlag,
            });

            context.OMSetBlendState(SelectBlendState(batch.Key.BlendKind));

            if (!ReferenceEquals(_boundPixelShader, pixelShader))
            {
                context.PSSetShader(pixelShader);
                _boundPixelShader = pixelShader;
            }

            if (!ReferenceEquals(_boundMask, maskView))
            {
                context.PSSetShaderResource(1, maskView);
                _boundMask = maskView;
            }

            var texture = _textures[batch.Key.TextureIndex];
            if (!ReferenceEquals(_boundTexture, texture.View))
            {
                context.PSSetShaderResource(0, texture.View);
                _boundTexture = texture.View;
            }

            context.DrawIndexed((uint)batch.IndexCount, (uint)batch.IndexStart, 0);
            LastDrawCallCount++;
        }
    }

    /// <summary>
    /// Whether the mask buffers have to be drawn again this frame.
    ///
    /// The mask pass is eight draws and two clears into two 256x256 buffers, and
    /// on a machine that draws with the CPU that is **4.5ms of a frame at one
    /// logical processor** - over a quarter of a 60 FPS budget - for a coverage
    /// map whose shapes move about a tenth of a pixel between frames. Drawing it
    /// every frame spends the budget on a picture nobody can tell apart.
    ///
    /// The test is the shapes' own movement, measured against the frame the
    /// buffers were last drawn, not a frame counter: a model that is holding still
    /// or breathing draws them rarely, and a model mid-jump draws them every frame
    /// without any of this being a special case.
    ///
    /// Skipping the *layout* as well as the draw is deliberate and load bearing:
    /// the layout decides where the main pass samples the mask, so re-laying it out
    /// while reusing last frame's texels would move the clip edge twice as far as
    /// the shape moved.
    /// </summary>
    private bool MasksNeedDraw()
    {
        if (!_masksDrawn || _maskSample.Length == 0 || DrawEveryFrameMasks)
        {
            return true;
        }

        var texel = UnitsPerMaskTexel();

        if (texel <= 0f)
        {
            return true;
        }

        var moved = 0f;

        for (var i = 0; i < _maskSample.Length; i++)
        {
            var (drawable, vertex) = _maskSample[i];
            var now = _model.Drawables[drawable].Positions[vertex];
            var was = _maskSamplePositions[i];

            var distance = MathF.Max(MathF.Abs(now.X - was.X), MathF.Abs(now.Y - was.Y));
            if (distance > moved)
            {
                moved = distance;
            }
        }

        MaskMovementUnits = moved;
        MaskMovementInTexels = moved / texel;
        return moved >= texel * MaskMovementTexels;
    }

    /// <summary>Model units one mask texel covers, taken from the layout, which is
    /// the box the shapes were last framed on. Zero before the first layout.</summary>
    private float UnitsPerMaskTexel()
    {
        var widest = 0f;

        foreach (var clipping in _masks.Contexts)
        {
            var box = MathF.Max(clipping.MaxX - clipping.MinX, clipping.MaxY - clipping.MinY);
            if (box > widest)
            {
                widest = box;
            }
        }

        return widest > 0f ? widest / ClippingMasks.BufferSize : 0f;
    }

    /// <summary>The clipping shapes the mask pass rasterises, in a fixed order, so
    /// their movement can be measured against the frame they were drawn.</summary>
    private (int Drawable, int Vertex)[] BuildMaskSample()
    {
        var sample = new List<(int, int)>();
        var seen = new HashSet<(int, int)>();

        foreach (var clipping in _masks.Contexts)
        {
            foreach (var index in clipping.MaskDrawables)
            {
                for (var vertex = 0; vertex < _model.Drawables[index].VertexCount; vertex++)
                {
                    if (seen.Add((index, vertex)))
                    {
                        sample.Add((index, vertex));
                    }
                }
            }
        }

        return [.. sample];
    }

    private void RememberMaskPositions()
    {
        for (var i = 0; i < _maskSample.Length; i++)
        {
            var (drawable, vertex) = _maskSample[i];
            _maskSamplePositions[i] = _model.Drawables[drawable].Positions[vertex];
        }

        _masksDrawn = true;
    }

    /// <summary>
    /// Renders every clipping shape into its mask buffer.
    ///
    /// Each buffer is cleared to white first. The shapes are then blended with
    /// ZERO / INV_SRC_COLOR, which multiplies the destination down towards black
    /// wherever a shape covers it. The result is a buffer that is black inside
    /// the clipping shape and white outside, which is the sense the main pass
    /// expects when it computes <c>1 - sample</c>.
    /// </summary>
    private void RenderMaskBuffers()
    {
        var context = _graphics.Context;

        // The previous frame's model pass may have left a mask texture bound as
        // a shader resource. Binding it as a render target at the same time is
        // not allowed, so release it first.
        context.PSSetShaderResource(1, null);

        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetInputLayout(_inputLayout);
        context.IASetVertexBuffer(0, _vertexBuffer, DynamicVertexStride, 0);
        context.IASetVertexBuffer(1, _staticVertexBuffer, StaticVertexStride, 0);
        context.IASetIndexBuffer(_maskIndexBuffer, Format.R32_UInt, 0);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_maskSetupShader);
        context.VSSetConstantBuffer(0, _constantBuffer);
        context.PSSetConstantBuffer(0, _constantBuffer);
        context.PSSetSampler(0, _sampler);
        context.RSSetState(_rasterizer);
        context.OMSetDepthStencilState(_depthStencil);
        context.OMSetBlendState(_maskBlend);

        var white = new Vortice.Mathematics.Color4(1f, 1f, 1f, 1f);
        for (var bufferIndex = 0; bufferIndex < _masks.BufferCount; bufferIndex++)
        {
            var target = _masks.RenderTarget(bufferIndex);
            context.OMSetRenderTargets(target, null);
            context.RSSetViewport(0, 0, ClippingMasks.BufferSize, ClippingMasks.BufferSize);
            context.ClearRenderTargetView(target, white);
        }

        LastMaskDrawCallCount = 0;
        var boundBuffer = -1;
        var boundContext = -1;
        var boundTexture = -1;

        foreach (var batch in _maskBatches)
        {
            var clipping = _masks.Contexts[batch.ContextIndex];

            if (clipping.BufferIndex != boundBuffer)
            {
                boundBuffer = clipping.BufferIndex;
                context.OMSetRenderTargets(_masks.RenderTarget(boundBuffer), null);
                context.RSSetViewport(0, 0, ClippingMasks.BufferSize, ClippingMasks.BufferSize);
            }

            if (batch.ContextIndex != boundContext)
            {
                boundContext = batch.ContextIndex;
                UpdateConstants(new Live2DConstants
                {
                    ProjectMatrix = clipping.MaskProjection,
                    ClipMatrix = Matrix4x4.Identity,
                    ChannelFlag = ClippingMasks.ChannelFlag(clipping.ChannelIndex),
                });
            }

            if (batch.TextureIndex != boundTexture)
            {
                boundTexture = batch.TextureIndex;
                context.PSSetShaderResource(0, _textures[boundTexture].View);
            }

            context.DrawIndexed((uint)batch.IndexCount, (uint)batch.IndexStart, 0);
            LastMaskDrawCallCount++;
        }
    }

    private void InvalidateStateCache()
    {
        _boundPixelShader = null;
        _boundMask = null;
        _boundTexture = null;
    }

    private Vector4 SelectDebugColor(DrawableInfo drawable)
    {
        if (_highlightMasked)
        {
            // Green marks the shape doing the clipping, magenta the content
            // being clipped. Content reaching outside green is what a missing
            // mask implementation would let through.
            if (MaskSourceDrawables.Contains(drawable.Index))
            {
                return new Vector4(0f, 1f, 0f, 1f);
            }

            if (drawable.MaskCount > 0)
            {
                return new Vector4(1f, 0f, 1f, 1f);
            }
        }

        if (HighlightedDrawables is { Count: > 0 } highlighted)
        {
            // Amber for the part in question, grey for the rest of the pet. The
            // dimmed copy is what makes the shape legible: a lit blob on its own
            // says nothing about where on the character it sits.
            return highlighted.Contains(drawable.Index)
                ? new Vector4(1f, 0.72f, 0.25f, 1f)
                : new Vector4(0.32f, 0.32f, 0.34f, 1f);
        }

        return ToVector4(drawable.MultiplyColor, Vector4.One);
    }

    private static (float MinX, float MinY, float MaxX, float MaxY) ComputeBounds(DrawableInfo drawable)
    {
        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;

        for (var v = 0; v < drawable.VertexCount; v++)
        {
            var point = drawable.Positions[v];
            minX = MathF.Min(minX, point.X);
            minY = MathF.Min(minY, point.Y);
            maxX = MathF.Max(maxX, point.X);
            maxY = MathF.Max(maxY, point.Y);
        }

        return (minX, minY, maxX, maxY);
    }

    /// <summary>
    private void UpdateConstants(Live2DConstants constants)
    {
        var mapped = _graphics.Context.Map(_constantBuffer, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            *(Live2DConstants*)mapped.DataPointer = constants;
        }
        finally
        {
            _graphics.Context.Unmap(_constantBuffer);
        }
    }

    private static int BlendKind(CubismNative.ConstantFlags flags)
    {
        if ((flags & CubismNative.ConstantFlags.BlendAdditive) != 0)
        {
            return 1;
        }

        if ((flags & CubismNative.ConstantFlags.BlendMultiplicative) != 0)
        {
            return 2;
        }

        return 0;
    }

    private ID3D11BlendState SelectBlendState(int kind) => kind switch
    {
        1 => _additiveBlend,
        2 => _multiplicativeBlend,
        _ => _normalBlend,
    };

    private ID3D11BlendState CreateBlendState(
        Blend sourceColor,
        Blend destinationColor,
        Blend sourceAlpha,
        Blend destinationAlpha)
    {
        var description = new BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
        };

        description.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = sourceColor,
            DestinationBlend = destinationColor,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = sourceAlpha,
            DestinationBlendAlpha = destinationAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };

        return _graphics.Device.CreateBlendState(description);
    }

    private static Vector4 ToVector4(CubismNative.Vector4 value, Vector4 fallback)
    {
        var result = new Vector4(value.X, value.Y, value.Z, value.W);

        // Some models leave these unset; treat an all-zero vector as "identity"
        // for multiply and "no-op" for screen.
        if (result == Vector4.Zero && fallback != Vector4.Zero)
        {
            return fallback;
        }

        return result;
    }

    private static ReadOnlyMemory<byte> Compile(string entryPoint, string profile) =>
        ShaderCompiler.Compile(Live2DShader.Source, entryPoint, profile);

    /// <summary>What has to match for two drawables to share a draw call.</summary>
    private readonly record struct BatchKey(int TextureIndex, int ContextIndex, int BlendKind, bool Inverted);

    private readonly record struct MaskBatch(int ContextIndex, int TextureIndex, int IndexStart, int IndexCount);

    private sealed class Batch(BatchKey key)
    {
        public BatchKey Key { get; private set; } = key;

        public List<int> Members { get; } = [];

        public int IndexStart { get; set; }

        public int IndexCount { get; set; }

        private float _minX = float.MaxValue;
        private float _minY = float.MaxValue;
        private float _maxX = float.MinValue;
        private float _maxY = float.MinValue;

        public void Reset(BatchKey key)
        {
            Key = key;
            Members.Clear();
            IndexStart = 0;
            IndexCount = 0;
            _minX = float.MaxValue;
            _minY = float.MaxValue;
            _maxX = float.MinValue;
            _maxY = float.MinValue;
        }

        public void Include(float minX, float minY, float maxX, float maxY)
        {
            _minX = MathF.Min(_minX, minX);
            _minY = MathF.Min(_minY, minY);
            _maxX = MathF.Max(_maxX, maxX);
            _maxY = MathF.Max(_maxY, maxY);
        }

        public bool Overlaps(float minX, float minY, float maxX, float maxY) =>
            !(_maxX <= minX || maxX <= _minX || _maxY <= minY || maxY <= _minY);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Live2DConstants
    {
        public Matrix4x4 ProjectMatrix;
        public Matrix4x4 ClipMatrix;
        public Vector4 ChannelFlag;
    }

    /// <summary>Per-batch summary for the periodic log.</summary>
    public string DescribePlan()
    {
        var largest = 0;
        var singletons = 0;

        foreach (var batch in _batches)
        {
            largest = Math.Max(largest, batch.Members.Count);
            if (batch.Members.Count == 1)
            {
                singletons++;
            }
        }

        return $"{LastVisibleDrawableCount} visible -> {_batches.Count} batch(es), " +
               $"largest {largest}, {singletons} singleton, " +
               $"{_mainIndexCount / 3} tris main, {_maskIndexCount / 3} tris mask";
    }

    public void Dispose()
    {
        _depthStencil.Dispose();
        _rasterizer.Dispose();
        _masks.Dispose();
        _upscaler.Dispose();
        _gpuTimer.Dispose();
        _maskBlend.Dispose();
        _multiplicativeBlend.Dispose();
        _additiveBlend.Dispose();
        _normalBlend.Dispose();
        _sampler.Dispose();
        _maskIndexBuffer.Dispose();
        _indexBuffer.Dispose();
        _staticVertexBuffer.Dispose();
        _vertexBuffer.Dispose();
        _constantBuffer.Dispose();
        _inputLayout.Dispose();
        _maskedInvertedPixelShader.Dispose();
        _maskedPixelShader.Dispose();
        _maskSetupShader.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();

        foreach (var texture in _textures)
        {
            texture.Dispose();
        }
    }
}
