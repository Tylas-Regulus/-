using Live2DPet.App;
using Live2DPet.Cubism;

namespace Live2DPet.Motion;

internal enum MotionSegmentType
{
    Linear = 0,
    Bezier = 1,
    Stepped = 2,
    InverseStepped = 3,
}

internal readonly record struct MotionPoint(float Time, float Value);

/// <summary>
/// One segment of an animation curve.
///
/// Linear, stepped and inverse-stepped segments carry two points; Bezier
/// segments carry four (start, two controls, end). The start point is shared
/// with the previous segment, which is why the encoding in the file stores it
/// only once.
/// </summary>
internal sealed class MotionSegment
{
    public MotionSegmentType Type { get; init; }

    public MotionPoint[] Points { get; init; } = [];

    /// <summary>Mirrors <c>AreBeziersRestricted</c>: when true the cheap
    /// evaluator is valid, when false the curve parameter must be solved.</summary>
    public bool RestrictedBezier { get; init; }

    public float EndTime => Points[^1].Time;

    public float Evaluate(float time) => Type switch
    {
        MotionSegmentType.Linear => Linear(Points, time),
        MotionSegmentType.Bezier => RestrictedBezier ? BezierNaive(Points, time) : BezierSolved(Points, time),
        MotionSegmentType.Stepped => Points[0].Value,
        MotionSegmentType.InverseStepped => Points[1].Value,
        _ => Points[0].Value,
    };

    private static MotionPoint Lerp(MotionPoint a, MotionPoint b, float t) =>
        new(a.Time + (b.Time - a.Time) * t, a.Value + (b.Value - a.Value) * t);

    private static float Linear(MotionPoint[] points, float time)
    {
        var t = (time - points[0].Time) / (points[1].Time - points[0].Time);
        if (t < 0f)
        {
            t = 0f;
        }

        return points[0].Value + (points[1].Value - points[0].Value) * t;
    }

    private static float BezierNaive(MotionPoint[] points, float time)
    {
        var t = (time - points[0].Time) / (points[3].Time - points[0].Time);
        if (t < 0f)
        {
            t = 0f;
        }

        return DeCasteljau(points, t);
    }

    private static float BezierSolved(MotionPoint[] points, float time)
    {
        var x1 = points[0].Time;
        var x2 = points[3].Time;
        var cx1 = points[1].Time;
        var cx2 = points[2].Time;

        var a = x2 - 3f * cx2 + 3f * cx1 - x1;
        var b = 3f * cx2 - 6f * cx1 + 3f * x1;
        var c = 3f * cx1 - 3f * x1;
        var d = x1 - time;

        var t = CubismEasing.CardanoAlgorithmForBezier(a, b, c, d);
        return DeCasteljau(points, t);
    }

    private static float DeCasteljau(MotionPoint[] p, float t)
    {
        var p01 = Lerp(p[0], p[1], t);
        var p12 = Lerp(p[1], p[2], t);
        var p23 = Lerp(p[2], p[3], t);
        var p012 = Lerp(p01, p12, t);
        var p123 = Lerp(p12, p23, t);
        return Lerp(p012, p123, t).Value;
    }
}

/// <summary>A parameter curve and its segments.</summary>
internal sealed class MotionCurve
{
    public required string ParameterId { get; init; }

    public float FadeInTime { get; init; } = -1f;

    public float FadeOutTime { get; init; } = -1f;

    public MotionSegment[] Segments { get; init; } = [];

    public float Evaluate(float time, bool isCorrection, float endTime)
    {
        foreach (var segment in Segments)
        {
            if (segment.EndTime > time)
            {
                return segment.Evaluate(time);
            }
        }

        // Past the last key. For a looping motion the tail is blended back
        // towards the first key so the wrap does not produce a visible jump.
        if (isCorrection && time < endTime)
        {
            var last = Segments[^1];
            var from = last.Points[^1];
            var to = Segments[0].Points[0] with { Time = endTime };

            return last.Type switch
            {
                MotionSegmentType.Stepped => from.Value,
                MotionSegmentType.InverseStepped => to.Value,
                _ => from.Value + (to.Value - from.Value) * ((time - from.Time) / (to.Time - from.Time)),
            };
        }

        return Segments[^1].Points[^1].Value;
    }
}

/// <summary>Parsed <c>motion3.json</c>, ready to be applied to a model.</summary>
internal sealed class Motion
{
    private Motion() { }

    public required string SourcePath { get; init; }

    public required float Duration { get; init; }

    public required float Fps { get; init; }

    public required bool Loop { get; init; }

    public required float FadeInSeconds { get; init; }

