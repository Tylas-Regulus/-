using System.Text.Json.Serialization;
using Live2DPet.Model;
using Live2DPet.Motion;
using Live2DPet.Physics;

namespace Live2DPet.Serialization;

/// <summary>
/// The file formats this cut reads: a Cubism model, its motions, expressions and
/// physics. The full engine also has interaction rules, editor documents and IPC
/// payloads in this context; none of those exist here, so neither does their
/// metadata.
///
/// Source-generated rather than reflection-based because the AOT build has no
/// reflection left to work out a type's shape at run time. Keeping the context
/// trimmed is the same cut as the project file: what is not in the package does
/// not get metadata generated for it.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Model3Json))]
[JsonSerializable(typeof(Motion3Json))]
[JsonSerializable(typeof(Physics3Json))]
// Both Motion3Json and Physics3Json have a nested type called Meta; the
// generator would otherwise name both "Meta" and lose one (SYSLIB1031).
[JsonSerializable(typeof(Motion3Json.Meta), TypeInfoPropertyName = "MotionMeta")]
[JsonSerializable(typeof(Physics3Json.Meta), TypeInfoPropertyName = "PhysicsMeta")]
[JsonSerializable(typeof(Expression.Expression3Json))]
internal partial class PetJson : JsonSerializerContext;
