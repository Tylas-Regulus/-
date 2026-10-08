using System.Diagnostics;
using System.Numerics;
using Live2DPet.Animation;
using Live2DPet.Cubism;
using Live2DPet.Model;
using Live2DPet.Motion;
using Live2DPet.Platform;
using Live2DPet.Physics;
using Live2DPet.Rendering;

namespace Live2DPet.App;

/// <summary>
/// The whole of this build: load a model, put it in a transparent top-most window,
/// and hold a frame rate on it.
///
/// What is *not* here is the point of the cut. No editor, no named pipe, no rules
/// file, no sequences, no presets, no timeline, no hotkeys, no layers, no voice, no
/// camera, no screen reading, no resizing wheel. What is left is the part the rest
/// was built on: the renderer, the frame pacer, and the quality governor that gives
/// up render quality rather than frames when the machine cannot manage both.
///
/// It is a copy rather than a mode of the full engine, so the two do not have to
/// agree about anything: this file can change shape without touching that one, and
/// what it needs is visible in one place - the list of files in the project.
/// </summary>
internal static class CoreRunner
{
    public static int Run(string[] args)
    {
        var options = CoreOptions.Parse(args);
        Log.AttachFile(options.LogPath ?? Path.Combine(AppContext.BaseDirectory, "live2dpetcore.log"));

        Log.Info("Live2DPetCore starting");

        // Which build this is: a running program keeps the code it was started
        // with, so "am I running the new one?" needs an answer in the log.
        Log.Info($"build: {BuildStamp()}");

        try
        {
            return RunCore(options);
        }
        catch (Exception ex)
        {
            Log.Error("fatal error", ex);
            return 1;
        }
    }

