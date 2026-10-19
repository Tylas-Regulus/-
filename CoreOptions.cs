namespace Live2DPet.App;

/// <summary>
/// The command line of the core engine.
///
/// Everything here is either something the pet needs to run, or a way to measure
/// it. There is no editor, no pipe, no voice and no rules file, so there are no
/// flags for them: this file is the shape of the cut.
/// </summary>
internal sealed class CoreOptions
{
    public string? ModelPath { get; private set; } = FindDefaultModel();

    public bool ModelSpecified { get; private set; }

    public int Width { get; private set; } = 640;

    public int Height { get; private set; } = 900;

    public bool TopMost { get; private set; } = true;

    public bool Transparent { get; private set; } = true;

    /// <summary>What the pet is drawn at, as a fraction of the window: the lever
    /// the governor pulls. `--render-scale` pins it.</summary>
    public float RenderScale { get; private set; } = 1f;

    public bool RenderScalePinned { get; private set; }

    /// <summary>The ceiling, in FPS. Zero means none at all; the default is the
    /// display's own rate, which is what the eye can actually see.</summary>
    public double TargetFps { get; private set; }

    public bool FrameCapSpecified { get; private set; }

    /// <summary>The floor the quality governor defends. This is the promise: at 60
    /// the pet gives up render quality rather than frames.</summary>
    public double MinFps { get; private set; } = 60;

    public bool MinFpsSpecified { get; private set; }

    public bool Governor { get; private set; } = true;

    /// <summary>Which lever automatic mode spends first: frames or picture.
    /// <c>--auto-plan quality|framerate</c>; the default is the same as the
    /// full engine's.</summary>
    public AutoPlan Plan { get; private set; } = AutoPlan.Quality;

    public bool PlanSpecified { get; private set; }

    /// <summary>Keep atlas pages on disk until something on them is visible.
    /// <c>--lazy-textures</c>.</summary>
    public bool LazyTextures { get; private set; }

    /// <summary>The largest a texture page may be, in pixels on its long edge;
    /// zero keeps the model's own size. <c>--texture-size</c>.</summary>
    public int TextureSize { get; private set; }

    public bool TextureSizeSpecified { get; private set; }

    /// <summary>Parts to leave out, matched by part or drawable name.</summary>
    public List<string> HiddenParts { get; } = [];

    /// <summary>UV patches to leave out, in <c>drawable:u0,v0,u1,v1</c> form.</summary>
    public List<string> DropUv { get; } = [];

    public static int NormalizeTextureSize(int value) =>
        value <= 0 ? 0 : Math.Clamp(value, 256, 8192);

    public bool Batching { get; private set; } = true;

    public bool SoftwareRasterizer { get; private set; }

    public bool DebugLayer { get; private set; }

    public bool UseDirectComposition { get; private set; } = true;

    public bool ShapeWindow { get; private set; } = true;

    public bool Tray { get; private set; } = true;

    public bool ClickThrough { get; private set; }

    public bool FocusFollowPointer { get; private set; } = true;

    public bool SkipModelPass { get; private set; }

    public bool SkipRenderPass { get; private set; }

    public bool SkipClippingPass { get; private set; }

    public bool SkipPresent { get; private set; }

    public bool WaitForVBlank { get; private set; } = true;

    public bool DebugMasks { get; private set; }

    public bool MaskEveryFrame { get; private set; }

    public bool NoMotion { get; private set; }

    public bool NoEffects { get; private set; }

    public bool NoPhysics { get; private set; }

    public int ThreadPriority { get; private set; } = Platform.Win32.ThreadPriorityAboveNormal;

    public int CpuAffinity { get; private set; }

    public int ExitAfterSeconds { get; private set; }

    public int CaptureFrame { get; private set; } = 10;

    public string? CapturePath { get; private set; }

    public string? LogPath { get; private set; }

    public string? StatePath { get; private set; }

    public (int X, int Y)? WindowPosition { get; private set; }

    public float? LookX { get; private set; }

    public float? LookY { get; private set; }

