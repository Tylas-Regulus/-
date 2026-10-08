using Live2DPet.App;
using Vortice.Direct3D11;

namespace Live2DPet.Rendering;

internal readonly record struct GpuSummary(double AverageMs, double MaxMs, int Samples)
{
    public override string ToString() =>
        Samples == 0 ? "gpu n/a" : $"gpu avg {AverageMs:F2}ms max {MaxMs:F2}ms";
}

/// <summary>
/// Reads back how long the GPU actually spent on the model pass.
///
/// Everything else in this engine measures CPU time. On a composition swap chain
/// the present does not block, so GPU cost is completely invisible from frame
/// timings - a frame can be comfortably inside budget while the GPU is already
/// saturated, and the problem only shows up later as a missed sync. Degrading
/// after a miss is reaction; this is what makes it possible to degrade before
/// one.
///
/// Results are read from a slot several frames old rather than the current one,
/// so the read never stalls waiting for the GPU to catch up.
/// </summary>
internal sealed unsafe class GpuTimer : IDisposable
{
    private const int RingSize = 8;

    private readonly GraphicsDevice _graphics;
    private readonly ID3D11Query[] _disjoint = new ID3D11Query[RingSize];
    private readonly ID3D11Query[] _begin = new ID3D11Query[RingSize];
    private readonly ID3D11Query[] _end = new ID3D11Query[RingSize];
    private readonly bool[] _issued = new bool[RingSize];

    private int _slot = -1;
    private double _sum;
    private double _max;
    private int _count;

    public GpuTimer(GraphicsDevice graphics)
    {
        _graphics = graphics;

        try
        {
            var disjointDescription = new QueryDescription(QueryType.TimestampDisjoint);
            var timestampDescription = new QueryDescription(QueryType.Timestamp);

            for (var i = 0; i < RingSize; i++)
            {
                _disjoint[i] = graphics.Device.CreateQuery(disjointDescription);
                _begin[i] = graphics.Device.CreateQuery(timestampDescription);
                _end[i] = graphics.Device.CreateQuery(timestampDescription);
            }

            Enabled = true;
        }
        catch (Exception ex)
        {
            // WARP and some drivers do not support timestamp queries. Losing
            // the measurement must not take the engine down with it.
            Enabled = false;
            Log.Warn($"gpu timestamps unavailable: {ex.Message}");
        }
    }

    public bool Enabled { get; }

    public void BeginFrame()
    {
        if (!Enabled)
        {
            return;
        }

        _slot = (_slot + 1) % RingSize;
        Resolve(_slot);

        var context = _graphics.Context;
        context.Begin(_disjoint[_slot]);
        context.End(_begin[_slot]);
    }

    public void EndFrame()
    {
        if (!Enabled)
        {
            return;
        }

        var context = _graphics.Context;
        context.End(_end[_slot]);
        context.End(_disjoint[_slot]);
        _issued[_slot] = true;
    }

    public GpuSummary TakeSummary()
    {
        var summary = new GpuSummary(_count == 0 ? 0 : _sum / _count, _max, _count);
        _sum = 0;
        _max = 0;
        _count = 0;
        return summary;
    }

    private void Resolve(int slot)
    {
        if (!_issued[slot])
        {
            return;
        }

        _issued[slot] = false;

        var context = _graphics.Context;

        QueryDataTimestampDisjoint disjoint;
        if (context.GetData(
                _disjoint[slot],
                (nint)(&disjoint),
                (uint)sizeof(QueryDataTimestampDisjoint),
                AsyncGetDataFlags.None).Failure)
        {
            return;
        }

        // A disjoint timestamp means the counter was reset or the GPU changed
        // frequency mid-sample, so the measurement is meaningless.
        if (disjoint.Disjoint || disjoint.Frequency == 0)
        {
            return;
        }

        ulong begin;
        ulong end;

        if (context.GetData(_begin[slot], (nint)(&begin), sizeof(ulong), AsyncGetDataFlags.None).Failure ||
            context.GetData(_end[slot], (nint)(&end), sizeof(ulong), AsyncGetDataFlags.None).Failure)
        {
            return;
        }

        var milliseconds = (double)(end - begin) * 1000.0 / disjoint.Frequency;

        if (milliseconds is < 0 or > 1000)
        {
            return;
        }

        _sum += milliseconds;
        _max = Math.Max(_max, milliseconds);
        _count++;
    }

    public void Dispose()
    {
        foreach (var query in _end)
        {
            query?.Dispose();
        }

        foreach (var query in _begin)
        {
            query?.Dispose();
        }

        foreach (var query in _disjoint)
        {
            query?.Dispose();
        }
    }
}
