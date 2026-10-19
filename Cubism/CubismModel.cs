using System.Text;
using Live2DPet.App;
using static Live2DPet.Cubism.CubismNative;

namespace Live2DPet.Cubism;

/// <summary>
/// Managed wrapper around a Cubism Core model.
///
/// This type owns the two unmanaged blocks Core needs (the revived moc and the
/// model instance) and exposes the parts of the Core API the engine actually
/// uses. Everything else in this project treats the model as an opaque bag of
/// already-deformed vertices plus the metadata needed to draw them.
/// </summary>
internal sealed unsafe class CubismModel : IDisposable
{
    private AlignedBuffer? _mocBuffer;
    private AlignedBuffer? _modelBuffer;

    private void* _moc;
    private void* _model;

    // Lazily built name to index maps. Parameter lookup by name happens several
    // times per frame once effects are running, so it must not scan.
    private Dictionary<string, int>? _parameterIndex;
    private Dictionary<string, int>? _partIndex;
    private Dictionary<string, int>? _drawableIndex;

    /// <summary>
    /// Parameter values captured after motion evaluation and before procedural
    /// effects run. Each frame restores this baseline so effects stay purely
    /// additive instead of accumulating on top of themselves.
    /// </summary>
    private float[] _savedParameters = [];

    private CubismModel()
    {
    }

    public int ParameterCount { get; private set; }

    public string[] ParameterIds { get; private set; } = [];

    public float* ParameterValues { get; private set; }

    public float* ParameterMinimums { get; private set; }

    public float* ParameterMaximums { get; private set; }

    public float* ParameterDefaults { get; private set; }

    public int PartCount { get; private set; }

    public string[] PartIds { get; private set; } = [];

    public float* PartOpacities { get; private set; }

    public DrawableInfo[] Drawables { get; private set; } = [];

    /// <summary>Render sequence: <c>Order[i]</c> is the drawable index to draw at step <c>i</c>.</summary>
    public int[] RenderSequence { get; private set; } = [];

    /// <summary>Inverse of <see cref="RenderSequence"/>: the slot each drawable occupies.</summary>
    public int[] RenderSlots { get; private set; } = [];

    /// <summary>Canvas size in model units (pixels divided by pixels-per-unit).</summary>
    public float CanvasWidth { get; private set; }

    public float CanvasHeight { get; private set; }

    public float CanvasOriginX { get; private set; }

    public float CanvasOriginY { get; private set; }

    public float PixelsPerUnit { get; private set; }

    public int TotalVertexCount { get; private set; }

    public int TotalIndexCount { get; private set; }

    public int OffscreenCount { get; private set; }

    public static CubismModel Load(string mocPath)
    {
        var bytes = File.ReadAllBytes(mocPath);
        return Load(bytes);
    }

    public static CubismModel Load(byte[] mocBytes)
    {
        var model = new CubismModel();

        // The moc has to live inside a 64 byte aligned buffer that we own, and
        // "reviving" prepares it for use in place.
        model._mocBuffer = new AlignedBuffer(mocBytes.Length, MocAlignment);
        mocBytes.CopyTo(model._mocBuffer.AsSpan());

        var mocAddress = model._mocBuffer.Pointer;

        if (csmHasMocConsistency(mocAddress, (uint)mocBytes.Length) == 0)
        {
            throw new InvalidDataException($"moc3 failed its consistency check: {mocPathOrUnknown()}");
        }

        model._moc = csmReviveMocInPlace(mocAddress, (uint)mocBytes.Length);
        if (model._moc == null)
        {
            throw new InvalidDataException("csmReviveMocInPlace returned null");
        }

        var modelSize = csmGetSizeofModel(model._moc);
        if (modelSize == 0)
        {
            throw new InvalidDataException("csmGetSizeofModel returned 0");
        }

        model._modelBuffer = new AlignedBuffer((int)modelSize, ModelAlignment);
        model._model = csmInitializeModelInPlace(model._moc, model._modelBuffer.Pointer, modelSize);
        if (model._model == null)
        {
            throw new InvalidDataException("csmInitializeModelInPlace returned null");
        }

        model.ReadStaticData();
        model.SnapshotParameters();
        model.Update();
        return model;

        static string mocPathOrUnknown() => "moc3";
    }

    /// <summary>Runs the deformer: parameter values in, vertex positions out.</summary>
    public void Update() => csmUpdateModel(_model);

    public int GetParameterIndex(string id)
    {
        _parameterIndex ??= BuildIndex(ParameterIds);
        return _parameterIndex.TryGetValue(id, out var index) ? index : -1;
    }

    public int GetPartIndex(string id)
    {
        _partIndex ??= BuildIndex(PartIds);
        return _partIndex.TryGetValue(id, out var index) ? index : -1;
    }

    /// <summary>
    /// Drawable index for an ArtMesh id. Hit areas and expressions address
    /// drawables by name, so this has to not scan.
    /// </summary>
    public int GetDrawableIndex(string id)
    {
        _drawableIndex ??= BuildIndex(Drawables.Select(drawable => drawable.Id).ToArray());
        return _drawableIndex.TryGetValue(id, out var index) ? index : -1;
    }

