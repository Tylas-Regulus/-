using System.Text.Json;
using System.Text.Json.Serialization;

namespace Live2DPet.Motion;

/// <summary>Raw shape of a <c>motion3.json</c> file.</summary>
internal sealed class Motion3Json
{
    [JsonPropertyName("Version")]
    public int Version { get; set; }

    [JsonPropertyName("Meta")]
    public Meta? Metadata { get; set; }

    [JsonPropertyName("Curves")]
    public List<Curve> Curves { get; set; } = [];

    public sealed class Meta
    {
        [JsonPropertyName("Duration")]
        public float Duration { get; set; }

        [JsonPropertyName("Fps")]
        public float Fps { get; set; } = 30f;

        [JsonPropertyName("Loop")]
        public bool Loop { get; set; }

        [JsonPropertyName("AreBeziersRestricted")]
        public bool AreBeziersRestricted { get; set; }

        [JsonPropertyName("CurveCount")]
        public int CurveCount { get; set; }

        [JsonPropertyName("FadeInTime")]
        public float? FadeInTime { get; set; }

        [JsonPropertyName("FadeOutTime")]
        public float? FadeOutTime { get; set; }
    }

    public sealed class Curve
    {
        [JsonPropertyName("Target")]
        public string? Target { get; set; }

        [JsonPropertyName("Id")]
        public string? Id { get; set; }

        [JsonPropertyName("FadeInTime")]
        public float? FadeInTime { get; set; }

        [JsonPropertyName("FadeOutTime")]
        public float? FadeOutTime { get; set; }

        /// <summary>
        /// Flat encoding, not an array of segments: a start point followed by a
        /// sequence of <c>[type, ...]</c> runs whose length depends on the type.
        /// </summary>
        [JsonPropertyName("Segments")]
        public List<float> Segments { get; set; } = [];
    }

    public static Motion3Json Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Motion3Json>(json)
               ?? throw new InvalidDataException($"could not parse {path}");
    }
}
