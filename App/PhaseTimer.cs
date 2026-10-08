using System.Diagnostics;

namespace Live2DPet.App;

/// <summary>
/// Accumulates one stage of the frame so the cost of each part is visible
/// separately. Aggregate frame time alone cannot answer "should I optimise the
/// deformer or the draw calls".
/// </summary>
internal sealed class PhaseTimer
{
    private readonly string _name;
    private double _sum;
    private double _max;
    private int _count;

    public PhaseTimer(string name) => _name = name;

    /// <summary>Times the body and returns its value.</summary>
    public T Measure<T>(Func<T> body)
    {
        var start = Stopwatch.GetTimestamp();
        var result = body();
        Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return result;
    }

    public void Measure(Action body)
    {
        var start = Stopwatch.GetTimestamp();
        body();
        Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    public void Add(double milliseconds)
    {
        _sum += milliseconds;
        _max = Math.Max(_max, milliseconds);
        _count++;
    }

    public PhaseSummary TakeSummary()
    {
        var summary = new PhaseSummary(_name, _count == 0 ? 0 : _sum / _count, _max);
        _sum = 0;
        _max = 0;
        _count = 0;
        return summary;
    }
}

internal readonly record struct PhaseSummary(string Name, double AverageMs, double MaxMs)
{
    public override string ToString() => $"{Name} avg {AverageMs:F2}ms max {MaxMs:F2}ms";
}
