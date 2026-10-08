using Live2DPet.App;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Live2DPet.Rendering;

/// <summary>Owns the D3D11 device, its immediate context and the DXGI factory.</summary>
internal sealed class GraphicsDevice : IDisposable
{
    private GraphicsDevice(
        ID3D11Device device,
        ID3D11DeviceContext context,
        FeatureLevel featureLevel,
        IDXGIFactory2 factory,
        string adapterName,
        bool softwareRasterizer)
    {
        Device = device;
        Context = context;
        FeatureLevel = featureLevel;
        Factory = factory;
        AdapterName = adapterName;
        IsSoftware = softwareRasterizer ||
                     adapterName.Contains("Basic Render", StringComparison.OrdinalIgnoreCase);
    }

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    public FeatureLevel FeatureLevel { get; }

    public IDXGIFactory2 Factory { get; }

    public string AdapterName { get; }

    /// <summary>
    /// Whether the frames are drawn by the CPU rather than by a graphics card -
    /// WARP, or the basic render driver a machine with no adapter falls back to.
    /// The adapter's own name says so; the driver that was *asked* for does not,
    /// because a machine with no card gets WARP whether or not anybody asked.
    ///
    /// It matters because such a device pays for everything in CPU time, and
    /// because waiting for a vertical blank on one of them is not a small delay
    /// but a second pacing mechanism that holds a frame that took 9.9ms to 72 FPS.
    /// </summary>
    public bool IsSoftware { get; }

    /// <summary>
    /// Creates the D3D11 device.
    ///
    /// <paramref name="softwareRasterizer"/> selects WARP, Microsoft's CPU
    /// rasteriser. It is not "a slower GPU", it is a deterministic one, and that
    /// is what makes it useful: a machine that never drops a frame cannot tell
    /// you whether a degradation strategy works, so the failing case has to be
    /// manufactured. WARP manufactures it, costs nothing to enable, and is
    /// reproducible on any machine.
    /// </summary>
    public static GraphicsDevice Create(bool debugLayer, bool softwareRasterizer = false)
    {
        var flags = DeviceCreationFlags.BgraSupport;
        if (debugLayer)
        {
            flags |= DeviceCreationFlags.Debug;
        }

        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0,
        };

        // A null adapter lets DXGI pick the default, which on a machine with a
        // single GPU is simply that GPU.
        var result = D3D11.D3D11CreateDevice(
            IntPtr.Zero,
            softwareRasterizer ? DriverType.Warp : DriverType.Hardware,
            flags,
            featureLevels,
            out var device,
            out var featureLevel,
            out var context);

        result.CheckError();

        var adapterName = "<unknown>";
        using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
        {
            using var adapter = dxgiDevice.GetAdapter();
            adapterName = adapter.Description.Description?.TrimEnd('\0') ?? "<unknown>";
        }

        var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(debugLayer);

        Log.Info(
            $"D3D11 device: {adapterName}, feature level {featureLevel}, " +
            $"driver={(softwareRasterizer ? "WARP" : "Hardware")}, debug={debugLayer}");

        return new GraphicsDevice(device, context, featureLevel, factory, adapterName, softwareRasterizer);
    }

    public void Dispose()
    {
        Factory.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