    private static unsafe int RunCore(CoreOptions options)
    {
        var settingsPath = options.StatePath
                           ?? Path.Combine(AppContext.BaseDirectory, CoreSettings.DefaultFileName);
        var settings = CoreSettings.Load(settingsPath);
        var modelPath = options.ModelPath;

        if (modelPath is null)
        {
            Log.Error("no model: pass --model <path to .model3.json>, "
                      + "or put one under a 'models' folder beside the executable");
            return 2;
        }

        if (!File.Exists(modelPath))
        {
            Log.Error($"model not found: {modelPath}");
            return 2;
        }

        // The loop runs a deadline every frame. On a machine with one logical
        // processor it is otherwise late whenever somebody else's slice is in the
        // way: measured, the same second of frames went from 5 late to 1.
        if (options.ThreadPriority != Win32.ThreadPriorityNormal &&
            Win32.RaiseThreadPriority(options.ThreadPriority))
        {
            Log.Info($"render thread priority: {(options.ThreadPriority >= Win32.ThreadPriorityHighest ? "highest" : "above normal")}");
        }

        if (options.CpuAffinity > 0)
        {
            var mask = (nint)((1L << options.CpuAffinity) - 1);

            if (Win32.SetProcessAffinityMask(Win32.GetCurrentProcess(), mask))
            {
                Log.Info($"cpu affinity restricted to {options.CpuAffinity} logical processor(s)");
            }
        }

        using var window = new PetWindow(
            "Live2DPet",
            options.Width,
            options.Height,
            options.TopMost,
            options.Transparent,
            clickThrough: false,
            tray: options.Tray);

        window.FocusFollowPointer = settings.FocusFollowPointer ?? options.FocusFollowPointer;

        if (options.ClickThrough)
        {
            window.PassThrough = true;
        }

        if (options.WindowPosition is { } requested)
        {
            window.MoveTo(requested.X, requested.Y);
        }
        else if (settings.WindowX is { } savedX && settings.WindowY is { } savedY)
        {
            window.MoveTo(savedX, savedY);
            Log.Info($"window position restored to {savedX},{savedY}");
        }

        using var graphics = GraphicsDevice.Create(options.DebugLayer, options.SoftwareRasterizer);
        using var swapChain = new CompositionSwapChain(
            graphics,
            window.Handle,
            options.Width,
            options.Height,
            options.UseDirectComposition);

        var modelJson = Model3Json.Load(modelPath);
        var files = modelJson.Resolve(modelPath);

        using var model = CubismModel.Load(files.Moc);

        // Regions the artist declared. A model that declares none simply falls back
        // to the part the clicked drawable belongs to.
        var hitZones = new HitZones(model, modelJson.HitAreas);

        Log.Info($"model: {Path.GetFileName(modelPath)}");
        Log.Info(
            $"model: {model.Drawables.Length} drawables, {model.TotalVertexCount} vertices, " +
            $"{model.TotalIndexCount / 3} triangles, {model.PartIds.Length} parts, " +
            $"{model.ParameterCount} parameters");

        var textures = files.Textures.Select(path => ModelTexture.Load(graphics, path)).ToArray();
        using var renderer = new ModelRenderer(graphics, model, textures)
        {
            HighlightMaskedDrawables = options.DebugMasks,
            RenderScale = options.RenderScale,
            Batching = options.Batching,
            SkipModelPass = options.SkipModelPass,
            SkipRenderPass = options.SkipRenderPass,
            SkipClippingPass = options.SkipClippingPass,
            DrawEveryFrameMasks = options.MaskEveryFrame,
        };

        foreach (var drawable in model.Drawables)
        {
            for (var i = 0; i < drawable.MaskCount; i++)
            {
                renderer.MaskSourceDrawables.Add(drawable.Masks[i]);
            }
        }

        // The quality governor. This is the feature the whole design is built
        // around: on a machine that cannot do both, the picture gives way, not the
        // frame rate. `--render-scale` pins the quality and stands it down, which is
        // what a measurement wants.
        var governor = new QualityGovernor(
            options.Governor && !options.RenderScalePinned,
            options.MinFps,
            paced: options.TargetFps > 0);

        if (governor.Enabled)
        {
            if (settings.RenderScale is { } rememberedScale)
            {
                governor.StartAt(rememberedScale);
                Log.Info($"quality: starting at {governor.RenderScale:F2} (what this machine settled on last run)");
            }

            renderer.RenderScale = governor.RenderScale;
            Log.Info(
                $"quality governor: on, defending {options.MinFps:F0} FPS " +
                $"(budget {governor.BudgetMs:F2}ms), ladder {QualityGovernor.DescribeLadder()}");
        }
        else
        {
            Log.Info(options.RenderScalePinned
                ? $"quality governor: off (render scale pinned to {options.RenderScale:F2})"
                : "quality governor: off (--governor off)");
        }

        Log.Info(options.TargetFps > 0
            ? $"frame ceiling: {options.TargetFps:F0} FPS"
            : "frame ceiling: none, so the loop runs as fast as this machine manages");

        var motionGroups = modelJson.ResolveMotionGroups(modelPath)
            .ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Select(Motion.Motion.Load).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        Log.Info(motionGroups.Count == 0
            ? "the model declares no motions"
            : "motion groups: " + string.Join(", ", motionGroups.Select(entry => $"{entry.Key}({entry.Value.Length})")));

        // Prefer the group literally called Idle: a model may declare others first,
        // and "whatever happens to be first" is not a safe default.
        var idleMotion = motionGroups.TryGetValue("Idle", out var idleGroup)
            ? idleGroup.FirstOrDefault()
            : motionGroups.Values.FirstOrDefault()?.FirstOrDefault();

        var motionManager = new MotionManager(model);

        if (!options.NoMotion && idleMotion != null)
        {
            motionManager.StartMotion(idleMotion);
        }

        var genericReaction = SelectZoneReaction(motionGroups, zone: null);
        Log.Info(genericReaction == null
            ? "click reaction: none (the model declares no motion to play)"
            : $"click reaction: group '{genericReaction.Value.Group}' ({genericReaction.Value.Motions.Length} to pick from)");

        var expressionManager = new ExpressionManager();
        var expressions = modelJson.ResolveExpressions(modelPath)
            .Select(reference => Expression.Load(reference.Name, reference.Path))
            .ToArray();

        var effects = new List<IEffect>();
        LookAt? lookAt = null;

        if (!options.NoEffects)
        {
            effects.Add(new EyeBlink(model, modelJson.GetParameterGroup("EyeBlink", CubismParameterIds.DefaultEyeBlink)));
            effects.Add(new Breath(model));
            lookAt = new LookAt(model);
            effects.Add(lookAt);
        }

        PhysicsSimulator? physics = null;

        if (options.NoPhysics)
        {
            Log.Info("physics disabled");
        }
        else if (files.Physics is { Length: > 0 } physicsPath)
        {
            physics = PhysicsSimulator.Load(physicsPath, model);
            Log.Info($"physics: {physics.SubRigCount} sub-rig(s)");
        }
        else
        {
            Log.Warn("the model declares no physics settings");
        }

        var shaper = new WindowShaper(new Silhouette(model), model, window);

        if (!options.ShapeWindow)
        {
            Log.Warn("--no-shape: the window is a rectangle again and will swallow clicks beside the pet");
        }

        // How far from the window's centre the gaze reaches full turn: scaled with
        // the window, because a pet half the screen away should still look at you.
        var lookRadius = MathF.Max(options.Width, options.Height) * 1.2f;
        var cursor = new CursorTracker(window.Handle, lookRadius);

        var hitTester = new HitTester(model, textures);
        var interaction = new PetInteraction(window, hitTester);
        var pacer = new FramePacer(options.TargetFps);
        var stats = new FrameStats();
        var effectsTimer = new PhaseTimer("effects");
        var drawTimer = new PhaseTimer("draw");
        var shapeTimer = new PhaseTimer("shape");
        var pointerTimer = new PhaseTimer("pointer");
        var random = new Random();

        var clock = Stopwatch.StartNew();
        var previousTime = 0.0;
        var nextReport = 1.0;
        var exitAt = options.ExitAfterSeconds > 0 ? options.ExitAfterSeconds : double.MaxValue;
        var frameIndex = 0;
        var savedWindowX = settings.WindowX ?? int.MinValue;
        var savedWindowY = settings.WindowY ?? int.MinValue;
        var savedScale = settings.RenderScale ?? -1f;
        var savedFocusFollow = window.FocusFollowPointer;
        var focusHandedBack = false;
        var wasDragging = false;

        void SaveState()
        {
            var state = new CoreSettings
            {
                WindowX = savedWindowX,
                WindowY = savedWindowY,
                RenderScale = savedScale > 0 ? savedScale : null,
                FocusFollowPointer = savedFocusFollow,
            };

            state.Save(settingsPath);
        }

        while (window.PumpMessages())
        {
            pacer.WaitForNextFrame();

            // The menu speaks through the window, not through the loop, so what
            // it said has to be collected here before the frame is built.
            if (window.TakeCommand() is PetCommand.Quit)
            {
                Log.Info("quit from the menu");
                break;
            }

            window.RefreshClientSize();

            if (window.ResizePending)
            {
                swapChain.Resize(window.Width, window.Height);
                window.ClearResizeFlag();
            }

            var frameMs = stats.Tick();
            var now = clock.Elapsed.TotalSeconds;
            var deltaSeconds = (float)Math.Min(now - previousTime, 0.1);
            previousTime = now;

            if (lookAt != null)
            {
                if (options.LookX.HasValue || options.LookY.HasValue)
                {
                    lookAt.TargetX = options.LookX ?? 0f;
                    lookAt.TargetY = options.LookY ?? 0f;
                }
                else
                {
                    cursor.Sample();
                    lookAt.TargetX = cursor.X;
                    lookAt.TargetY = cursor.Y;
                }
            }

            // Order matters. The baseline is captured before the procedural effects
            // run, so effects are re-derived from a clean state every frame instead
            // of accumulating on top of their own output.
            effectsTimer.Measure(() =>
            {
                model.LoadParameters();
                motionManager.Update(deltaSeconds);

                if (!options.NoMotion && motionManager.IsFinished && idleMotion != null)
                {
                    motionManager.StartMotion(idleMotion);
                }

                model.SaveParameters();

                foreach (var effect in effects)
                {
                    effect.Update(model, deltaSeconds);
                }

                expressionManager.Update(model, deltaSeconds);

                // Physics last: it reads the parameters everything else just wrote.
                physics?.Evaluate(model, deltaSeconds);
            });

            model.Update();
            renderer.UploadGeometry();

            drawTimer.Measure(() => renderer.Draw(swapChain.RenderTargetView, swapChain.Width, swapChain.Height));

            if (options.ShapeWindow)
            {
                shapeTimer.Measure(() => shaper.Update(swapChain.Width, swapChain.Height, renderer.Framing));
            }

            if (!options.SkipPresent)
            {
                // A software device does not wait for a vertical blank: the
                // compositor already shows the newest frame at every vblank, and
                // waiting as well is a second pacer that holds a 9.9ms frame to 72
                // FPS on one processor.
                swapChain.Present(options.WaitForVBlank && !graphics.IsSoftware);
            }

            pointerTimer.Measure(() => interaction.Update(renderer.Framing, swapChain.Width, swapChain.Height));

            // A click: the region under the pointer, and the motion group the
            // convention says that region plays. No rules file - that is the full
            // engine - but the convention is what a model's own motion groups are
            // named for, so a well-made model still reacts.
            if (interaction.TryTakeClick(out var clicked, out var clickX, out var clickY))
            {
                var modelPoint = renderer.Framing.ToModel(clickX, clickY, swapChain.Width, swapChain.Height);
                var zone = hitZones.NameAt(modelPoint, clicked);
                var where = $"click at {clickX},{clickY}: {model.Drawables[clicked].Id}" +
                            (string.IsNullOrEmpty(zone) ? string.Empty : $", region '{zone}'");

                if (options.NoMotion)
                {
                    Log.Info($"{where}: motion playback disabled");
                }
                else if (SelectZoneReaction(motionGroups, zone) is { } reaction)
                {
                    var motion = reaction.Motions[random.Next(reaction.Motions.Length)];
                    motionManager.StartMotion(motion, playOnce: true);
                    Log.Info($"{where} -> {reaction.Group}/{Path.GetFileNameWithoutExtension(motion.SourcePath)}");
                }
                else
                {
                    Log.Info($"{where}: nothing to play");
                }
            }

            // A drag is the window following the pointer, and the position is
            // remembered when it is let go.
            var dragging = interaction.Dragging;

            if (dragging != wasDragging && Win32.GetWindowRect(window.Handle, out var dragBounds))
            {
                Log.Info(dragging
                    ? $"drag started at {dragBounds.Left},{dragBounds.Top}"
                    : $"dropped at {dragBounds.Left},{dragBounds.Top}");

                wasDragging = dragging;
            }

            // Focus follows the pointer, the half Windows permits: the pet may give
            // focus away, never take it by hovering. The "is the pointer on the pet"
            // test runs every frame - that is what re-arms the hand-back.
            if (window.FocusFollowPointer &&
                Win32.GetCursorPos(out var focusCursor) &&
                window.IsForeground)
            {
                var inside = shaper.Contains(focusCursor.X, focusCursor.Y, 12) ||
                             Win32.WindowFromPoint(focusCursor) == window.Handle;

                if (inside)
                {
                    focusHandedBack = false;
                }
                else if (!focusHandedBack)
                {
                    focusHandedBack = true;
                    var under = Win32.WindowFromPoint(focusCursor);

                    if (under != 0 && under != window.Handle && Win32.SetForegroundWindow(under))
                    {
                        Log.Info($"focus handed back to 0x{under:X}");
                    }
                }
            }

            if (options.HitTestGrid > 0 && frameIndex == options.CaptureFrame)
            {
                ReportHitTestGrid(hitTester, hitZones, renderer.Framing, swapChain.Width, swapChain.Height, options.HitTestGrid);
            }

            if (options.CapturePath is { Length: > 0 } capture && frameIndex == options.CaptureFrame)
            {
                swapChain.SaveBackBuffer(capture);
                Log.Info($"captured frame {frameIndex} to {capture}");
            }

            frameIndex++;

            // Remember where it was put and the quality this machine settled on, but
            // not sixty times a second while it is being dragged.
            if (!dragging && Win32.GetWindowRect(window.Handle, out var currentBounds))
            {
                var scale = governor.Enabled ? governor.RenderScale : renderer.RenderScale;
                var moved = currentBounds.Left != savedWindowX || currentBounds.Top != savedWindowY;
                var requalified = MathF.Abs(scale - savedScale) > 0.001f;
                var toggled = window.FocusFollowPointer != savedFocusFollow;

                if (moved || requalified || toggled)
                {
                    savedWindowX = currentBounds.Left;
                    savedWindowY = currentBounds.Top;
                    savedScale = scale;
                    savedFocusFollow = window.FocusFollowPointer;
                    SaveState();
                }
            }

            // The governor decides once a frame; it only speaks when it moves.
            if (governor.Enabled && governor.Observe(frameMs) is { } change)
            {
                renderer.RenderScale = change.RenderScale;
                Log.Info(
                    $"quality: render scale -> {change.RenderScale:F2} " +
                    $"(level {change.Level + 1}/{governor.LevelCount}: {change.Reason})");
            }

            if (now >= nextReport)
            {
                nextReport = now + 1.0;
                var summary = stats.Snapshot();
                var frames = stats.ConsumeIntervalFrameCount();

                Log.Info($"frames={frames} ({frames / 1.0:F1} fps) {summary}");
                Log.Info(
                    $"    {effectsTimer.TakeSummary()} | {pointerTimer.TakeSummary()} | " +
                    $"{drawTimer.TakeSummary()} | {shapeTimer.TakeSummary()} | {renderer.TakeGpuSummary()}");
                Log.Info(
                    $"    draw calls: {renderer.LastDrawCallCount} model + " +
                    $"{renderer.LastMaskDrawCallCount} mask | {renderer.DescribePlan()}");
                Log.Info($"    quality: {governor.Describe()}");
            }

            if (now >= exitAt)
            {
                Log.Info($"exit-after {options.ExitAfterSeconds}s reached");
                Log.Info($"final: {stats.Snapshot()}");
                break;
            }
        }

        SaveState();
        return 0;
    }

