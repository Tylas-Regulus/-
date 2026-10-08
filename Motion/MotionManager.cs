using Live2DPet.Cubism;

namespace Live2DPet.Motion;

/// <summary>A motion that is playing, plus the bookkeeping its fade needs.</summary>
internal sealed class MotionQueueEntry
{
    public required Motion Motion { get; init; }

    public bool Started { get; set; }

    public float StartTime { get; set; }

    public float FadeInStartTime { get; set; }

    /// <summary>Negative means "no end", which is what an un-interrupted loop has.</summary>
    public float EndTime { get; set; } = -1f;

    public bool FadeOutTriggered { get; set; }

    /// <summary>
    /// Play this motion exactly once even though the file marks it as looping.
    ///
    /// The sample model marks every entry in its action group as a loop, which
    /// is right for a viewer that leaves the animation running but wrong for a
    /// pet: a triggered action should play and hand control back to idle.
    /// </summary>
    public bool PlayOnce { get; set; }

    public bool Finished { get; set; }

    /// <summary>
    /// Hold this motion's final pose instead of playing it.
    ///
    /// This is the difference the user asked for by name: the same action file
    /// can be an animation or "just a picture of the character holding that
    /// thing". Frozen at the end, the prop is where the author left it - and
    /// because everything the engine adds on top (gaze, breath, physics) still
    /// runs, it is a picture that can still follow the mouse.
    /// </summary>
    public bool Static { get; set; }

    /// <summary>
    /// Fade times for this playing of the motion, overriding the ones in the
    /// file. Zero means "whatever the file says", so a clip that does not care
    /// behaves exactly as before.
    /// </summary>
    public float FadeIn { get; set; }

    public float FadeOut { get; set; }
}

/// <summary>
/// Plays motions and blends them into the model's parameters.
///
/// The blend model is "ease towards", not "add": each curve interpolates from
/// the parameter's current value towards the curve's evaluated value by the
/// fade weight. That is why two motions can run at once without double-counting,
/// and why the order the entries are applied in matters.
/// </summary>
internal sealed class MotionManager
{
    private readonly CubismModel _model;
    private readonly List<MotionQueueEntry> _entries = [];

    private float _userTime;

    public MotionManager(CubismModel model) => _model = model;

    public Motion? CurrentMotion => _entries.Count > 0 ? _entries[^1].Motion : null;

    public string CurrentMotionName =>
        CurrentMotion == null ? "(none)" : Path.GetFileNameWithoutExtension(CurrentMotion.SourcePath);

    public float CurrentMotionElapsed =>
        _entries.Count == 0 ? 0f : MathF.Max(0f, _userTime - _entries[^1].StartTime);

    public bool IsFinished =>
        _entries.Count == 0 ||
        _entries.TrueForAll(entry => entry.Finished);

    /// <summary>
    /// Whether a motion handed out by <see cref="StartMotion"/> is still
    /// running. The caller needs this rather than <see cref="IsFinished"/>:
    /// the idle loop never finishes, so "is anything playing" is always true
    /// on a pet that is alive, and a sequence that asked to wait for its own
    /// motion would wait forever.
    /// </summary>
    public static bool IsPlaying(MotionQueueEntry entry) => !entry.Finished;

    /// <summary>
    /// Begins a motion, fading out whatever is currently playing. Returns the
    /// queue entry so the caller can ask later whether this particular motion
    /// has ended.
    /// </summary>
    /// <param name="duration">
    /// Play for this long instead of for the motion's own length. Zero means the
    /// motion decides. The entry still fades out through the motion's own
    /// fade-out time, which is what turns "stop early" into a cross-fade rather
    /// than a cut.
    /// </param>
    /// <param name="static">
    /// Hold the motion's final pose instead of playing it. Zero-duration and
    /// static together mean "hold it until something else takes over".
    /// </param>
    /// <param name="seekTo">
    /// Start part-way through, in seconds - what scrubbing the playhead needs:
    /// the model is shown at an arbitrary moment of the motion.
    /// </param>
    public MotionQueueEntry StartMotion(
        Motion motion,
        bool playOnce = false,
        float duration = 0f,
        bool @static = false,
        float seekTo = 0f,
        float fadeIn = 0f,
        float fadeOut = 0f)
    {
        var outgoing = _entries.FindLast(entry => !entry.Finished);
        if (outgoing != null && !outgoing.FadeOutTriggered)
        {
            outgoing.FadeOutTriggered = true;

            // Pull the end time in so the fade-out curve has something to ease
            // against. A looping motion has no end time, so this is what stops it.
            var shortened = _userTime + outgoing.Motion.FadeOutSeconds;
            if (outgoing.EndTime < 0f || shortened < outgoing.EndTime)
            {
                outgoing.EndTime = shortened;
            }
        }

        var started = new MotionQueueEntry
        {
            Motion = motion,
            PlayOnce = playOnce,
            Static = @static,
            FadeIn = fadeIn,
            FadeOut = fadeOut,
        };

        if (duration > 0f)
        {
            // Set before the entry is ever applied: the apply step only fills in
            // an end time when there is not one already.
            started.EndTime = _userTime + duration;
        }

        if (seekTo > 0f)
        {
            // Applied as though it had already been running for this long: every
            // time the apply step derives comes from these two numbers.
            started.Started = true;
            started.StartTime = _userTime - seekTo;
            started.FadeInStartTime = started.StartTime;

            if (duration > 0f)
            {
                started.EndTime = _userTime + MathF.Max(0f, duration - seekTo);
            }
        }

        _entries.Add(started);
        return started;
    }

