using System.Diagnostics;

namespace Live2DPet.App;

/// <summary>
/// Rolling frame-time statistics.
///
/// Averages lie about stutter, so the interesting numbers are the tail
/// percentiles and how often a vertical sync deadline was missed. The budget
/// for a 60 Hz display is 16.667 ms; anything above it costs a whole refresh
/// interval, which is why we track misses rather than mean frame time.
/// </summary>
internal sealed class FrameStats
{
    private const int Capacity = 600;

    private readonly double[] _samples = new double[Capacity];
    private readonly double[] _scratch = new double[Capacity];
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    private int _count;
    private int _cursor;
    private int _framesSinceReport;
    private long _lastTimestamp;

    public double LastFrameMs { get; private set; }

    public double TotalSeconds => _stopwatch.Elapsed.TotalSeconds;

    public int VsycMisses { get; private set; }

    public int FrameCount { get; private set; }

    /// <summary>Records a frame boundary and returns the elapsed frame time in ms.</summary>
    public double Tick()
    {
        var now = _stopwatch.ElapsedTicks;
        double ms = 0;

        if (_lastTimestamp != 0)
        {
            ms = (now - _lastTimestamp) * 1000.0 / Stopwatch.Frequency;
            LastFrameMs = ms;
            FrameCount++;
            _framesSinceReport++;

            _samples[_cursor] = ms;
            _cursor = (_cursor + 1) % Capacity;
            if (_count < Capacity)
            {
                _count++;
            }

            // One refresh interval at 60 Hz, plus a small tolerance so that
            // normal scheduler jitter does not register as a dropped frame.
            if (ms > 16.667 * 1.5)
            {
                VsycMisses++;
            }
        }

        _lastTimestamp = now;
        return ms;
    }

    public FrameSummary Snapshot()
    {
        if (_count == 0)
        {
            return default;
        }

        Array.Copy(_samples, _scratch, _count);
        var span = _scratch.AsSpan(0, _count);
        span.Sort();

        return new FrameSummary(
            Count: _count,
            Min: span[0],
            P50: Percentile(span, 0.50),
            P95: Percentile(span, 0.95),
            P99: Percentile(span, 0.99),
            Max: span[^1],
            Misses: VsycMisses);
    }

    /// <summary>Returns the number of frames since the previous call, so a report
    /// can quote the real rate for its own interval rather than the ring buffer
    /// size, which saturates.</summary>
    public int ConsumeIntervalFrameCount()
    {
        var count = _framesSinceReport;
        _framesSinceReport = 0;
        return count;
    }

    private static double Percentile(ReadOnlySpan<double> sorted, double q)
    {
        var index = (int)Math.Round((sorted.Length - 1) * q);
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}

internal readonly record struct FrameSummary(
    int Count,
    double Min,
    double P50,
    double P95,
    double P99,
    double Max,
    int Misses)
{
    public override string ToString() =>
        $"p50={P50:F2}ms p95={P95:F2}ms p99={P99:F2}ms max={Max:F2}ms misses={Misses}";
}