    /// <summary>
    /// What the pet does with a poke, with no rules file: a motion group named
    /// after the region (<c>TapHead</c> for a "Head" area), else the usual tap, else
    /// anything that is not the idle loop. The convention Cubism's samples use and
    /// artists author against, kept here so the core reacts without the data layer
    /// the full engine has.
    /// </summary>
    private static (string Group, Motion.Motion[] Motions)? SelectZoneReaction(
        Dictionary<string, Motion.Motion[]> groups,
        string? zone)
    {
        if (!string.IsNullOrEmpty(zone))
        {
            foreach (var candidate in new[] { $"Tap{zone}", zone })
            {
                if (groups.TryGetValue(candidate, out var motions) && motions.Length > 0)
                {
                    return (candidate, motions);
                }
            }
        }

        foreach (var name in (ReadOnlySpan<string>)["TapBody", "Tap", "TapHead", "Flick"])
        {
            if (groups.TryGetValue(name, out var motions) && motions.Length > 0)
            {
                return (name, motions);
            }
        }

        var fallback = groups.FirstOrDefault(entry =>
            !entry.Key.Equals("Idle", StringComparison.OrdinalIgnoreCase) && entry.Value.Length > 0);

        return fallback.Value is { Length: > 0 } ? (fallback.Key, fallback.Value) : null;
    }

