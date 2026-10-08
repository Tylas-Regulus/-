namespace Live2DPet.Rendering;

/// <summary>
/// The model shader set.
///
/// Deliberate simplifications compared to the stock Cubism shader set:
///   * one vertex/pixel pair instead of the per-color-blend-mode explosion,
///   * no offscreen render targets.
///
/// The premultiplied-alpha convention is the part worth getting right from day
/// one. Live2D atlases store straight alpha, but the composition swap chain is
/// <c>DXGI_ALPHA_MODE_PREMULTIPLIED</c>, so either the texture gets premultiplied
/// on load or the shader premultiplies on output. We do the former: it is the
/// only variant that survives bilinear filtering and mip generation, because
/// filtering straight-alpha RGB bleeds the (meaningless) colour of transparent
/// texels into the visible edge, which shows up as a dark halo.
///
/// Milestone 5 moved the three per-drawable colours - opacity, multiply and
/// screen - out of the constant buffer and into vertex attributes. That is the
/// whole reason batching is now possible: with no per-drawable constant, a draw
/// call is pure geometry, so drawables sharing a texture, a clipping context and
/// a blend mode can be submitted as one. The constant buffer that remains holds
/// only what genuinely varies per *batch*: the two matrices and the clip channel.
///
/// The tint attributes are declared <c>nointerpolation</c> because they are
/// constant across a drawable's triangles. Interpolating them would cost setup
/// work per pixel to compute a value that is already known.
/// </summary>
internal static class Live2DShader
{
    /// <summary>
    /// <c>row_major</c> is load bearing. The CPU side uses
    /// <see cref="System.Numerics.Matrix4x4"/>, which is stored row major, and
    /// the vertex is a row vector multiplied on the left (<c>mul(v, M)</c>).
    /// HLSL defaults to column-major storage, which would silently transpose
    /// every matrix we upload.
    /// </summary>
    public const string Source =
        """
        cbuffer Live2DConstants : register(b0)
        {
            row_major float4x4 ProjectMatrix;
            row_major float4x4 ClipMatrix;
            float4 ChannelFlag;
        };

        SamplerState MainSampler : register(s0);
        Texture2D MainTexture : register(t0);
        Texture2D MaskTexture : register(t1);

        struct VsInput
        {
            float2 Position : POSITION;   // stream 0, changes every frame
            float  Opacity  : OPACITY;    // stream 0, changes every frame
            float2 Uv       : TEXCOORD0;  // stream 1, static
            float4 Multiply : TEXCOORD1;  // stream 1, static
            float4 Screen   : TEXCOORD2;  // stream 1, static
        };

        struct VsOutput
        {
            float4 Position : SV_POSITION;
            float2 Uv       : TEXCOORD0;
            float2 MaskUv   : TEXCOORD1;
            nointerpolation float4 Multiply : TEXCOORD2;
            nointerpolation float4 Screen   : TEXCOORD3;
            nointerpolation float  Opacity  : TEXCOORD4;
        };

        VsOutput VsMain(VsInput input)
        {
            VsOutput output;
            output.Position = mul(float4(input.Position, 0.0f, 1.0f), ProjectMatrix);
            // Cubism UVs put the origin at the bottom left; D3D textures put it
            // at the top left, so V has to be flipped.
            output.Uv = float2(input.Uv.x, 1.0f - input.Uv.y);
            output.MaskUv = float2(0.0f, 0.0f);
            output.Multiply = input.Multiply;
            output.Screen = input.Screen;
            output.Opacity = input.Opacity;
            return output;
        }

        // Mask pass. ProjectMatrix maps model space onto the mask buffer, and
        // ClipMatrix is unused here.
        VsOutput VsMasked(VsInput input)
        {
            VsOutput output;
            output.Position = mul(float4(input.Position, 0.0f, 1.0f), ProjectMatrix);
            output.Uv = float2(input.Uv.x, 1.0f - input.Uv.y);

            // ClipMatrix carries model space to mask texture UV.
            float4 clip = mul(float4(input.Position, 0.0f, 1.0f), ClipMatrix);
            output.MaskUv = clip.xy;

            output.Multiply = input.Multiply;
            output.Screen = input.Screen;
            output.Opacity = input.Opacity;
            return output;
        }

        // Writes the mask shape's coverage into one channel of the mask buffer.
        // The buffer is cleared to white and this is blended with
        // ZERO / INV_SRC_COLOR, so the shape ends up black and everything else
        // stays white. Sampling therefore yields 0 inside the mask, which is why
        // the main pass computes 1 - sample.
        float4 PsMaskSetup(VsOutput input) : SV_Target
        {
            return ChannelFlag * MainTexture.Sample(MainSampler, input.Uv).a;
        }

        float4 ApplyTint(float4 texColor, VsOutput input)
        {
            // The texture is premultiplied, so rgb already carries alpha.
            texColor.rgb *= input.Multiply.rgb;
            texColor.rgb = (texColor.rgb + input.Screen.rgb * texColor.a)
                         - (texColor.rgb * input.Screen.rgb);

            return texColor * input.Opacity;
        }

        float4 PsMain(VsOutput input) : SV_Target
        {
            return ApplyTint(MainTexture.Sample(MainSampler, input.Uv), input);
        }

        // 1 inside the clipping shape, 0 outside.
        float MaskValue(VsOutput input)
        {
            float4 clipMask = (1.0f - MaskTexture.Sample(MainSampler, input.MaskUv)) * ChannelFlag;
            return clipMask.r + clipMask.g + clipMask.b + clipMask.a;
        }

        float4 PsMainMasked(VsOutput input) : SV_Target
        {
            float4 texColor = ApplyTint(MainTexture.Sample(MainSampler, input.Uv), input);
            return texColor * MaskValue(input);
        }

        float4 PsMainMaskedInverted(VsOutput input) : SV_Target
        {
            float4 texColor = ApplyTint(MainTexture.Sample(MainSampler, input.Uv), input);
            return texColor * (1.0f - MaskValue(input));
        }
        """;
}
