using Live2DPet.App;
using StbImageSharp;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Live2DPet.Rendering;

/// <summary>
/// One ArtMesh atlas, decoded to premultiplied BGRA/RGBA and uploaded.
///
/// Premultiplying here rather than in the shader is what makes bilinear
/// filtering and mip generation correct: texels that are fully transparent
/// usually still carry whatever colour the artist painted underneath, and
/// filtering straight-alpha RGB pulls that colour into the visible edge.
/// </summary>
internal sealed class ModelTexture : IDisposable
{
    private readonly byte[] _alpha;

    private ModelTexture(
        ID3D11Texture2D texture,
        ID3D11ShaderResourceView view,
        string path,
        int width,
        int height,
        byte[] alpha)
    {
        Texture = texture;
        View = view;
        Path = path;
        Width = width;
        Height = height;
        _alpha = alpha;
    }

    public ID3D11Texture2D Texture { get; }

    public ID3D11ShaderResourceView View { get; }

    public string Path { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// Alpha at a normalised coordinate, with the origin at the texture's top
    /// left as D3D indexes it. Callers sampling from Cubism UVs have to flip V
    /// the same way the shader does.
    ///
    /// The CPU keeps this copy because hit testing needs it and nothing else
    /// does. The renderer samples the texture on the GPU, but "did the user click
    /// on the character or on the transparent space around it" is a question
    /// about the texture's coverage, and answering it on the CPU avoids reading a
    /// staging texture back every frame. The decoder already had the pixels, so
    /// this is one byte per texel rather than a second image.
    /// </summary>
    public byte AlphaAt(float u, float v)
    {
        if (!(u >= 0f) || u >= 1f || !(v >= 0f) || v >= 1f)
        {
            return 0;
        }

        var x = Math.Min((int)(u * Width), Width - 1);
        var y = Math.Min((int)(v * Height), Height - 1);
        return _alpha[y * Width + x];
    }

    public static ModelTexture Load(GraphicsDevice graphics, string path)
    {
        ImageResult image;
        using (var stream = File.OpenRead(path))
        {
            image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        }

        Premultiply(image.Data);

        var alpha = new byte[image.Width * image.Height];
        for (var i = 0; i < alpha.Length; i++)
        {
            alpha[i] = image.Data[i * 4 + 3];
        }

        // MipLevels must be explicit here. Passing 0 asks D3D11 for a full mip
        // chain, but an immutable resource has to be handed every subresource up
        // front, and an immutable resource cannot have mips generated into it
        // afterwards either. Mip generation therefore needs a default-usage
        // texture plus UpdateSubresource, which is a separate change.
        var description = new Texture2DDescription
        {
            Width = (uint)image.Width,
            Height = (uint)image.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        unsafe
        {
            fixed (byte* pixels = image.Data)
            {
                var data = new SubresourceData((nint)pixels, (uint)(image.Width * 4));
                var texture = graphics.Device.CreateTexture2D(description, new[] { data });
                var view = graphics.Device.CreateShaderResourceView(texture);

                Log.Info(
                    $"texture {System.IO.Path.GetFileName(path)}: {image.Width}x{image.Height} " +
                    $"(premultiplied, {alpha.Length / 1024} KiB alpha kept for hit testing)");
                return new ModelTexture(texture, view, path, image.Width, image.Height, alpha);
            }
        }
    }

    private static void Premultiply(byte[] rgba)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var alpha = rgba[i + 3];
            if (alpha == 255)
            {
                continue;
            }

            if (alpha == 0)
            {
                rgba[i] = 0;
                rgba[i + 1] = 0;
                rgba[i + 2] = 0;
                continue;
            }

            // (channel * alpha + 127) / 255, rounded rather than truncated.
            rgba[i] = (byte)((rgba[i] * alpha + 127) / 255);
            rgba[i + 1] = (byte)((rgba[i + 1] * alpha + 127) / 255);
            rgba[i + 2] = (byte)((rgba[i + 2] * alpha + 127) / 255);
        }
    }

    public void Dispose()
    {
        View.Dispose();
        Texture.Dispose();
    }
}
