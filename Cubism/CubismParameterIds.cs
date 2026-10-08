namespace Live2DPet.Cubism;

/// <summary>
/// Parameter identifiers that Cubism Editor exports are expected to carry, so
/// they are safe to reference by name.
///
/// Only the ones the engine drives itself live here. Anything belonging to a
/// specific model's rig belongs in that model's <c>Groups</c> in
/// <c>.model3.json</c>, not in this list.
/// </summary>
internal static class CubismParameterIds
{
    public const string AngleX = "ParamAngleX";
    public const string AngleY = "ParamAngleY";
    public const string AngleZ = "ParamAngleZ";

    public const string BodyAngleX = "ParamBodyAngleX";
    public const string BodyAngleY = "ParamBodyAngleY";
    public const string BodyAngleZ = "ParamBodyAngleZ";

    public const string Breath = "ParamBreath";

    public const string EyeBallX = "ParamEyeBallX";
    public const string EyeBallY = "ParamEyeBallY";

    public const string EyeLOpen = "ParamEyeLOpen";
    public const string EyeROpen = "ParamEyeROpen";

    public const string MouthOpenY = "ParamMouthOpenY";
    public const string MouthForm = "ParamMouthForm";

    /// <summary>Fallback used when a model does not declare an EyeBlink group.</summary>
    public static readonly string[] DefaultEyeBlink = [EyeLOpen, EyeROpen];
}