    public float GetParameterValue(int index) => ParameterValues[index];

    public float GetParameterMinimum(int index) => ParameterMinimums[index];

    public float GetParameterMaximum(int index) => ParameterMaximums[index];

    public float GetParameterDefault(int index) => ParameterDefaults[index];

    /// <summary>Writes a parameter, clamped to the range the rig declares.</summary>
    public void SetParameterValue(int index, float value)
    {
        if ((uint)index >= (uint)ParameterCount)
        {
            return;
        }

        ParameterValues[index] = Math.Clamp(value, ParameterMinimums[index], ParameterMaximums[index]);
    }

    public void AddParameterValue(int index, float value, float weight = 1f)
    {
        if ((uint)index >= (uint)ParameterCount)
        {
            return;
        }

        SetParameterValue(index, ParameterValues[index] + value * weight);
    }

    /// <summary>Restores the post-motion baseline so effects can be re-applied.</summary>
    public void LoadParameters()
    {
        var count = Math.Min(ParameterCount, _savedParameters.Length);
        for (var i = 0; i < count; i++)
        {
            ParameterValues[i] = _savedParameters[i];
        }
    }

    /// <summary>Captures the current values as the next frame's baseline.</summary>
    public void SaveParameters()
    {
        if (_savedParameters.Length != ParameterCount)
        {
            SnapshotParameters();
            return;
        }

        for (var i = 0; i < ParameterCount; i++)
        {
            _savedParameters[i] = ParameterValues[i];
        }
    }

    private void SnapshotParameters()
    {
        _savedParameters = new float[ParameterCount];
        for (var i = 0; i < ParameterCount; i++)
        {
            _savedParameters[i] = ParameterValues[i];
        }
    }

    private static Dictionary<string, int> BuildIndex(string[] ids)
    {
        var map = new Dictionary<string, int>(ids.Length, StringComparer.Ordinal);
        for (var i = 0; i < ids.Length; i++)
        {
            map.TryAdd(ids[i], i);
        }

        return map;
    }

    public bool IsDrawableVisible(int drawableIndex) =>
        (GetDynamicFlags(drawableIndex) & DynamicFlags.IsVisible) != 0;

    public float GetDrawableOpacity(int drawableIndex) =>
        csmGetDrawableOpacities(_model)[drawableIndex];

    public DynamicFlags GetDynamicFlags(int drawableIndex) =>
        (DynamicFlags)csmGetDrawableDynamicFlags(_model)[drawableIndex];

    public static string GetVersionString()
    {
        var version = csmGetVersion();
        return $"{version >> 24}.{(version >> 16) & 0xFF}.{(version >> 8) & 0xFF}.{version & 0xFF}";
    }

    public (float MinX, float MinY, float MaxX, float MaxY) ComputeDefaultPoseBounds()
        => ComputePoseBounds(visibleOnly: false);

    /// <summary>
    /// The same box, but measured over the drawables that are actually on screen.
    ///
    /// A model can carry art its parameters have hidden - a whole second outfit, a
    /// rack of props - and everything in it is still somewhere in model space. The
    /// box over all of it is then the box of the <em>file</em> rather than of the
    /// pet, which is what makes a character sit small and low inside its own window,
    /// and what makes growing that window look like the pet sinking towards the
    /// taskbar. This is the box a resize should be anchored on.
    /// </summary>
    public (float MinX, float MinY, float MaxX, float MaxY) ComputeVisiblePoseBounds()
        => ComputePoseBounds(visibleOnly: true);

    private (float MinX, float MinY, float MaxX, float MaxY) ComputePoseBounds(bool visibleOnly)
    {
        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;

        for (var i = 0; i < Drawables.Length; i++)
        {
            var drawable = Drawables[i];

            if (drawable.VertexCount == 0)
            {
                continue;
            }

            if (visibleOnly && (!IsDrawableVisible(i) || GetDrawableOpacity(i) <= 0.001f))
            {
                continue;
            }

            for (var v = 0; v < drawable.VertexCount; v++)
            {
                var p = drawable.Positions[v];
                minX = MathF.Min(minX, p.X);
                minY = MathF.Min(minY, p.Y);
                maxX = MathF.Max(maxX, p.X);
                maxY = MathF.Max(maxY, p.Y);
            }
        }

        return (minX, minY, maxX, maxY);
    }