    /// <summary>A coverage map of the hit test, one character per cell: the
    /// diagnostic that made the click mapping visible when it was wrong.</summary>
    private static void ReportHitTestGrid(
        HitTester tester,
        HitZones hitZones,
        ModelFraming framing,
        int width,
        int height,
        int cell)
    {
        cell = Math.Clamp(cell, 4, 128);

        var columns = Math.Max(1, width / cell);
        var rows = Math.Max(1, height / cell);
        var hits = 0;
        var symbols = new Dictionary<string, int>(StringComparer.Ordinal);
        var counts = new List<int>();
        var row = new System.Text.StringBuilder(columns);
        var timer = Stopwatch.StartNew();

        for (var r = 0; r < rows; r++)
        {
            row.Clear();

            for (var c = 0; c < columns; c++)
            {
                var x = (c + 0.5f) * cell;
                var y = (r + 0.5f) * cell;

                if (!tester.TryHit(framing, x, y, width, height, out var drawable))
                {
                    row.Append('.');
                    continue;
                }

                hits++;
                var zone = hitZones.NameAt(framing.ToModel(x, y, width, height), drawable) ?? "(unnamed)";

                if (!symbols.TryGetValue(zone, out var symbol))
                {
                    symbol = symbols.Count;
                    symbols[zone] = symbol;
                    counts.Add(0);
                }

                counts[symbol]++;
                row.Append((char)('a' + (symbol % 26)));
            }

            Log.Info($"    {row}");
        }

        foreach (var (zone, symbol) in symbols)
        {
            Log.Info($"    {(char)('a' + (symbol % 26))} = {zone} ({counts[symbol]} cells)");
        }

        Log.Info(
            $"hit test grid: {columns}x{rows} cells of {cell}px, {hits} hits " +
            $"({100.0 * hits / (columns * rows):F1}% of the window) in {timer.ElapsedMilliseconds}ms");
    }

    private static string BuildStamp()
    {
        var path = Environment.ProcessPath;

        return path != null && File.Exists(path)
            ? $"{File.GetLastWriteTime(path):yyyy-MM-dd HH:mm:ss} ({Path.GetFileName(path)})"
            : "unknown";
    }
}