    public int HitTestGrid { get; private set; }

    /// <summary>
    /// Where a model is looked for when `--model` is not given: the first
    /// `*.model3.json` under a `models` folder beside the executable, then under
    /// one in the working directory. Null when there is none, which the runner
    /// reports rather than guessing.
    /// </summary>
    private static string? FindDefaultModel()
    {
        foreach (var root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var folder = Path.Combine(root, "models");

            if (!Directory.Exists(folder))
            {
                continue;
            }

            var found = Directory
                .EnumerateFiles(folder, "*.model3.json", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    public static CoreOptions Parse(string[] args)
    {
        var options = new CoreOptions();
        var i = 0;

        string? Next() => i + 1 < args.Length ? args[++i] : null;

        for (; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model":
                    options.ModelPath = Next() ?? options.ModelPath;
                    options.ModelSpecified = true;
                    break;
                case "--width":
                    options.Width = int.TryParse(Next(), out var width) ? Math.Max(64, width) : options.Width;
                    break;
                case "--height":
                    options.Height = int.TryParse(Next(), out var height) ? Math.Max(64, height) : options.Height;
                    break;
                case "--fps":
                    options.TargetFps = double.TryParse(Next(), out var fps) ? Math.Max(0, fps) : options.TargetFps;
                    options.FrameCapSpecified = true;
                    break;
                case "--min-fps":
                    options.MinFps = double.TryParse(Next(), out var floor) ? Math.Max(0, floor) : options.MinFps;
                    options.MinFpsSpecified = true;
                    break;
                case "--render-scale":
                    options.RenderScale = float.TryParse(Next(), out var scale) ? Math.Clamp(scale, 0.2f, 1f) : options.RenderScale;
                    options.RenderScalePinned = true;
                    break;
                case "--governor":
                    options.Governor = !string.Equals(Next(), "off", StringComparison.OrdinalIgnoreCase);
                    break;
                case "--auto-plan":
                    if (AutoPlans.TryParse(Next() ?? string.Empty, out var plan))
                    {
                        options.Plan = plan;
                        options.PlanSpecified = true;
                    }
                    break;
                case "--lazy-textures":
                    options.LazyTextures = true;
                    break;
                case "--texture-size":
                    options.TextureSize = NormalizeTextureSize(
                        int.TryParse(Next(), out var textureSize) ? textureSize : 0);
                    options.TextureSizeSpecified = true;
                    break;
                case "--hide-part":
                {
                    var part = Next();

                    if (!string.IsNullOrWhiteSpace(part))
                    {
                        options.HiddenParts.Add(part);
                    }

                    break;
                }
                case "--drop-uv":
                {
                    var spec = Next();

                    if (!string.IsNullOrWhiteSpace(spec))
                    {
                        options.DropUv.Add(spec);
                    }

                    break;
                }
                case "--warp":
                    options.SoftwareRasterizer = true;
                    break;
                case "--debug-layer":
                    options.DebugLayer = true;
                    break;
                case "--no-dcomp":
                    options.UseDirectComposition = false;
                    break;
                case "--no-shape":
                    options.ShapeWindow = false;
                    break;
                case "--no-batch":
                    options.Batching = false;
                    break;
                case "--no-model":
                    options.SkipModelPass = true;
                    break;
                case "--no-render":
                    options.SkipRenderPass = true;
                    break;
                case "--no-clip":
                    options.SkipClippingPass = true;
                    break;
                case "--no-present":
                    options.SkipPresent = true;
                    break;
                case "--no-vsync":
                    options.WaitForVBlank = false;
                    break;
                case "--mask-every-frame":
                    options.MaskEveryFrame = true;
                    break;
                case "--debug-masks":
                    options.DebugMasks = true;
                    break;
                case "--tray":
                    options.Tray = !string.Equals(Next(), "off", StringComparison.OrdinalIgnoreCase);
                    break;
                case "--click-through":
                    options.ClickThrough = true;
                    break;
                case "--focus-follow":
                    options.FocusFollowPointer = !string.Equals(Next(), "off", StringComparison.OrdinalIgnoreCase);
                    break;
                case "--no-motion":
                    options.NoMotion = true;
                    break;
                case "--no-fx":
                    options.NoEffects = true;
                    break;
                case "--no-physics":
                    options.NoPhysics = true;
                    break;
                case "--thread-priority":
                    options.ThreadPriority = int.TryParse(Next(), out var priority) ? priority : options.ThreadPriority;
                    break;
                case "--cpu-affinity":
                    options.CpuAffinity = int.TryParse(Next(), out var cores) ? Math.Max(0, cores) : 0;
                    break;
                case "--exit-after":
                    options.ExitAfterSeconds = int.TryParse(Next(), out var seconds) ? Math.Max(0, seconds) : 0;
                    break;
                case "--capture":
                    options.CapturePath = Next();
                    break;
                case "--capture-frame":
                    options.CaptureFrame = int.TryParse(Next(), out var frame) ? Math.Max(0, frame) : options.CaptureFrame;
                    break;
                case "--hit-test-grid":
                    options.HitTestGrid = int.TryParse(Next(), out var cell) ? Math.Max(0, cell) : 0;
                    break;
                case "--log":
                    options.LogPath = Next();
                    break;
                case "--state":
                    options.StatePath = Next();
                    break;
                case "--window-position":
                {
                    var parts = (Next() ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                    if (parts is { Length: 2 } && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y))
                    {
                        options.WindowPosition = (x, y);
                    }

                    break;
                }
                case "--look-x":
                    options.LookX = float.TryParse(Next(), out var lookX) ? lookX : null;
                    break;
                case "--look-y":
                    options.LookY = float.TryParse(Next(), out var lookY) ? lookY : null;
                    break;
                case "--help":
                case "-h":
                    Help();
                    Environment.Exit(0);
                    break;
            }
        }

        // The ceiling: what was typed, else the display's own refresh rate. Read
        // from the monitor rather than assumed to be 60 - a 165 Hz panel told "60"
        // would be capped at a third of what it can show.
        if (!options.FrameCapSpecified)
        {
            var refresh = Platform.Win32.DesktopRefreshRate();
            options.TargetFps = refresh > 0 ? refresh : 60;
        }

        if (!options.MinFpsSpecified && options.TargetFps > 0 && options.TargetFps < options.MinFps)
        {
            options.MinFps = options.TargetFps;
        }

        return options;
    }

    private static void Help() => Console.WriteLine(
        """
        Live2DPetCore - the pet, the frame pacer and the quality governor, and nothing else.

          --model <path>      .model3.json to load (default: first one under models\)
          --width/--height    window size in pixels (default 640x900)
          --fps <n>           frame ceiling (default: the display's refresh rate, 0 = none)
          --min-fps <n>       the floor the governor defends (default 60)
          --render-scale <f>  pin the render quality, 0.2 to 1 (stands the governor down)
          --governor <on|off> trade render quality for frame rate (default on)
          --auto-plan <quality|framerate>  which lever automatic mode spends first
          --lazy-textures     keep atlas pages on disk until the picture needs them
          --texture-size <px> cap each atlas page's long edge (0 = as authored)
          --hide-part <name>  leave a part/drawable out; repeatable
          --drop-uv <spec>    leave a UV patch out; drawable:u0,v0,u1,v1
          --no-motion         do not play motions
          --no-fx             no blink, breath or gaze
          --no-physics        no physics
          --exit-after <sec>  quit by itself, 0 = run until closed
          --log <path>        where to write the run log
          --state <path>      where to keep the window position and quality
          --window-position <x,y>
          --click-through     the pet takes no mouse input at all
          --tray <on|off>     the tray icon (default on)
          --capture <path>    write one frame to a 32-bit BMP

        Measurement: --warp, --cpu-affinity <n>, --thread-priority <0|1|2>,
          --no-shape, --no-batch, --no-model, --no-render, --no-clip, --no-present,
          --no-vsync, --mask-every-frame, --hit-test-grid <n>
        """);
}
