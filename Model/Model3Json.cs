using System.Text.Json;
using System.Text.Json.Serialization;

namespace Live2DPet.Model;

/// <summary>
/// The slice of <c>.model3.json</c> this engine needs.
///
/// Milestone 0 only requires the moc and the texture list. Physics, pose,
/// expressions, motions and hit areas are declared in the same file and will be
/// picked up as the corresponding subsystems land.
/// </summary>
internal sealed class Model3Json
{
    [JsonPropertyName("Version")]
    public int Version { get; set; }

    [JsonPropertyName("FileReferences")]
    public FileReferences? Files { get; set; }

    [JsonPropertyName("Groups")]
    public List<Group> Groups { get; set; } = [];

    [JsonPropertyName("HitAreas")]
    public List<HitArea> HitAreas { get; set; } = [];

    /// <summary>A named set of parameters, e.g. the ones that close the eyes.</summary>
    public sealed class Group
    {
        [JsonPropertyName("Target")]
        public string? Target { get; set; }

        [JsonPropertyName("Name")]
        public string? Name { get; set; }

        [JsonPropertyName("Ids")]
        public List<string> Ids { get; set; } = [];
    }

    public sealed class HitArea
    {
        [JsonPropertyName("Name")]
        public string? Name { get; set; }

        [JsonPropertyName("Id")]
        public string? Id { get; set; }
    }

    public sealed class FileReferences
    {
        [JsonPropertyName("Moc")]
        public string? Moc { get; set; }

        [JsonPropertyName("Textures")]
        public List<string> Textures { get; set; } = [];

        [JsonPropertyName("Physics")]
        public string? Physics { get; set; }

        [JsonPropertyName("Pose")]
        public string? Pose { get; set; }

        [JsonPropertyName("Motions")]
        public Dictionary<string, List<MotionReference>> Motions { get; set; } = [];

        [JsonPropertyName("Expressions")]
        public List<ExpressionReference> Expressions { get; set; } = [];
    }

    public sealed class MotionReference
    {
        [JsonPropertyName("File")]
        public string? File { get; set; }

        [JsonPropertyName("FadeInTime")]
        public float? FadeInTime { get; set; }

        [JsonPropertyName("FadeOutTime")]
        public float? FadeOutTime { get; set; }
    }

    public sealed class ExpressionReference
    {
        [JsonPropertyName("Name")]
        public string? Name { get; set; }

        [JsonPropertyName("File")]
        public string? File { get; set; }
    }

    public static Model3Json Load(string path)
    {
        var json = File.ReadAllText(path);
        var model = JsonSerializer.Deserialize<Model3Json>(json)
                    ?? throw new InvalidDataException($"could not parse {path}");

        if (string.IsNullOrWhiteSpace(model.Files?.Moc))
        {
            throw new InvalidDataException($"{path} does not declare FileReferences.Moc");
        }

        return model;
    }

    /// <summary>
    /// Returns the ids of a parameter group, falling back to the supplied names
    /// when the model does not declare one. Models authored outside the editor
    /// routinely omit these groups even though the parameters themselves exist.
    /// </summary>
    public IReadOnlyList<string> GetParameterGroup(string groupName, IReadOnlyList<string> fallback)
    {
        var group = Groups.FirstOrDefault(g =>
            string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(g.Target, "Parameter", StringComparison.OrdinalIgnoreCase));

        return group is { Ids.Count: > 0 } ? group.Ids : fallback;
    }

    /// <summary>Resolves every motion file, grouped by the name the model gives
    /// it (usually an animation category such as Idle or TapBody).</summary>
    public Dictionary<string, string[]> ResolveMotionGroups(string modelJsonPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(modelJsonPath))
                        ?? throw new InvalidDataException("model path has no directory");

        var groups = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, references) in Files?.Motions ?? [])
        {
            var paths = references
                .Where(reference => !string.IsNullOrWhiteSpace(reference.File))
                .Select(reference => Path.GetFullPath(Path.Combine(
                    directory,
                    reference.File!.Replace('/', Path.DirectorySeparatorChar))))
                .ToArray();

            if (paths.Length > 0)
            {
                groups[name] = paths;
            }
        }

        return groups;
    }

    /// <summary>Resolves expression files along with the names the model gives
    /// them, which is what a UI would offer as a list.</summary>
    public (string Name, string Path)[] ResolveExpressions(string modelJsonPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(modelJsonPath))
                        ?? throw new InvalidDataException("model path has no directory");

        return (Files?.Expressions ?? [])
            .Where(reference => !string.IsNullOrWhiteSpace(reference.File))
            .Select(reference => (
                Name: string.IsNullOrWhiteSpace(reference.Name)
                    ? Path.GetFileNameWithoutExtension(reference.File)
                    : reference.Name!,
                Path: Path.GetFullPath(Path.Combine(
                    directory,
                    reference.File!.Replace('/', Path.DirectorySeparatorChar)))))
            .ToArray();
    }

    /// <summary>Resolves every referenced file relative to the model directory.</summary>
    public ModelFiles Resolve(string modelJsonPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(modelJsonPath))
                        ?? throw new InvalidDataException("model path has no directory");

        var references = Files ?? throw new InvalidDataException("missing FileReferences");

        string Resolve(string relative) =>
            Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));

        return new ModelFiles(
            Directory: directory,
            Moc: Resolve(references.Moc!),
            Textures: references.Textures.Select(Resolve).ToArray(),
            Physics: string.IsNullOrWhiteSpace(references.Physics) ? null : Resolve(references.Physics),
            Pose: string.IsNullOrWhiteSpace(references.Pose) ? null : Resolve(references.Pose));
    }
}

internal sealed record ModelFiles(
    string Directory,
    string Moc,
    string[] Textures,
    string? Physics,
    string? Pose);
