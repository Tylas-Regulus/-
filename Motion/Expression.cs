using System.Text.Json;
using System.Text.Json.Serialization;
using Live2DPet.Cubism;

namespace Live2DPet.Motion;

/// <summary>
/// The three things an expression can be, from the point of view of somebody
/// building a reaction.
///
/// A model's expressions are not all the same kind of thing. Some are a face
/// (happy, blush); some are something worn (glasses, a hairband, a sticker);
/// some are the setting itself (a dark tablecloth, a cup of tea). They are one
/// mechanism in the file format - a set of parameter overrides - but three
/// different intentions,
/// and the intention decides what replaces what: switching the scene must not
/// take the glasses off, and putting on a face must not put the table away.
/// </summary>
internal static class InteractionSlot
{
    public const string Face = "Face";

    public const string Costume = "Costume";

    public const string Scene = "Scene";

    /// <summary>
    /// The motion track. Not an expression layer: a motion clip is not "held",
    /// it plays, so this lane exists only on the timeline and the expression
    /// manager never looks at it.
    /// </summary>
    public const string Motion = "Motion";

    /// <summary>Scene first, face last: specific beats general.</summary>
    public static readonly string[] Order = [Scene, Costume, Face];

    /// <summary>Every lane a timeline can have, top to bottom.</summary>
    public static readonly string[] Lanes = [Motion, Face, Costume, Scene];

    /// <summary>Reads a lane name, accepting the Chinese names as well.</summary>
    public static string NormalizeLane(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "motion" or "动作" => Motion,
        _ => Normalize(name),
    };

    /// <summary>Accepts the English names and the Chinese ones, because the file
    /// is written by hand in either.</summary>
    public static string Normalize(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "costume" or "dress" or "装扮" or "穿戴" => Costume,
        "scene" or "background" or "场景" or "背景" => Scene,
        _ => Face,
    };

    /// <summary>How the slot is written in the log and shown in the editor.</summary>
    public static string Label(string slot) => slot switch
    {
        Motion => "动作",
        Costume => "装扮",
        Scene => "场景",
        _ => "表情",
    };
}

internal enum ExpressionBlend
{
    Add,
    Multiply,
    Overwrite,
}

internal readonly record struct ExpressionParameter(string Id, float Value, ExpressionBlend Blend);

/// <summary>
/// A facial expression: a set of parameter overrides.
///
/// Unlike a motion, an expression does not own a timeline. It is a target state
/// that the model eases towards, and the three blend modes are what let one
/// expression combine with whatever the motion is already doing instead of
/// fighting it.
/// </summary>
internal sealed class Expression
{
    public required string Name { get; init; }

    public required string SourcePath { get; init; }

    public required ExpressionParameter[] Parameters { get; init; }

    public static Expression Load(string name, string path)
    {
        var json = JsonSerializer.Deserialize<Expression3Json>(File.ReadAllText(path))
                   ?? throw new InvalidDataException($"could not parse {path}");

        var parameters = new List<ExpressionParameter>(json.Parameters.Count);

        foreach (var parameter in json.Parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Id))
            {
                continue;
            }

            var blend = parameter.Blend switch
            {
                "Add" => ExpressionBlend.Add,
                "Multiply" => ExpressionBlend.Multiply,
                _ => ExpressionBlend.Overwrite,
            };

            parameters.Add(new ExpressionParameter(parameter.Id, parameter.Value, blend));
        }

        return new Expression { Name = name, SourcePath = path, Parameters = parameters.ToArray() };
    }

    private sealed class Expression3Json
    {
        [JsonPropertyName("Type")]
        public string? Type { get; set; }

        [JsonPropertyName("Parameters")]
        public List<Parameter> Parameters { get; set; } = [];

        public sealed class Parameter
        {
            [JsonPropertyName("Id")]
            public string? Id { get; set; }

            [JsonPropertyName("Value")]
            public float Value { get; set; }

            [JsonPropertyName("Blend")]
            public string? Blend { get; set; }
        }
    }
}

