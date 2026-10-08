using Live2DPet.Cubism;

namespace Live2DPet.Animation;

/// <summary>
/// A procedural contribution to the model's parameters.
///
/// Effects run after motion evaluation and before deformation, on top of the
/// baseline that <see cref="CubismModel.LoadParameters"/> restored. They are
/// therefore re-derived from scratch every frame and must not carry state that
/// depends on their own output.
/// </summary>
internal interface IEffect
{
    string Name { get; }

    void Update(CubismModel model, float deltaSeconds);
}
