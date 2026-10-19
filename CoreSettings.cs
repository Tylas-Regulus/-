using System.Globalization;

namespace Live2DPet.App;

/// <summary>
/// The two things this build remembers between runs: where the pet was put, and
/// what render quality this machine settled on.
///
/// A flat key=value file beside the executable, unparseable lines ignored rather
/// than fatal, and a failed write is a warning - losing a window position is not
/// worth taking the pet down for. The full engine's state file has more in it
/// (frame ceiling, size, input switches); this one only keeps what it uses, and
/// ignores anything else it finds in the file rather than dropping it.
/// </summary>
internal sealed class CoreSettings
{
    public const string DefaultFileName = "live2dpetcore.state";

    private const int CurrentVersion = 1;

    public int? WindowX { get; set; }

    public int? WindowY { get; set; }

    /// <summary>Render scale the governor settled on last time, so a machine that
    /// had to degrade does not spend the first seconds re-learning it.</summary>
    public float? RenderScale { get; set; }

    /// <summary>The frame rate the governor was on when it settled on
    /// <see cref="RenderScale"/>. Both halves are needed to start where the
    /// machine left off.</summary>
    public double? RenderFps { get; set; }

    /// <summary>The atlas page-size cap this machine chose; zero/null is the
    /// model's own size.</summary>
    public int? TextureSize { get; set; }

    /// <summary>Whether pages are loaded on demand.</summary>
    public bool? LazyTextures { get; set; }

    /// <summary>Which lever automatic mode spends first.</summary>
    public AutoPlan? Plan { get; set; }

    public bool? FocusFollowPointer { get; set; }

    public Dictionary<string, List<string>> HiddenPartsByModel { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<string>> DropUvByModel { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string[] HiddenPartsFor(string modelPath) =>
        HiddenPartsByModel.TryGetValue(modelPath, out var parts) ? [.. parts] : [];

    public string[] DropUvFor(string modelPath) =>
        DropUvByModel.TryGetValue(modelPath, out var specs) ? [.. specs] : [];

    public void RememberHiddenParts(string modelPath, string[] parts)
    {
        if (modelPath.Length > 0 && parts.Length > 0)
        {
            HiddenPartsByModel[modelPath] = [.. parts];
        }
    }

    public void RememberDropUv(string modelPath, string[] specs)
    {
        if (modelPath.Length > 0 && specs.Length > 0)
        {
            DropUvByModel[modelPath] = [.. specs];
        }
    }

    public static CoreSettings Load(string path)
    {
        var settings = new CoreSettings();

        if (!File.Exists(path))
        {
            return settings;
        }

        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var separator = line.IndexOf('=');

                if (separator <= 0)
                {
                    continue;
                }

                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();

                switch (key)
                {
                    case "windowX" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x):
                        settings.WindowX = x;
                        break;
                    case "windowY" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y):
                        settings.WindowY = y;
                        break;
                    case "renderScale" when float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale):
                        settings.RenderScale = scale;
                        break;
                    case "renderFps" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var renderFps):
                        settings.RenderFps = renderFps > 0 ? renderFps : null;
                        break;
                    case "textureSize" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var textureSize):
                        settings.TextureSize = CoreOptions.NormalizeTextureSize(textureSize);
                        break;
                    case "lazyTextures" when bool.TryParse(value, out var lazy):
                        settings.LazyTextures = lazy;
                        break;
                    case "autoPlan" when AutoPlans.TryParse(value, out var plan):
                        settings.Plan = plan;
                        break;
                    case "hideParts":
                    {
                        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                        if (parts.Length >= 2)
                        {
                            settings.HiddenPartsByModel[parts[0]] = [.. parts[1..]];
                        }

                        break;
                    }
                    case "dropUv":
                    {
                        var specs = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                        if (specs.Length >= 2)
                        {
                            settings.DropUvByModel[specs[0]] = [.. specs[1..]];
                        }

                        break;
                    }
                    case "focusFollow" when bool.TryParse(value, out var focus):
                        settings.FocusFollowPointer = focus;
                        break;

                    // A key this build does not know - a state file written by the
                    // full engine, or by a later version of this one - is skipped
                    // rather than guessed at.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read {path} ({ex.Message}); starting from defaults");
            return new CoreSettings();
        }

        return settings;
    }

    public void Save(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var lines = new List<string> { $"version={CurrentVersion}" };

            if (WindowX.HasValue && WindowY.HasValue)
            {
                lines.Add($"windowX={WindowX.Value.ToString(CultureInfo.InvariantCulture)}");
                lines.Add($"windowY={WindowY.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            if (RenderScale.HasValue)
            {
                lines.Add($"renderScale={RenderScale.Value.ToString("F2", CultureInfo.InvariantCulture)}");
            }

            if (RenderFps.HasValue)
            {
                lines.Add($"renderFps={RenderFps.Value.ToString("0.###", CultureInfo.InvariantCulture)}");
            }

            if (TextureSize.HasValue)
            {
                lines.Add($"textureSize={TextureSize.Value}");
            }

            if (LazyTextures.HasValue)
            {
                lines.Add($"lazyTextures={(LazyTextures.Value ? "true" : "false")}");
            }

            if (Plan.HasValue)
            {
                lines.Add($"autoPlan={AutoPlans.Key(Plan.Value)}");
            }

            if (FocusFollowPointer.HasValue)
            {
                lines.Add($"focusFollow={(FocusFollowPointer.Value ? "true" : "false")}");
            }

            foreach (var (model, parts) in HiddenPartsByModel)
            {
                if (parts.Count > 0)
                {
                    lines.Add($"hideParts={model}|{string.Join("|", parts)}");
                }
            }

            foreach (var (model, specs) in DropUvByModel)
            {
                if (specs.Count > 0)
                {
                    lines.Add($"dropUv={model}|{string.Join("|", specs)}");
                }
            }

            File.WriteAllLines(path, lines);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not write {path}: {ex.Message}");
        }
    }
}