/// <summary>
/// Holds the active expressions and cross-fades between them.
///
/// Two slots *per layer* rather than one: the outgoing expression keeps being
/// applied at a falling weight while the incoming one rises, so switching
/// expressions blends instead of snapping.
///
/// The layers are why a model's "expressions" can behave like three different
/// things. A face, something worn and the scene itself are one mechanism in the
/// file format - a set of parameter overrides - but three different intentions,
/// and the intention decides what replaces what: switching the tablecloth must
/// not take the glasses off, and putting on a face must not put the table away.
/// Each layer holds one expression, independently, and the layers are applied
/// scene-then-costume-then-face so that when two of them touch the same
/// parameter the more specific one wins.
/// </summary>
internal sealed class ExpressionManager
{
    private const float FadeInSeconds = 0.5f;
    private const float FadeOutSeconds = 0.5f;

    private sealed class Layer
    {
        public Expression? Current;
        public Expression? Outgoing;
        public float CurrentWeight;
        public float OutgoingWeight;
    }

    private readonly Dictionary<string, Layer> _layers = new(StringComparer.OrdinalIgnoreCase);

    public string CurrentName => NameIn("Face");

    /// <summary>What is being worn in one layer, for the log and the events.</summary>
    public string NameIn(string slot) =>
        _layers.TryGetValue(slot, out var layer) && layer.Current != null ? layer.Current.Name : "(none)";

    /// <summary>Every layer that is currently showing something.</summary>
    public IEnumerable<(string Slot, string Name)> Active =>
        _layers
            .Where(entry => entry.Value.Current != null)
            .Select(entry => (entry.Key, entry.Value.Current!.Name));

    public void Set(Expression? expression) => Set("Face", expression);

    public void Set(string slot, Expression? expression)
    {
        if (!_layers.TryGetValue(slot, out var layer))
        {
            layer = new Layer();
            _layers[slot] = layer;
        }

        if (ReferenceEquals(layer.Current, expression))
        {
            return;
        }

        // Whatever is fading out is discarded; only one outgoing slot exists.
        layer.Outgoing = layer.Current;
        layer.OutgoingWeight = layer.CurrentWeight;
        layer.Current = expression;
        layer.CurrentWeight = 0f;
    }

    public void Clear(string slot) => Set(slot, null);

    public void Clear() => Clear("Face");

    /// <summary>Releases every layer, for "put everything back".</summary>
    public void ClearAll()
    {
        foreach (var slot in _layers.Keys.ToArray())
        {
            Clear(slot);
        }
    }

    public void Update(CubismModel model, float deltaSeconds)
    {
        // Order matters and is fixed rather than "whatever order they were set":
        // a face over a costume over a scene, so a specific instruction beats a
        // general one when they share a parameter.
        foreach (var slot in InteractionSlot.Order)
        {
            if (_layers.TryGetValue(slot, out var layer))
            {
                Update(model, layer, deltaSeconds);
            }
        }

        // A layer nobody asked for and that has finished fading is dropped, so
        // a long run does not accumulate empties.
        foreach (var (slot, layer) in _layers.ToArray())
        {
            if (layer.Current == null && layer.Outgoing == null)
            {
                _layers.Remove(slot);
            }
        }
    }

    private static void Update(CubismModel model, Layer layer, float deltaSeconds)
    {
        if (layer.Current != null)
        {
            layer.CurrentWeight = MathF.Min(1f, layer.CurrentWeight + deltaSeconds / FadeInSeconds);
            Apply(model, layer.Current, layer.CurrentWeight);
        }

        if (layer.Outgoing != null)
        {
            layer.OutgoingWeight -= deltaSeconds / FadeOutSeconds / 2f;

            if (layer.OutgoingWeight <= 0f)
            {
                layer.Outgoing = null;
                layer.OutgoingWeight = 0f;
            }
            else
            {
                Apply(model, layer.Outgoing, layer.OutgoingWeight);
            }
        }
    }

    private static void Apply(CubismModel model, Expression expression, float weight)
    {
        foreach (var parameter in expression.Parameters)
        {
            var index = model.GetParameterIndex(parameter.Id);
            if (index < 0)
            {
                continue;
            }

            var source = model.GetParameterValue(index);
            var target = parameter.Blend switch
            {
                ExpressionBlend.Add => source + parameter.Value,
                ExpressionBlend.Multiply => source * parameter.Value,
                _ => parameter.Value,
            };

            model.SetParameterValue(index, source + (target - source) * weight);
        }
    }
}