    /// <summary>Advances every playing motion and writes the result into the model.</summary>
    public void Update(float deltaSeconds)
    {
        _userTime += deltaSeconds;

        for (var i = 0; i < _entries.Count;)
        {
            var entry = _entries[i];
            Apply(entry);

            if (entry.Finished)
            {
                _entries.RemoveAt(i);
            }
            else
            {
                i++;
            }
        }
    }

    private void Apply(MotionQueueEntry entry)
    {
        var motion = entry.Motion;

        if (!entry.Started)
        {
            entry.Started = true;
            entry.StartTime = _userTime;
            entry.FadeInStartTime = _userTime;

            // A looping motion is left without an end time so it never expires,
            // unless this instance was asked to play only once.
            //
            // A held pose is the exception: it has no length of its own, so the
            // only thing that can end it is a duration the caller asked for or
            // the next motion taking over.
            if (!entry.Static && entry.EndTime < 0f && (!motion.Loop || entry.PlayOnce))
            {
                entry.EndTime = _userTime + motion.Duration;
            }
        }

        var fadeInSeconds = entry.FadeIn > 0f ? entry.FadeIn : motion.FadeInSeconds;
        var fadeOutSeconds = entry.FadeOut > 0f ? entry.FadeOut : motion.FadeOutSeconds;

        var fadeIn = fadeInSeconds <= 0f
            ? 1f
            : CubismEasing.EasingSine((_userTime - entry.FadeInStartTime) / fadeInSeconds);

        var fadeOut = fadeOutSeconds <= 0f || entry.EndTime < 0f
            ? 1f
            : CubismEasing.EasingSine((entry.EndTime - _userTime) / fadeOutSeconds);

        var fadeWeight = motion.Weight * fadeIn * fadeOut;

        // A held pose: no fade at either end, and the parameter values are the
        // ones the motion finishes with. Everything else in the frame - gaze,
        // breath, physics - still runs on top, which is what keeps a static prop
        // attached to a character that is still alive.
        if (entry.Static)
        {
            // A held pose blurs in and out over the clip's own fade times rather
            // than appearing: "she is holding the menu" should look like the menu
            // arriving, not like a light being switched on. The values underneath
            // are whatever the rest of the frame has already put there - the idle
            // motion, another clip - which is exactly what a cross-fade blends
            // against.
            var inWeight = fadeInSeconds <= 0f || entry.FadeIn <= 0f
                ? 1f
                : CubismEasing.EasingSine((_userTime - entry.FadeInStartTime) / fadeInSeconds);

            var outWeight = entry.FadeOut <= 0f || entry.EndTime < 0f
                ? 1f
                : CubismEasing.EasingSine((entry.EndTime - _userTime) / fadeOutSeconds);

            var weight = motion.Weight * inWeight * outWeight;

            foreach (var curve in motion.ParameterCurves)
            {
                var index = _model.GetParameterIndex(curve.ParameterId);

                if (index >= 0)
                {
                    var source = _model.GetParameterValue(index);
                    var target = curve.Evaluate(motion.Duration, false, motion.Duration);
                    _model.SetParameterValue(index, source + (target - source) * weight);
                }
            }

            if (entry.EndTime > 0f && entry.EndTime < _userTime)
            {
                entry.Finished = true;
            }

            return;
        }

        // The loop period is slightly longer than the declared duration: the
        // extra frame gives the tail room to blend back into the head.
        var duration = motion.Duration;
        var isCorrection = motion.Loop;

        if (motion.Loop)
        {
            duration += 1f / motion.Fps;
        }

        var time = MathF.Max(0f, _userTime - entry.StartTime);
        if (motion.Loop)
        {
            while (time > duration)
            {
                time -= duration;
            }
        }

        foreach (var curve in motion.ParameterCurves)
        {
            var index = _model.GetParameterIndex(curve.ParameterId);
            if (index < 0)
            {
                continue;
            }

            var source = _model.GetParameterValue(index);
            var value = curve.Evaluate(time, isCorrection, duration);

            float blended;
            if (curve.FadeInTime < 0f && curve.FadeOutTime < 0f)
            {
                blended = source + (value - source) * fadeWeight;
            }
            else
            {
                // A per-curve fade overrides the motion-wide one for that curve.
                var perCurveIn = curve.FadeInTime < 0f
                    ? fadeIn
                    : curve.FadeInTime == 0f
                        ? 1f
                        : CubismEasing.EasingSine((_userTime - entry.FadeInStartTime) / curve.FadeInTime);

                var perCurveOut = curve.FadeOutTime < 0f
                    ? fadeOut
                    : curve.FadeOutTime == 0f || entry.EndTime < 0f
                        ? 1f
                        : CubismEasing.EasingSine((entry.EndTime - _userTime) / curve.FadeOutTime);

                var weight = motion.Weight * perCurveIn * perCurveOut;
                blended = source + (value - source) * weight;
            }

            _model.SetParameterValue(index, blended);
        }

        if (entry.EndTime > 0f && entry.EndTime < _userTime)
        {
            entry.Finished = true;
        }
    }
}
