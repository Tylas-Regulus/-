using Live2DPet.App;
using Vortice.D3DCompiler;

namespace Live2DPet.Rendering;

/// <summary>
/// Compiles HLSL at runtime with d3dcompiler_47.dll.
///
/// Runtime compilation keeps the edit-build-run loop short while the engine is
/// still taking shape. Once the shader set stabilises it should be compiled
/// ahead of time and shipped as bytecode, which also removes the dependency on
/// a compiler being present at run time.
/// </summary>
internal static class ShaderCompiler
{
    public static ReadOnlyMemory<byte> Compile(string source, string entryPoint, string profile)
    {
        try
        {
            return Compiler.Compile(
                source,
                entryPoint,
                "live2d.hlsl",
                profile,
                ShaderFlags.OptimizationLevel3 | ShaderFlags.EnableStrictness,
                EffectFlags.None);
        }
        catch (Exception ex)
        {
            // The compiler reports the offending line and column in its message,
            // so surface it verbatim rather than replacing it.
            throw new InvalidOperationException(
                $"shader '{entryPoint}' ({profile}) failed to compile: {ex.Message}", ex);
        }
    }
}
