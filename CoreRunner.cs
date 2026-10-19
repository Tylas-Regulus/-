using System.Diagnostics;
using System.Numerics;
using System.Runtime;
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

        var lazyTextures = options.LazyTextures || settings.LazyTextures == true;
        var textureSize = CoreOptions.NormalizeTextureSize(
            options.TextureSizeSpecified ? options.TextureSize : settings.TextureSize ?? 0);
        var textures = new ModelTexture[files.Textures.Length];

        for (var i = 0; i < files.Textures.Length; i++)
        {
            var path = files.Textures[i];
            textures[i] = lazyTextures
                ? ModelTexture.Deferred(graphics, path, textureSize)
                : ModelTexture.Load(graphics, path, textureSize);

            if (!lazyTextures && textureSize > 0)
            {
                // A 4096 PNG decodes to a 64 MiB managed array. Collect between
                // pages so the next decode reuses the large-object segment
                // instead of growing a new one for every page.
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
        }

        Log.Info(textureSize > 0
            ? $"textures: page size cap {textureSize}px"
            : "textures: page size as authored");

        if (lazyTextures)
        {
            Log.Info($"textures: lazy loading on - all {textures.Length} page(s) start on disk");
        }

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

        string[] hiddenParts = options.HiddenParts.Count > 0
            ? [.. options.HiddenParts]
            : settings.HiddenPartsFor(modelPath);
        string[] decidedHiddenParts = options.HiddenParts.Count > 0 ? hiddenParts : [];

        if (hiddenParts.Length > 0)
        {
            renderer.SetHiddenParts(hiddenParts);
        }

        string[] droppedUv = options.DropUv.Count > 0
            ? [.. options.DropUv]
            : settings.DropUvFor(modelPath);
        string[] decidedDroppedUv = options.DropUv.Count > 0 ? droppedUv : [];

        if (droppedUv.Length > 0)
        {
            renderer.SetDropUv(droppedUv);
        }

        // The watermark can change what the visible artwork is; fit the framing
        // after the rules have been applied rather than before them.
        renderer.SetFramingToDefaultPose();

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
        var plan = options.PlanSpecified ? options.Plan : settings.Plan ?? AutoPlan.Quality;
        var governor = new QualityGovernor(
            options.Governor && !options.RenderScalePinned,
            options.MinFps,
            options.TargetFps,
            options.Height,
            plan);
        var pacerFps = governor.Enabled ? governor.TargetFps : options.TargetFps;

        if (governor.Enabled)
        {
            if (settings.RenderScale is { } rememberedScale)
            {
                governor.StartAt(rememberedScale, settings.RenderFps);
                Log.Info(
                    $"quality: starting at {governor.RenderScale:F2} / {governor.TargetFps:F0} FPS " +
                    (settings.RenderFps is null
                        ? "(sharpness from last run; no frame rate on record, so starting at the ceiling)"
                        : "(what this machine settled on last run)"));
            }

            renderer.RenderScale = governor.RenderScale;
            Log.Info(
                $"quality governor: on, defending {governor.FloorFps:F0} FPS " +
                $"(budget {governor.BudgetMs:F2}ms), ladder {governor.DescribeLadder()}");
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
        hitTester.HiddenDrawables = renderer.HiddenDrawables;
        hitTester.DropUv = renderer.DropUvMap;
        var interaction = new PetInteraction(window, hitTester);
        var pacer = new FramePacer(pacerFps);

        void SyncFrameCeiling(string why)
        {
            var wanted = governor.Enabled ? governor.TargetFps : options.TargetFps;

            if (Math.Abs(wanted - pacerFps) < 0.001)
            {
                return;
            }

            pacerFps = wanted;
            pacer.SetTarget(wanted);
            Log.Info(wanted > 0
                ? $"frame ceiling: {wanted:F0} FPS ({why})"
                : $"frame ceiling: none ({why})");
        }

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
        var savedFps = settings.RenderFps ?? -1;
        var savedFocusFollow = window.FocusFollowPointer;
        var focusHandedBack = false;
        var wasDragging = false;
        var noticeShown = false;
        var stateCheckCountdown = 30;
        var stateStamp = settingsPath is { Length: > 0 } && File.Exists(settingsPath)
            ? File.GetLastWriteTimeUtc(settingsPath)
            : DateTime.MinValue;

        void SaveState()
        {
            settings.WindowX = savedWindowX;
            settings.WindowY = savedWindowY;
            settings.RenderScale = savedScale > 0 ? savedScale : null;
            settings.RenderFps = savedFps > 0 ? savedFps : null;
            settings.TextureSize = textureSize;
            settings.LazyTextures = lazyTextures;
            settings.Plan = governor.Plan;
            settings.FocusFollowPointer = savedFocusFollow;

            if (decidedHiddenParts.Length > 0)
            {
                settings.RememberHiddenParts(modelPath, decidedHiddenParts);
            }

            if (decidedDroppedUv.Length > 0)
            {
                settings.RememberDropUv(modelPath, decidedDroppedUv);
            }

            settings.Save(settingsPath);
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

            // The editor is gone, but the state file is still the channel a
            // script or a hand edit uses. Re-read it about twice a second so a
            // texture-size change reaches the pet that is on screen now.
            var stateChanged = false;

            if (stateCheckCountdown-- <= 0 && settingsPath is { Length: > 0 } && File.Exists(settingsPath))
            {
                stateCheckCountdown = 30;
                var stamp = File.GetLastWriteTimeUtc(settingsPath);

                if (stamp != stateStamp)
                {
                    stateStamp = stamp;
                    stateChanged = true;
                }
            }

            if (stateChanged)
            {
                var reread = CoreSettings.Load(settingsPath);
                var liveTextureSize = CoreOptions.NormalizeTextureSize(
                    options.TextureSizeSpecified ? options.TextureSize : reread.TextureSize ?? 0);

                if (liveTextureSize != textureSize)
                {
                    var previousTextureSize = textureSize;
                    textureSize = liveTextureSize;
                    var reloadClock = Stopwatch.StartNew();

                    graphics.Context.ClearState();

                    foreach (var texture in textures)
                    {
                        texture.Reload(graphics, textureSize);
                        GCSettings.LargeObjectHeapCompactionMode =
                            GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect(
                            GC.MaxGeneration,
                            GCCollectionMode.Forced,
                            blocking: true,
                            compacting: true);
                    }

                    graphics.Context.Flush();
                    Log.Info(
                        $"textures: page size {previousTextureSize} -> {textureSize}, " +
                        $"reloaded in {reloadClock.ElapsedMilliseconds} ms live");
                    SaveState();
                }

                if (!options.PlanSpecified && reread.Plan is { } livePlan && livePlan != governor.Plan)
                {
                    governor.SetPlan(livePlan);
                    SyncFrameCeiling("自动顺序");
                    Log.Info($"quality: plan {AutoPlans.Label(livePlan)} live");
                    SaveState();
                }
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

            if (lazyTextures)
            {
                var needed = renderer.PagesInUse();

                for (var page = 0; page < needed.Length; page++)
                {
                    if (!needed[page] || textures[page].IsLoaded)
                    {
                        continue;
                    }

                    var loadClock = Stopwatch.StartNew();
                    textures[page].Load(graphics);
                    Log.Info($"texture: page {page} loaded on demand in {loadClock.ElapsedMilliseconds} ms");

                    if (textureSize > 0)
                    {
                        GCSettings.LargeObjectHeapCompactionMode =
                            GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect(
                            GC.MaxGeneration,
                            GCCollectionMode.Forced,
                            blocking: true,
                            compacting: true);
                    }
                }
            }

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
                var fpsNow = governor.Enabled ? governor.TargetFps : options.TargetFps;
                var moved = currentBounds.Left != savedWindowX || currentBounds.Top != savedWindowY;
                var requalified = MathF.Abs(scale - savedScale) > 0.001f;
                var reframed = Math.Abs(fpsNow - savedFps) > 0.001;
                var toggled = window.FocusFollowPointer != savedFocusFollow;

                if (moved || requalified || reframed || toggled)
                {
                    savedWindowX = currentBounds.Left;
                    savedWindowY = currentBounds.Top;
                    savedScale = scale;
                    savedFps = fpsNow;
                    savedFocusFollow = window.FocusFollowPointer;
                    SaveState();
                }
            }

            // The governor decides once a frame; it only speaks when it moves.
            if (governor.Enabled && governor.Observe(frameMs) is { } change)
            {
                renderer.RenderScale = change.RenderScale;
                SyncFrameCeiling($"level {change.Level + 1}/{governor.LevelCount}");
                Log.Info(
                    $"quality: render scale -> {change.RenderScale:F2} " +
                    $"(level {change.Level + 1}/{governor.LevelCount}: {change.Reason})");
            }

            if (!noticeShown && governor.SunkToFloor)
            {
                noticeShown = true;
                Log.Warn(
                    $"quality: the ladder is exhausted at {governor.FloorFps:F0} FPS; " +
                    "nothing automatic is left");
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
