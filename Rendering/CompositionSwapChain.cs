using Live2DPet.App;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace Live2DPet.Rendering;

/// <summary>
/// Swap chain plus the composition tree that presents it.
///
/// When <c>useDirectComposition</c> is set the swap chain is created with
/// <c>CreateSwapChainForComposition</c> and attached to a single visual. The
/// window has no redirection bitmap, so DWM composites our buffer directly and
/// nothing is copied through system memory.
///
/// The fallback path exists purely for debugging: it creates an ordinary
/// window-bound swap chain, which cannot do per-pixel transparency, so a blank
/// or fully black window can be told apart from a broken model.
/// </summary>
internal sealed class CompositionSwapChain : IDisposable
{
    private readonly GraphicsDevice _graphics;
    private readonly nint _windowHandle;
    private readonly bool _useComposition;

    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _renderTargetView;
    private IDCompositionDevice? _compositionDevice;
    private IDCompositionTarget? _compositionTarget;
    private IDCompositionVisual? _compositionVisual;

    public CompositionSwapChain(
        GraphicsDevice graphics,
        nint windowHandle,
        int width,
        int height,
        bool useDirectComposition)
    {
        _graphics = graphics;
        _windowHandle = windowHandle;
        _useComposition = useDirectComposition;

        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        CreateResources();
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public ID3D11RenderTargetView RenderTargetView =>
        _renderTargetView ?? throw new ObjectDisposedException(nameof(CompositionSwapChain));

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (width == Width && height == Height)
        {
            return;
        }

        Width = width;
        Height = height;

        // Everything that references the back buffer must be released before
        // the buffers can be resized.
        _renderTargetView?.Dispose();
        _renderTargetView = null;

        if (_compositionVisual != null && _swapChain != null)
        {
            _compositionVisual.SetContent(null);
            _compositionDevice?.Commit();
        }

        _swapChain!.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None);
        CreateRenderTargetView();
        AttachToComposition();

        Log.Info($"swap chain resized to {width}x{height}");
    }

    public void Present(bool vsync)
    {
        _swapChain!.Present(vsync ? 1u : 0u, PresentFlags.None);
    }

    /// <summary>
    /// Writes the back buffer to a 32-bit BMP.
    ///
    /// Frame timings and even the GPU timer cannot say whether a rendering
    /// change altered the picture; only the pixels can. The back buffer has to
    /// be read before <see cref="Present"/>, because a flip-discard swap chain
    /// gives the buffer back with undefined contents afterwards.
    ///
    /// BMP is chosen because it is the one format that can be written with no
    /// encoder: the swap chain is already B8G8R8A8, which is BMP's byte order,
    /// so the readback is a row copy plus a header.
    /// </summary>
    public void SaveBackBuffer(string path)
    {
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);

        var description = new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };

        using var staging = _graphics.Device.CreateTexture2D(description);
        _graphics.Context.CopyResource(staging, backBuffer);

        var bytesPerRow = Width * 4;
        var pixels = new byte[bytesPerRow * Height];

        var mapped = _graphics.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            // BMP stores rows bottom up.
            for (var y = 0; y < Height; y++)
            {
                var source = mapped.DataPointer + (nint)((long)(Height - 1 - y) * mapped.RowPitch);
                System.Runtime.InteropServices.Marshal.Copy(
                    source,
                    pixels,
                    y * bytesPerRow,
                    bytesPerRow);
            }
        }
        finally
        {
            _graphics.Context.Unmap(staging, 0);
        }

        WriteBmp(path, pixels, Width, Height);
    }

    /// <summary>
    /// Writes 32-bit pixels as a BMP. Shared with the editor's screenshot, which
    /// has the same problem - bytes in BGRX order and no encoder worth taking a
    /// dependency on.
    /// </summary>
    internal static void WriteBmp(string path, byte[] pixels, int width, int height)
    {
        const int headerSize = 14 + 40;
        var file = new byte[headerSize + pixels.Length];

        void WriteInt32(int offset, int value)
        {
            file[offset + 0] = (byte)value;
            file[offset + 1] = (byte)(value >> 8);
            file[offset + 2] = (byte)(value >> 16);
            file[offset + 3] = (byte)(value >> 24);
        }

        file[0] = (byte)'B';
        file[1] = (byte)'M';
        WriteInt32(2, file.Length);
        WriteInt32(10, headerSize);

        WriteInt32(14, 40);
        WriteInt32(18, width);
        WriteInt32(22, height);
        file[26] = 1;  // planes
        file[28] = 32; // bits per pixel
        WriteInt32(34, pixels.Length);
        WriteInt32(38, 2835); // 72 dpi
        WriteInt32(42, 2835);

        pixels.CopyTo(file, headerSize);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, file);
    }

    private void CreateResources()
    {
        var description = new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            // Stretch is the only scaling a composition swap chain accepts - None
            // comes back as an invalid call. It is also the reason the window is
            // never resized while the pet has a picture on screen: with Stretch, the
            // buffer the compositor already has is rescaled to the size the window
            // has *become*, so the frame before a resize is shown at the wrong scale
            // and in the wrong place until the next one arrives - measured on this
            // engine as the pet jumping by its own offset inside the window for one
            // frame. The window is therefore built once, at the largest the pet can
            // get, and a zoom moves the view instead - see EngineRunner.
            //
            // Stretch also means the buffer must keep matching the window: a swap
            // chain larger than its window would be scaled down into it.
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = _useComposition ? AlphaMode.Premultiplied : AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };

        using var dxgiDevice = _graphics.Device.QueryInterface<IDXGIDevice>();

        if (_useComposition)
        {
            _swapChain = _graphics.Factory.CreateSwapChainForComposition(dxgiDevice, description, null);
            Log.Info("swap chain created for composition (premultiplied alpha, flip discard, 2 buffers)");
        }
        else
        {
            _swapChain = _graphics.Factory.CreateSwapChainForHwnd(
                dxgiDevice,
                _windowHandle,
                description,
                null,
                null);
            Log.Warn("swap chain created for HWND: per-pixel transparency is not available on this path");
        }

        CreateRenderTargetView();

        if (_useComposition)
        {
            _compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
            _compositionDevice.CreateTargetForHwnd(_windowHandle, true, out _compositionTarget);
            _compositionDevice.CreateVisual(out _compositionVisual);
            AttachToComposition();
            Log.Info("DirectComposition target attached");
        }
    }

    private void AttachToComposition()
    {
        if (_compositionVisual == null || _compositionTarget == null || _compositionDevice == null)
        {
            return;
        }

        _compositionVisual.SetContent(_swapChain);
        _compositionTarget.SetRoot(_compositionVisual);
        _compositionDevice.Commit();
    }

    private void CreateRenderTargetView()
    {
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _renderTargetView = _graphics.Device.CreateRenderTargetView(backBuffer);
    }

    public void Dispose()
    {
        _compositionVisual?.Dispose();
        _compositionTarget?.Dispose();
        _compositionDevice?.Dispose();
        _renderTargetView?.Dispose();
        _swapChain?.Dispose();
    }
}
