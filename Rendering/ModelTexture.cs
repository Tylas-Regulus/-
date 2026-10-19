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
    /// <summary>The 1x1 transparent texture a deferred page binds until it is
    /// loaded. Shared by every deferred page: drawing it is drawing nothing,
    /// which is the right answer for a page whose contents are not visible.</summary>
    private static ID3D11Texture2D? _blank;
    private static ID3D11ShaderResourceView? _blankView;

    private ID3D11Texture2D _texture;
    private ID3D11ShaderResourceView _view;
    private byte[]? _alpha;
    private int _alphaWidth;
    private int _alphaHeight;
    private int _maxSize;

    private ModelTexture(
        ID3D11Texture2D texture,
        ID3D11ShaderResourceView view,
        string path,
        int width,
        int height,
        byte[]? alpha,
        int alphaWidth,
        int alphaHeight,
        bool loaded,
        int maxSize)
    {
        _texture = texture;
        _view = view;
        Path = path;
        Width = width;
        Height = height;
        _alpha = alpha;
        _alphaWidth = alphaWidth;
        _alphaHeight = alphaHeight;
        IsLoaded = loaded;
        _maxSize = maxSize;
    }

    public ID3D11Texture2D Texture => _texture;

    public ID3D11ShaderResourceView View => _view;

    public string Path { get; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>
    /// Whether the page's own pixels are on the GPU yet. A deferred page binds
    /// <see cref="_blank"/> and answers 0 to <see cref="AlphaAt"/> until
    /// <see cref="Load"/> is called for it.
    /// </summary>
    public bool IsLoaded { get; private set; }

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
    ///
    /// It is stored at a quarter of the resolution on each axis: a click either
    /// lands on the character or it does not, and four texels of slop is a pixel
    /// or two on screen. See <see cref="Load"/> for what that buys.
    /// </summary>
    public byte AlphaAt(float u, float v)
    {
        if (_alpha == null || !(u >= 0f) || u >= 1f || !(v >= 0f) || v >= 1f)
        {
            return 0;
        }

        var x = Math.Min((int)(u * _alphaWidth), _alphaWidth - 1);
        var y = Math.Min((int)(v * _alphaHeight), _alphaHeight - 1);
        return _alpha[y * _alphaWidth + x];
    }

    /// <summary>
    /// A page whose pixels stay on disk until something on it is visible.
    ///
    /// An atlas is mostly whole pages the current pose never draws - props, a
    /// second outfit - and this is what lets those cost nothing until they are
    /// needed. The page binds a 1x1 transparent texture meanwhile, so every draw
    /// path stays valid and draws nothing. See <see cref="Load(GraphicsDevice)"/>.
    /// </summary>
    public static ModelTexture Deferred(GraphicsDevice graphics, string path, int maxSize)
    {
        EnsureBlank(graphics);
        return new ModelTexture(_blank!, _blankView!, path, 0, 0, null, 0, 0, loaded: false, maxSize);
    }

    private static void EnsureBlank(GraphicsDevice graphics)
    {
        if (_blank != null)
        {
            return;
        }

        var description = new Texture2DDescription
        {
            Width = 1,
            Height = 1,
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
            var pixel = stackalloc byte[4];
            var data = new SubresourceData((nint)pixel, 4);
            _blank = graphics.Device.CreateTexture2D(description, new[] { data });
            _blankView = graphics.Device.CreateShaderResourceView(_blank);
        }
    }

    public static ModelTexture Load(GraphicsDevice graphics, string path, int maxSize)
    {
        var texture = new ModelTexture(null!, null!, path, 0, 0, null, 0, 0, loaded: false, maxSize);
        texture.Load(graphics);
        return texture;
    }

    /// <summary>Decodes, uploads and keeps the coverage plane for this page.
    /// Called once, when the page first has something visible on it.</summary>
    public void Load(GraphicsDevice graphics)
    {
        if (IsLoaded)
        {
            return;
        }

        ImageResult image;
        using (var stream = File.OpenRead(Path))
        {
            image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        }

        Premultiply(image.Data);

        // The page is authored for a zoom range and a display size the pet may
        // never use. A cap here is the difference between 64 MiB and 4 MiB for
        // one 4096 page; UVs stay normalised, so neither the model nor the
        // clipping masks need to know. Only ever shrink - a 2048 page asked for
        // 4096 is already at its authored size.
        var pixels = image.Data;
        var width = image.Width;
        var height = image.Height;

        if (_maxSize > 0 && Math.Max(width, height) > _maxSize)
        {
            (pixels, width, height) = Downsample(pixels, width, height, _maxSize);
        }

        // Hit testing only ever asks whether a point is on the artwork, so the
        // coverage plane is kept at a quarter of the atlas on each axis: a 4x4
        // box average, which keeps the same "more than a few percent opaque"
        // threshold meaningful. The full plane is one byte per texel - 16 MiB for
        // a 4096 page, 272 MiB across this model's seventeen atlases - and this
        // is the difference between a pet that costs a quarter of a gigabyte to
        // click on and one that costs seventeen megabytes.
        const int AlphaShift = 2;
        var alphaWidth = Math.Max(1, width >> AlphaShift);
        var alphaHeight = Math.Max(1, height >> AlphaShift);
        var alpha = new byte[alphaWidth * alphaHeight];
        var block = 1 << AlphaShift;

        for (var y = 0; y < alphaHeight; y++)
        {
            var firstRow = y * block;
            var lastRow = Math.Min(firstRow + block, height);

            for (var x = 0; x < alphaWidth; x++)
            {
                var firstColumn = x * block;
                var lastColumn = Math.Min(firstColumn + block, width);
                var sum = 0;
                var count = 0;

                for (var row = firstRow; row < lastRow; row++)
                {
                    var offset = row * width;

                    for (var column = firstColumn; column < lastColumn; column++)
                    {
                        sum += pixels[((offset + column) * 4) + 3];
                        count++;
                    }
                }

                alpha[(y * alphaWidth) + x] = (byte)((sum + (count / 2)) / count);
            }
        }

        // MipLevels must be explicit here. Passing 0 asks D3D11 for a full mip
        // chain, but an immutable resource has to be handed every subresource up
        // front, and an immutable resource cannot have mips generated into it
        // afterwards either. Mip generation therefore needs a default-usage
        // texture plus UpdateSubresource, which is a separate change.
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
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
            fixed (byte* pixelData = pixels)
            {
                var data = new SubresourceData((nint)pixelData, (uint)(width * 4));
                var texture = graphics.Device.CreateTexture2D(description, new[] { data });
                var view = graphics.Device.CreateShaderResourceView(texture);

                // The shared blank belongs to every deferred page, so it must
                // survive the first page that stops being deferred. Only a real
                // texture is ours to release here.
                if (_texture != null && _view != null && !ReferenceEquals(_texture, _blank))
                {
                    _texture.Dispose();
                    _view.Dispose();
                }

                _texture = texture;
                _view = view;
                _alpha = alpha;
                _alphaWidth = alphaWidth;
                _alphaHeight = alphaHeight;
                IsLoaded = true;
                Width = width;
                Height = height;

                Log.Info(
                    $"texture {System.IO.Path.GetFileName(Path)}: " +
                    (width == image.Width && height == image.Height
                        ? $"{width}x{height} "
                        : $"{image.Width}x{image.Height} -> {width}x{height} ") +
                    $"(premultiplied, {alphaWidth}x{alphaHeight} alpha = {alpha.Length / 1024} KiB " +
                    "kept for hit testing)");
                return;
            }
        }
    }

    /// <summary>
    /// Changes the page-size cap on a texture that is already in use.
    ///
    /// A deferred page only remembers the new cap; it has no pixels to redo.
    /// A loaded page is released and decoded again at the new size, in place,
    /// so the renderer and the hit tester keep the same object and the next
    /// frame is already consistent. This is what makes the 画质 page's dropdown
    /// apply to the pet that is on screen rather than to the next start.
    /// </summary>
    public void Reload(GraphicsDevice graphics, int maxSize)
    {
        if (_maxSize == maxSize)
        {
            return;
        }

        _maxSize = maxSize;

        if (!IsLoaded)
        {
            return;
        }

        if (_texture != null && _view != null && !ReferenceEquals(_texture, _blank))
        {
            _view.Dispose();
            _texture.Dispose();
        }

        _texture = null!;
        _view = null!;
        _alpha = null;
        _alphaWidth = 0;
        _alphaHeight = 0;
        IsLoaded = false;
        Width = 0;
        Height = 0;

        Load(graphics);
    }

    /// <summary>
    /// Area-average filter down to <paramref name="maxSize"/> on the long edge.
    /// The pixels are already premultiplied, so averaging them is the same
    /// operation the GPU's bilinear filter would do - just at a lower rate.
    ///
    /// A direct area sum, not a separable two-pass filter: both read every
    /// source pixel once, but the separable version needs a full-width
    /// intermediate (16-33 MB per page on this model) and that intermediate
    /// survives in the large object heap long enough to show up in the working
    /// set. One output buffer is all this needs.
    /// </summary>
    private static (byte[] Pixels, int Width, int Height) Downsample(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int maxSize)
    {
        var scale = maxSize / (double)Math.Max(sourceWidth, sourceHeight);
        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));

        if (width >= sourceWidth && height >= sourceHeight)
        {
            return (source, sourceWidth, sourceHeight);
        }

        var targetPixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            var y0 = (int)((long)y * sourceHeight / height);
            var y1 = (int)((long)(y + 1) * sourceHeight / height);
            if (y1 <= y0)
            {
                y1 = y0 + 1;
            }

            var targetRow = y * width * 4;

            for (var x = 0; x < width; x++)
            {
                var x0 = (int)((long)x * sourceWidth / width);
                var x1 = (int)((long)(x + 1) * sourceWidth / width);
                if (x1 <= x0)
                {
                    x1 = x0 + 1;
                }

                long r = 0, g = 0, b = 0, a = 0;

                for (var sy = y0; sy < y1; sy++)
                {
                    var sourceRow = sy * sourceWidth;

                    for (var sx = x0; sx < x1; sx++)
                    {
                        var i = (sourceRow + sx) * 4;
                        r += source[i];
                        g += source[i + 1];
                        b += source[i + 2];
                        a += source[i + 3];
                    }
                }

                var count = (long)(x1 - x0) * (y1 - y0);
                var o = targetRow + x * 4;
                targetPixels[o] = (byte)((r + (count / 2)) / count);
                targetPixels[o + 1] = (byte)((g + (count / 2)) / count);
                targetPixels[o + 2] = (byte)((b + (count / 2)) / count);
                targetPixels[o + 3] = (byte)((a + (count / 2)) / count);
            }
        }

        return (targetPixels, width, height);
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
        // A deferred page still points at the shared blank, which belongs to the
        // process rather than to this page.
        if (_texture == null || ReferenceEquals(_texture, _blank))
        {
            return;
        }

        _view.Dispose();
        _texture.Dispose();
    }
}
