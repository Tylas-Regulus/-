using Live2DPet.Cubism;

namespace Live2DPet.Animation;

/// <summary>
/// Adds a slow sinusoidal drift to head, body and breath parameters.
///
/// Several oscillators at deliberately mismatched cycles, so the motion never
/// settles into an obvious loop. The amplitudes and periods below are the
/// framework sample's, and they are tuned to be noticeable only in aggregate:
/// no single one of them is visible on its own.
/// </summary>
internal sealed class Breath : IEffect
{
    public readonly record struct BreathParameter(int Index, float Offset, float Peak, float Cycle, float Weight);

    private readonly BreathParameter[] _parameters;
    private float _time;

    public Breath(CubismModel model)
    {
        // (parameter, offset, peak, cycle seconds, weight)
        var specification = new (string Id, float Offset, float Peak, float Cycle, float Weight)[]
        {
            (CubismParameterIds.AngleX, 0f, 15f, 6.5345f, 0.5f),
            (CubismParameterIds.AngleY, 0f, 8f, 3.5345f, 0.5f),
            (CubismParameterIds.AngleZ, 0f, 10f, 5.5345f, 0.5f),
            (CubismParameterIds.BodyAngleX, 0f, 4f, 15.5345f, 0.5f),
            (CubismParameterIds.Breath, 0.5f, 0.5f, 3.2345f, 0.5f),
        };

        _parameters = specification
            .Select(s => new BreathParameter(model.GetParameterIndex(s.Id), s.Offset, s.Peak, s.Cycle, s.Weight))
            .Where(p => p.Index >= 0)
            .ToArray();
    }

    public string Name => "breath";

    public void Update(CubismModel model, float deltaSeconds)
    {
        _time += deltaSeconds;
        var t = _time * 2f * MathF.PI;

        foreach (var parameter in _parameters)
        {
            var value = parameter.Offset + parameter.Peak * MathF.Sin(t / parameter.Cycle);
            model.AddParameterValue(parameter.Index, value, parameter.Weight);
        }
    }
}
