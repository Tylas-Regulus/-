using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Live2DPet.Physics;

/// <summary>Raw shape of a <c>physics3.json</c> file.</summary>
internal sealed class Physics3Json
{
    [JsonPropertyName("Version")]
    public int Version { get; set; }

    [JsonPropertyName("Meta")]
    public Meta? Metadata { get; set; }

    [JsonPropertyName("PhysicsSettings")]
    public List<Setting> Settings { get; set; } = [];

    public sealed class Meta
    {
        [JsonPropertyName("PhysicsSettingCount")]
        public int SettingCount { get; set; }

        [JsonPropertyName("TotalInputCount")]
        public int TotalInputCount { get; set; }

        [JsonPropertyName("TotalOutputCount")]
        public int TotalOutputCount { get; set; }

        [JsonPropertyName("VertexCount")]
        public int VertexCount { get; set; }

        [JsonPropertyName("Fps")]
        public float Fps { get; set; }

        [JsonPropertyName("EffectiveForces")]
        public Forces? EffectiveForces { get; set; }
    }

    public sealed class Forces
    {
        [JsonPropertyName("Gravity")]
        public Vec2 Gravity { get; set; } = new();

        [JsonPropertyName("Wind")]
        public Vec2 Wind { get; set; } = new();
    }

    /// <summary>
    /// A 2D vector with settable properties rather than public fields.
    ///
    /// <see cref="System.Numerics.Vector2"/> stores X and Y as public *fields*,
    /// and System.Text.Json does not populate fields unless explicitly told to.
    /// Deserialising into it silently produces (0, 0), which zeroes gravity -
    /// and a zero gravity vector makes every output angle's reference direction
    /// degenerate.
    /// </summary>
    public sealed class Vec2
    {
        [JsonPropertyName("X")]
        public float X { get; set; }

        [JsonPropertyName("Y")]
        public float Y { get; set; }
    }

    public sealed class Setting
    {
        [JsonPropertyName("Id")]
        public string? Id { get; set; }

        [JsonPropertyName("Input")]
        public List<Input> Inputs { get; set; } = [];

        [JsonPropertyName("Output")]
        public List<Output> Outputs { get; set; } = [];

        [JsonPropertyName("Vertices")]
        public List<Vertex> Vertices { get; set; } = [];

        [JsonPropertyName("Normalization")]
        public Normalization? Normalization { get; set; }
    }

    public sealed class Source
    {
        [JsonPropertyName("Target")]
        public string? Target { get; set; }

        [JsonPropertyName("Id")]
        public string? Id { get; set; }
    }

    public sealed class Input
    {
        [JsonPropertyName("Source")]
        public Source? Source { get; set; }

        [JsonPropertyName("Weight")]
        public float Weight { get; set; }

        /// <summary>One of <c>X</c>, <c>Y</c> or <c>Angle</c>.</summary>
        [JsonPropertyName("Type")]
        public string? Type { get; set; }

        [JsonPropertyName("Reflect")]
        public bool Reflect { get; set; }
    }

    public sealed class Output
    {
        [JsonPropertyName("Destination")]
        public Source? Destination { get; set; }

        [JsonPropertyName("VertexIndex")]
        public int VertexIndex { get; set; }

        /// <summary>
        /// A single number in this format version. It is read as the angle scale
        /// only; the SDK leaves the translation scale at zero, and every output
        /// in the sample model is an angle output, so translation scale is never
        /// consulted. Kept as a vector to mirror the runtime structure.
        /// </summary>
        [JsonPropertyName("Scale")]
        public float Scale { get; set; }

        [JsonPropertyName("Weight")]
        public float Weight { get; set; }

        [JsonPropertyName("Type")]
        public string? Type { get; set; }

        [JsonPropertyName("Reflect")]
        public bool Reflect { get; set; }
    }

    public sealed class Vertex
    {
        [JsonPropertyName("Position")]
        public Vec2 Position { get; set; } = new();

        [JsonPropertyName("Mobility")]
        public float Mobility { get; set; }

        [JsonPropertyName("Delay")]
        public float Delay { get; set; }

        [JsonPropertyName("Acceleration")]
        public float Acceleration { get; set; }

        [JsonPropertyName("Radius")]
        public float Radius { get; set; }
    }

    public sealed class Normalization
    {
        [JsonPropertyName("Position")]
        public NormalizationRange Position { get; set; } = new();

        [JsonPropertyName("Angle")]
        public NormalizationRange Angle { get; set; } = new();
    }

    public sealed class NormalizationRange
    {
        [JsonPropertyName("Minimum")]
        public float Minimum { get; set; }

        [JsonPropertyName("Default")]
        public float Default { get; set; }

        [JsonPropertyName("Maximum")]
        public float Maximum { get; set; }
    }

    public static Physics3Json Load(string path)
    {
        return JsonSerializer.Deserialize<Physics3Json>(File.ReadAllText(path))
               ?? throw new InvalidDataException($"could not parse {path}");
    }
}
