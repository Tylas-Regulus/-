using Live2DPet.Cubism;

namespace Live2DPet.Animation;

/// <summary>
/// Turns the cursor position into head, body and eye rotation.
///
/// The cross term matters: <c>ParamAngleZ</c> is head tilt, and the rig is
/// authored so that tilt responds to looking up-and-left versus up-and-right,
/// which no linear combination of the two axes can express.
/// </summary>
internal sealed class LookAt : IEffect
{
    private readonly record struct LookParameter(int Index, float FactorX, float FactorY, float FactorXY);

    private readonly LookParameter[] _parameters;
    private readonly GazeTarget _target = new();

    public LookAt(CubismModel model)
    {
        var specification = new (string Id, float FactorX, float FactorY, float FactorXY)[]
        {
            (CubismParameterIds.AngleX, 30f, 0f, 0f),
            (CubismParameterIds.AngleY, 0f, 30f, 0f),
            (CubismParameterIds.AngleZ, 0f, 0f, -30f),
            (CubismParameterIds.BodyAngleX, 10f, 0f, 0f),
            (CubismParameterIds.EyeBallX, 1f, 0f, 0f),
            (CubismParameterIds.EyeBallY, 0f, 1f, 0f),
        };

        _parameters = specification
            .Select(s => new LookParameter(model.GetParameterIndex(s.Id), s.FactorX, s.FactorY, s.FactorXY))
            .Where(p => p.Index >= 0)
            .ToArray();
    }

    public string Name => "look";

    /// <summary>Cursor position in normalised window space: -1 to 1 on each axis.</summary>
    public float TargetX { get; set; }

    public float TargetY { get; set; }

    public void Update(CubismModel model, float deltaSeconds)
    {
        _target.Set(TargetX, TargetY);
        _target.Update(deltaSeconds);

        var x = _target.X;
        var y = _target.Y;
        var xy = x * y;

        foreach (var parameter in _parameters)
        {
            model.AddParameterValue(
                parameter.Index,
                parameter.FactorX * x + parameter.FactorY * y + parameter.FactorXY * xy);
        }
    }
}
