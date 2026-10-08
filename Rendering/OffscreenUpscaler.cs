using System.Numerics;
using Live2DPet.App;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Live2DPet.Rendering;

/// <summary>
/// Renders the model into a smaller offscreen target and stretches the result
/// onto the swap chain.
///
/// This is the cheapest quality lever the engine has, and the one a weak GPU
/// feels first: fill rate and memory bandwidth scale with the pixel count, so
/// halving the linear resolution removes three quarters of the fragment work.
/// On a pet the model is a soft illustration with no fine detail to lose, and
/// the upscale is a single bilinear sample, so the visual cost is close to nil.
/// </summary>
internal sealed class OffscreenUpscaler : IDisposable
{
    private readonly GraphicsDevice _graphics;
    private readonly ID3D11VertexShader _blitVertexShader;
    private readonly ID3D11PixelShader _blitPixelShader;
    private readonly ID3D11SamplerState _blitSampler;

    private ID3D11Texture2D? _texture;
    private ID3D11RenderTargetView? _renderTarget;
    private ID3D11ShaderResourceView? _resource;
    private int _width;
    private int _height;

    public OffscreenUpscaler(GraphicsDevice graphics)
    {
        _graphics = graphics;

        _blitVertexShader = graphics.Device.CreateVertexShader(Compile("VsBlit", "vs_5_0").Span);
        _blitPixelShader = graphics.Device.CreatePixelShader(Compile("PsBlit", "ps_5_0").Span);
        _blitSampler = graphics.Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });
    }

    public int Width => _width;

    public int Height => _height;

    /// <summary>Gets a target of the requested scaled size, recreating it on change.</summary>
    public ID3D11RenderTargetView? EnsureTarget(int fullWidth, int fullHeight, float scale)
    {
        var width = Math.Max(16, (int)(fullWidth * scale));
        var height = Math.Max(16, (int)(fullHeight * scale));

        if (_renderTarget != null && width == _width && height == _height)
        {
            return _renderTarget;
        }

        DisposeTarget();

        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        _texture = _graphics.Device.CreateTexture2D(description);
        _renderTarget = _graphics.Device.CreateRenderTargetView(_texture);
        _resource = _graphics.Device.CreateShaderResourceView(_texture);
        _width = width;
        _height = height;

        Log.Info($"offscreen render target: {width}x{height} (scale {scale:F2})");
        return _renderTarget;
    }

    /// <summary>Stretches the offscreen result onto <paramref name="destination"/>.</summary>
    public void Blit(ID3D11RenderTargetView destination, int destinationWidth, int destinationHeight)
    {
        if (_resource == null)
        {
            return;
        }

        var context = _graphics.Context;

        context.OMSetRenderTargets(destination, null);
        context.RSSetViewport(0, 0, destinationWidth, destinationHeight);
        context.RSSetState(null);
        context.OMSetDepthStencilState(null);
        context.OMSetBlendState(null);

        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetInputLayout(null);
        context.VSSetShader(_blitVertexShader);
        context.PSSetShader(_blitPixelShader);
        context.PSSetShaderResource(0, _resource);
        context.PSSetSampler(0, _blitSampler);

        // A single triangle covering the viewport; no vertex buffer needed.
        context.Draw(3, 0);
    }

    private void DisposeTarget()
    {
        _resource?.Dispose();
        _renderTarget?.Dispose();
        _texture?.Dispose();
        _resource = null;
        _renderTarget = null;
        _texture = null;
        _width = 0;
        _height = 0;
    }

    private static ReadOnlyMemory<byte> Compile(string entryPoint, string profile) =>
        ShaderCompiler.Compile(BlitShaderSource, entryPoint, profile);

    private const string BlitShaderSource =
        """
        SamplerState MainSampler : register(s0);
        Texture2D SourceTexture : register(t0);

        struct BlitOutput
        {
            float4 Position : SV_POSITION;
            float2 Uv       : TEXCOORD0;
        };

        BlitOutput VsBlit(uint vertexId : SV_VertexID)
        {
            BlitOutput output;
            float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
            output.Uv = uv;
            output.Position = float4(uv * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), 0.0f, 1.0f);
            return output;
        }

        float4 PsBlit(BlitOutput input) : SV_Target
        {
            return SourceTexture.Sample(MainSampler, input.Uv);
        }
        """;

    public void Dispose()
    {
        DisposeTarget();
        _blitSampler.Dispose();
        _blitPixelShader.Dispose();
        _blitVertexShader.Dispose();
    }
}
