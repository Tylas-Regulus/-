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

    public bool? FocusFollowPointer { get; set; }

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

            if (FocusFollowPointer.HasValue)
            {
                lines.Add($"focusFollow={(FocusFollowPointer.Value ? "true" : "false")}");
            }

            File.WriteAllLines(path, lines);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not write {path}: {ex.Message}");
        }
    }
}
