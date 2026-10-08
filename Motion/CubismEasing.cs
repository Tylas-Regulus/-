namespace Live2DPet.Motion;

/// <summary>Numerical helpers ported from the framework's <c>CubismMath</c>.</summary>
internal static class CubismEasing
{
    public const float Epsilon = 0.00001f;

    /// <summary>
    /// Sine easing, used for both fade in and fade out. Linear fades read as a
    /// snap; this is the difference between a motion that starts and one that
    /// is faded in.
    /// </summary>
    public static float EasingSine(float value)
    {
        if (value < 0f)
        {
            return 0f;
        }

        if (value > 1f)
        {
            return 1f;
        }

        return 0.5f - 0.5f * MathF.Cos(value * MathF.PI);
    }

    public static float Range(float value, float minimum, float maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

    private static float QuadraticEquation(float a, float b, float c)
    {
        if (MathF.Abs(a) < Epsilon)
        {
            return MathF.Abs(b) < Epsilon ? -c : -c / b;
        }

        return -(b + MathF.Sqrt(b * b - 4f * a * c)) / (2f * a);
    }

    /// <summary>
    /// Solves for the curve parameter of a cubic Bezier at a given x.
    ///
    /// The naive evaluator that simply reuses the normalised time as the curve
    /// parameter is only valid when the control points keep the x mapping
    /// monotonic, which is why Cubism writes <c>AreBeziersRestricted</c> into
    /// motion files. When that flag is absent the parameter has to be solved
    /// properly, which is what this does.
    /// </summary>
    public static float CardanoAlgorithmForBezier(float a, float b, float c, float d)
    {
        if (MathF.Abs(a) < Epsilon)
        {
            return Range(QuadraticEquation(b, c, d), 0f, 1f);
        }

        var ba = b / a;
        var ca = c / a;
        var da = d / a;

        var p = (3f * ca - ba * ba) / 3f;
        var p3 = p / 3f;
        var q = (2f * ba * ba * ba - 9f * ba * ca + 27f * da) / 27f;
        var q2 = q / 2f;
        var discriminant = q2 * q2 + p3 * p3 * p3;

        const float centre = 0.5f;
        const float threshold = centre + 0.01f;

        if (discriminant < 0f)
        {
            var mp3 = -p / 3f;
            var mp33 = mp3 * mp3 * mp3;
            var r = MathF.Sqrt(mp33);
            var t = -q / (2f * r);
            var cosPhi = Range(t, -1f, 1f);
            var phi = MathF.Acos(cosPhi);
            var cubeRootR = MathF.Cbrt(r);
            var t1 = 2f * cubeRootR;

            var root1 = t1 * MathF.Cos(phi / 3f) - ba / 3f;
            if (MathF.Abs(root1 - centre) < threshold)
            {
                return Range(root1, 0f, 1f);
            }

            var root2 = t1 * MathF.Cos((phi + 2f * MathF.PI) / 3f) - ba / 3f;
            if (MathF.Abs(root2 - centre) < threshold)
            {
                return Range(root2, 0f, 1f);
            }

            var root3 = t1 * MathF.Cos((phi + 4f * MathF.PI) / 3f) - ba / 3f;
            return Range(root3, 0f, 1f);
        }

        if (discriminant == 0f)
        {
            var u1 = q2 < 0f ? MathF.Cbrt(-q2) : -MathF.Cbrt(q2);

            var root1 = 2f * u1 - ba / 3f;
            if (MathF.Abs(root1 - centre) < threshold)
            {
                return Range(root1, 0f, 1f);
            }

            return Range(-u1 - ba / 3f, 0f, 1f);
        }

        var sd = MathF.Sqrt(discriminant);
        var uu1 = MathF.Cbrt(sd - q2);
        var vv1 = MathF.Cbrt(sd + q2);

        return Range(uu1 - vv1 - ba / 3f, 0f, 1f);
    }
}