    private void ReadStaticData()
    {
        ParameterCount = csmGetParameterCount(_model);
        ParameterIds = ReadStrings(csmGetParameterIds(_model), ParameterCount);
        ParameterMinimums = csmGetParameterMinimumValues(_model);
        ParameterMaximums = csmGetParameterMaximumValues(_model);
        ParameterDefaults = csmGetParameterDefaultValues(_model);
        ParameterValues = csmGetParameterValues(_model);

        PartCount = csmGetPartCount(_model);
        PartIds = ReadStrings(csmGetPartIds(_model), PartCount);
        PartOpacities = csmGetPartOpacities(_model);

        Vector2 sizeInPixels;
        Vector2 originInPixels;
        float pixelsPerUnit;
        csmReadCanvasInfo(_model, &sizeInPixels, &originInPixels, &pixelsPerUnit);

        PixelsPerUnit = pixelsPerUnit <= 0 ? 1f : pixelsPerUnit;
        CanvasWidth = sizeInPixels.X / PixelsPerUnit;
        CanvasHeight = sizeInPixels.Y / PixelsPerUnit;
        CanvasOriginX = originInPixels.X / PixelsPerUnit;
        CanvasOriginY = originInPixels.Y / PixelsPerUnit;

        var drawableCount = csmGetDrawableCount(_model);
        var ids = csmGetDrawableIds(_model);
        var textureIndices = csmGetDrawableTextureIndices(_model);
        var constantFlags = csmGetDrawableConstantFlags(_model);
        var vertexCounts = csmGetDrawableVertexCounts(_model);
        var positions = csmGetDrawableVertexPositions(_model);
        var uvs = csmGetDrawableVertexUvs(_model);
        var indexCounts = csmGetDrawableIndexCounts(_model);
        var indices = csmGetDrawableIndices(_model);
        var maskCounts = csmGetDrawableMaskCounts(_model);
        var masks = csmGetDrawableMasks(_model);
        var multiplyColors = csmGetDrawableMultiplyColors(_model);
        var screenColors = csmGetDrawableScreenColors(_model);
        var renderOrders = csmGetRenderOrders(_model);
        var parentParts = csmGetDrawableParentPartIndices(_model);

        Drawables = new DrawableInfo[drawableCount];
        RenderSequence = new int[drawableCount];
        RenderSlots = new int[drawableCount];

        for (var i = 0; i < drawableCount; i++)
        {
            Drawables[i] = new DrawableInfo
            {
                Index = i,
                Id = ReadString(ids[i]),
                TextureIndex = textureIndices[i],
                ConstantFlags = (ConstantFlags)constantFlags[i],
                VertexCount = vertexCounts[i],
                Positions = positions[i],
                Uvs = uvs[i],
                IndexCount = indexCounts[i],
                Indices = indices[i],
                MaskCount = maskCounts[i],
                Masks = masks[i],
                MultiplyColor = multiplyColors[i],
                ScreenColor = screenColors[i],
                PartIndex = parentParts[i],
            };

            TotalVertexCount += vertexCounts[i];
            TotalIndexCount += indexCounts[i];
        }

        // renderOrders is an inverse permutation: the value is the slot the
        // drawable occupies in the render sequence.
        for (var i = 0; i < drawableCount; i++)
        {
            var slot = renderOrders[i];
            if ((uint)slot < (uint)drawableCount)
            {
                RenderSequence[slot] = i;
                RenderSlots[i] = slot;
            }
        }

        OffscreenCount = csmGetOffscreenCount(_model);
    }

    private static string[] ReadStrings(byte** pointers, int count)
    {
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = ReadString(pointers[i]);
        }

        return result;
    }

    private static string ReadString(byte* pointer)
    {
        if (pointer == null)
        {
            return string.Empty;
        }

        var length = 0;
        while (pointer[length] != 0)
        {
            length++;
        }

        return Encoding.UTF8.GetString(pointer, length);
    }

    public void Dispose()
    {
        // Core has no destroy entry point; dropping the owning buffers is the
        // whole teardown. Dispose the model before the moc.
        _modelBuffer?.Dispose();
        _mocBuffer?.Dispose();
        _modelBuffer = null;
        _mocBuffer = null;
        _model = null;
        _moc = null;
        GC.SuppressFinalize(this);
    }

    ~CubismModel() => Dispose();
}

/// <summary>Everything needed to submit one ArtMesh, captured once at load.</summary>
internal readonly unsafe struct DrawableInfo
{
    public int Index { get; init; }

    public string Id { get; init; }

    public int TextureIndex { get; init; }

    public CubismNative.ConstantFlags ConstantFlags { get; init; }

    public int VertexCount { get; init; }

    /// <summary>Points at Core-owned memory that is rewritten by every <c>Update</c>.</summary>
    public CubismNative.Vector2* Positions { get; init; }

    public CubismNative.Vector2* Uvs { get; init; }

    public int IndexCount { get; init; }

    public ushort* Indices { get; init; }

    public int MaskCount { get; init; }

    public int* Masks { get; init; }

    public CubismNative.Vector4 MultiplyColor { get; init; }

    public CubismNative.Vector4 ScreenColor { get; init; }

    /// <summary>Index into <see cref="CubismModel.PartIds"/>, or -1. The part a
    /// drawable belongs to is what gives a click a meaningful name on models
    /// that do not declare hit areas.</summary>
    public int PartIndex { get; init; }
}