    public required float FadeOutSeconds { get; init; }

    public required MotionCurve[] ParameterCurves { get; init; }

    /// <summary>Blend weight applied on top of the fade, 0 to 1.</summary>
    public float Weight { get; set; } = 1f;

    /// <summary>
    /// Whether this motion actually moves.
    ///
    /// A motion is a set of parameter curves over time, and a curve whose values
    /// never change is a *pose* wearing a motion's file format - which is how an
    /// author ships a prop that should appear and then sit still. Nothing in the
    /// file says which kind it is, but the curves do, so the engine can tell
    /// without anybody tagging anything: that is what makes the 动态/静态 choice
    /// work on a model nobody has described to us.
    /// </summary>
    public bool IsAnimated { get; private init; }

    private static bool CurvesMove(MotionCurve[] curves)
    {
        foreach (var curve in curves)
        {
            foreach (var segment in curve.Segments)
            {
                if (segment.Points.Length < 2)
                {
                    continue;
                }

                var first = segment.Points[0].Value;

                foreach (var point in segment.Points)
                {
                    // A thousandth of a parameter unit is below anything an eye
                    // can see and above the float noise of an exported curve.
                    if (MathF.Abs(point.Value - first) > 0.001f)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public static Motion Load(string path)
    {
        var json = Motion3Json.Load(path);
        var metadata = json.Metadata ?? throw new InvalidDataException($"missing Meta in {path}");
        var restricted = metadata.AreBeziersRestricted;

        var curves = new List<MotionCurve>(json.Curves.Count);
        var skipped = 0;

        foreach (var curve in json.Curves)
        {
            // Model curves drive eye blink, lip sync and model opacity. None of
            // the sample model's motions use them; parsing but ignoring them
            // keeps a model that does from failing loudly.
            if (!string.Equals(curve.Target, "Parameter", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            var segments = ParseSegments(curve.Segments, restricted, path, curve.Id);
            if (segments.Length == 0)
            {
                continue;
            }

            curves.Add(new MotionCurve
            {
                ParameterId = curve.Id ?? string.Empty,
                FadeInTime = curve.FadeInTime ?? -1f,
                FadeOutTime = curve.FadeOutTime ?? -1f,
                Segments = segments,
            });
        }

        if (skipped > 0)
        {
            Log.Warn($"{Path.GetFileName(path)}: {skipped} non-parameter curves ignored");
        }

        return new Motion
        {
            SourcePath = path,
            Duration = metadata.Duration,
            Fps = metadata.Fps > 0 ? metadata.Fps : 30f,
            Loop = metadata.Loop,
            FadeInSeconds = metadata.FadeInTime is { } input && input >= 0 ? input : 1f,
            FadeOutSeconds = metadata.FadeOutTime is { } output && output >= 0 ? output : 1f,
            ParameterCurves = curves.ToArray(),
            IsAnimated = CurvesMove(curves.ToArray()),
        };
    }

    /// <summary>
    /// Unpacks the flat segment encoding: a start point followed by runs of
    /// <c>[type, ...]</c> whose length depends on the type.
    /// </summary>
    private static MotionSegment[] ParseSegments(
        List<float> values,
        bool restrictedBezier,
        string path,
        string? curveId)
    {
        if (values.Count < 2)
        {
            return [];
        }

        var segments = new List<MotionSegment>();
        var previous = new MotionPoint(values[0], values[1]);
        var position = 2;

        while (position < values.Count)
        {
            var type = (MotionSegmentType)(int)values[position];

            switch (type)
            {
                case MotionSegmentType.Bezier:
                {
                    var control1 = new MotionPoint(values[position + 1], values[position + 2]);
                    var control2 = new MotionPoint(values[position + 3], values[position + 4]);
                    var end = new MotionPoint(values[position + 5], values[position + 6]);

                    segments.Add(new MotionSegment
                    {
                        Type = type,
                        Points = [previous, control1, control2, end],
                        RestrictedBezier = restrictedBezier,
                    });

                    previous = end;
                    position += 7;
                    break;
                }

                case MotionSegmentType.Linear:
                case MotionSegmentType.Stepped:
                case MotionSegmentType.InverseStepped:
                {
                    var end = new MotionPoint(values[position + 1], values[position + 2]);

                    segments.Add(new MotionSegment
                    {
                        Type = type,
                        Points = [previous, end],
                    });

                    previous = end;
                    position += 3;
                    break;
                }

                default:
                    throw new InvalidDataException(
                        $"unknown segment type {type} in {Path.GetFileName(path)} curve '{curveId}'");
            }
        }

        return segments.ToArray();
    }
}
