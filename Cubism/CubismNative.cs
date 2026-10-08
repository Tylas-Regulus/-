using System.Runtime.InteropServices;

namespace Live2DPet.Cubism;

/// <summary>
/// Raw P/Invoke surface of Live2D Cubism Core.
///
/// Signatures mirror <c>Core/include/Live2DCubismCore.h</c> from
/// CubismSdkForNative-5-r.5 one for one. The header is the only authority here:
/// if a signature below ever disagrees with it, the header wins.
///
/// Notes that matter:
///   * The x64 exports are undecorated (see <c>dumpbin /exports</c>), so no
///     name mangling hints are required.
///   * On x64 Windows there is a single calling convention, so Cdecl is correct
///     even though the header says __stdcall for 32-bit builds.
///   * Callers must provide the memory: mocs need 64 byte alignment, models 16.
/// </summary>
internal static unsafe class CubismNative
{
    private const string Dll = "Live2DCubismCore";

    public const nuint MocAlignment = 64;
    public const nuint ModelAlignment = 16;

    // ---------------------------------------------------------------- version

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint csmGetVersion();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int csmGetLatestMocVersion();

    // ------------------------------------------------------------- consistency

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int csmHasMocConsistency(void* address, uint size);

    // -------------------------------------------------------------------- moc

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void* csmReviveMocInPlace(void* address, uint size);

    // ------------------------------------------------------------------ model

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint csmGetSizeofModel(void* moc);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void* csmInitializeModelInPlace(void* moc, void* address, uint size);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void csmUpdateModel(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void csmResetDrawableDynamicFlags(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void csmReadCanvasInfo(
        void* model,
        Vector2* outSizeInPixels,
        Vector2* outOriginInPixels,
        float* outPixelsPerUnit);

    // ------------------------------------------------------------- parameters

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int csmGetParameterCount(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte** csmGetParameterIds(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int* csmGetParameterTypes(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern float* csmGetParameterMinimumValues(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern float* csmGetParameterMaximumValues(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern float* csmGetParameterDefaultValues(void* model);

    /// <summary>Read/write parameter buffer. This is how the animation layer
    /// feeds values into the deformer.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern float* csmGetParameterValues(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int* csmGetParameterKeyCounts(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern float** csmGetParameterKeyValues(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int* csmGetParameterRepeats(void* model);

    // ------------------------------------------------------------------ parts

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int csmGetPartCount(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte** csmGetPartIds(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern float* csmGetPartOpacities(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int* csmGetPartParentPartIndices(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int* csmGetPartOffscreenIndices(void* model);

    // -------------------------------------------------------------- drawables

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int csmGetDrawableCount(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte** csmGetDrawableIds(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte* csmGetDrawableConstantFlags(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte* csmGetDrawableDynamicFlags(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int* csmGetDrawableBlendModes(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int* csmGetDrawableTextureIndices(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int* csmGetDrawableDrawOrders(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern float* csmGetDrawableOpacities(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int* csmGetDrawableMaskCounts(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int** csmGetDrawableMasks(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int* csmGetDrawableVertexCounts(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern Vector2** csmGetDrawableVertexPositions(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern Vector2** csmGetDrawableVertexUvs(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int* csmGetDrawableIndexCounts(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern ushort** csmGetDrawableIndices(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern Vector4* csmGetDrawableMultiplyColors(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern Vector4* csmGetDrawableScreenColors(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int* csmGetDrawableParentPartIndices(void* model);

    /// <summary>
    /// Render order, indexed by drawable. The value is the *position* of that
    /// drawable in the render sequence, i.e. an inverse permutation:
    /// <c>sequence[renderOrder[i]] = i</c>.
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int* csmGetRenderOrders(void* model);

    // -------------------------------------------------------------- offscreens

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int csmGetOffscreenCount(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int* csmGetOffscreenBlendModes(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern float* csmGetOffscreenOpacities(void* model);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int* csmGetOffscreenOwnerIndices(void* model);

    // ------------------------------------------------------------------ types

    [StructLayout(LayoutKind.Sequential)]
    public struct Vector2
    {
        public float X;
        public float Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vector4
    {
        public float X;
        public float Y;
        public float Z;
        public float W;
    }

    /// <summary>Static per-drawable flags (<c>csmGetDrawableConstantFlags</c>).</summary>
    [Flags]
    public enum ConstantFlags : byte
    {
        None = 0,
        BlendAdditive = 1 << 0,
        BlendMultiplicative = 1 << 1,
        IsDoubleSided = 1 << 2,
        IsInvertedMask = 1 << 3,
    }

    /// <summary>Per-frame per-drawable flags (<c>csmGetDrawableDynamicFlags</c>).</summary>
    [Flags]
    public enum DynamicFlags : byte
    {
        None = 0,
        IsVisible = 1 << 0,
        VisibilityDidChange = 1 << 1,
        OpacityDidChange = 1 << 2,
        DrawOrderDidChange = 1 << 3,
        RenderOrderDidChange = 1 << 4,
        VertexPositionsDidChange = 1 << 5,
        BlendColorDidChange = 1 << 6,
    }
}
