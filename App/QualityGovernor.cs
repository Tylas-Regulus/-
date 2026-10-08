namespace Live2DPet.App;

/// <summary>A change of quality, with the evidence that caused it.</summary>
internal readonly record struct GovernorChange(float RenderScale, int Level, string Reason);

/// <summary>
/// Decides how much render quality to give up in order to hold the frame rate.
///
/// What it does <em>not</em> do is as deliberate as what it does.
///
/// It does not watch the mean, and it does not watch the tail. Milestone 4 found
/// that degrading the render scale makes the average frame time worse - the
/// upscale pass costs more than the pixels it saves - while removing every
/// missed sync, so a governor on the mean would back out of a change that works.
/// The tail is the trap one step further along: a healthy paced loop already has
/// one, because the pacer holds the frame time against the deadline and any
/// scheduler jitter shows up as frames slightly over it. Measured here, 640x900
/// holding a clean 60 FPS reads p99 17.3-17.7ms against a 16.667ms budget, so a
/// tail rule tuned to the budget walks a healthy machine down the whole ladder.
/// It did, the first time this was tried.
///
/// What is left is two signals that separate with a wide margin:
///
///   * the <b>median</b> frame time clearing the budget by several percent, which
///     means the loop is not keeping up at all (measured 16.65ms healthy against
///     20.8ms failing);
///   * the <b>fraction of frames that missed a sync</b>, which catches a machine
///     whose median is fine but which stutters anyway.
///
/// It does not consult the GPU timestamp. That was the original plan - watch
/// headroom, degrade before a miss happens - and milestone 5 killed it: the
/// timestamp turned out to be blind to the levers themselves. Eighty per cent
/// fewer draw calls moved it by nothing, and it reads zero under WARP, which is
/// the only machine here that can actually be made to fail. A signal that cannot
/// see the effect of the action is not a control input.
///
/// Down the ladder is fast, up is slow, and the ladder cannot cascade: every
/// level gets a dwell period and a window of evidence before it is judged, and
/// coming back up needs a long quiet period that doubles each time a step up
/// turns out to have been a mistake.
/// </summary>
internal sealed class QualityGovernor
{
    /// <summary>Render scale steps, best first.</summary>
    private static readonly float[] Ladder = [1.00f, 0.85f, 0.70f, 0.50f, 0.35f, 0.25f];

    /// <summary>Same threshold <see cref="FrameStats"/> uses to count a missed
    /// sync: a frame has to overrun by half a refresh before it certainly lost a
    /// whole slot rather than merely being late.</summary>
    private const double MissFactor = 1.5;

    private const int MissesToDegrade = 2;

    /// <summary>No level is judged before it has had this long, so a burst of
    /// stalls cannot cascade the ladder to the floor in a dozen frames.</summary>
    private const double MinimumDwellSeconds = 1.5;

    /// <summary>Frame times at the current level, used for windowed judgement.
    /// Four seconds at 60 Hz.</summary>
    private const int WindowCapacity = 240;

    /// <summary>Two seconds of evidence before a window may be judged, so a new
    /// level is not condemned for the frames that allocated its render target.</summary>
    private const int MinimumSamplesToJudge = 120;

    private const int FramesBetweenJudgements = 60;

    /// <summary>A paced loop's median sits on the budget by construction, so it
    /// only says "overloaded" once it clears the budget by more than the pacer's
    /// own jitter. Healthy runs measure 16.6ms against 16.667ms.</summary>
    private const double OverloadMedianFactor = 1.05;

    private const double MissRateToDegrade = 0.03;
    private const int MinimumMissesToDegrade = 4;

    private const double RecoveryMedianFactor = 1.01;
    private const double RecoveryMissRate = 0.005;

    /// <summary>How long a level has to be clean before the governor tries the
    /// next one up. Ten seconds was the first answer and it is too short for a
    /// machine whose ceiling is one processor: every probe costs a few seconds of
    /// stutter, and one every ten seconds is a pet that looks broken in exchange
    /// for a slightly sharper picture.</summary>
    private const double FirstQuietPeriodSeconds = 20.0;
    private const double MaxQuietPeriodSeconds = 300.0;

    /// <summary>A degrade this soon after an upgrade means the upgrade was wrong
    /// rather than that the machine got slower.</summary>
    private const double FailedProbeWindowSeconds = 5.0;

    /// <summary>Startup allocates targets and compiles shaders; those frames are
    /// not evidence about steady state.</summary>
    private const double WarmupSeconds = 2.0;

    private readonly bool _enabled;
    private readonly double _budgetMs;

    private readonly double[] _window = new double[WindowCapacity];
    private readonly double[] _scratch = new double[WindowCapacity];

    private int _windowCount;
    private int _windowCursor;
    private int _windowMisses;
    private int _framesSinceJudgement;

    private int _level;
    private int _consecutiveMisses;

    private double _elapsedSeconds;
    private double _quietSeconds;
    private double _secondsAtLevel;
    private double _secondsSinceUpgrade = double.PositiveInfinity;
    private double _quietPeriodSeconds = FirstQuietPeriodSeconds;

    private int _degradations;
    private int _recoveries;
    private double _windowMedian;
    private double _windowTail;
    private string _lastReason = "none";

    /// <param name="floorFramesPerSecond">
    /// The rate to defend. This is a floor, not a ceiling: on a machine that can
    /// do more it has nothing to do, and on one that cannot it gives up quality
    /// until the floor is met.
    /// </param>
    /// <param name="paced">
    /// Whether the loop has a ceiling and is therefore being held against a
    /// deadline. A paced loop has missed syncs to count; an uncapped loop does
    /// not, and its median frame time *is* the machine's own speed rather than
    /// the pacer's decision, which is why the same budget means something
    /// slightly different in each mode.
    /// </param>
    public QualityGovernor(bool enabled, double floorFramesPerSecond, bool paced)
    {
        _enabled = enabled && floorFramesPerSecond > 0;
        _budgetMs = _enabled ? 1000.0 / floorFramesPerSecond : 0;
        _paced = paced;
    }

    private readonly bool _paced;

    public bool Enabled => _enabled;

    public float RenderScale => Ladder[_level];

    public int Level => _level;

    public int LevelCount => Ladder.Length;

    public int Degradations => _degradations;

    public int Recoveries => _recoveries;

    public double BudgetMs => _budgetMs;

    public static string DescribeLadder() => string.Join(" ", Ladder.Select(scale => $"{scale:F2}"));

    /// <summary>
    /// Starts the ladder at the rung nearest a scale remembered from last run.
    ///
    /// Only the starting point is restored, not the state: the governor still
    /// has to earn the next step up with a quiet period, so a machine that was
    /// busy last time but idle now climbs back on its own.
    /// </summary>
    public void StartAt(float renderScale)
    {
        if (!_enabled)
        {
            return;
        }

        var best = 0;
        for (var i = 1; i < Ladder.Length; i++)
        {
            if (MathF.Abs(Ladder[i] - renderScale) < MathF.Abs(Ladder[best] - renderScale))
            {
                best = i;
            }
        }

        _level = best;
    }

    /// <summary>
    /// Takes one frame's duration. Returns a change when the governor moved the
    /// quality level, otherwise null.
    /// </summary>
    public GovernorChange? Observe(double frameMs)
    {
        if (!_enabled)
        {
            return null;
        }

        var seconds = frameMs / 1000.0;
        _elapsedSeconds += seconds;

        if (_elapsedSeconds < WarmupSeconds)
        {
            return null;
        }

        Push(frameMs);
        _quietSeconds += seconds;
        _secondsAtLevel += seconds;
        _secondsSinceUpgrade += seconds;

        if (frameMs > _budgetMs * MissFactor)
        {
            _windowMisses++;
            _consecutiveMisses++;
            _quietSeconds = 0;
        }
        else
        {
            _consecutiveMisses = 0;
        }

        // A hard stall, twice running, is acted on at once - the tail signal the
        // governor is allowed to have, because a frame that lost a whole refresh
        // slot is a fact rather than a statistic.
        if (_consecutiveMisses >= MissesToDegrade && _secondsAtLevel >= MinimumDwellSeconds)
        {
            return Degrade(
                $"{MissesToDegrade} consecutive frames over {_budgetMs * MissFactor:F1}ms " +
                $"(last {frameMs:F1}ms)");
        }

        if (_framesSinceJudgement++ < FramesBetweenJudgements || _windowCount < MinimumSamplesToJudge)
        {
            return null;
        }

        _framesSinceJudgement = 0;

        var (median, tail) = Percentiles();
        _windowMedian = median;
        _windowTail = tail;

        var settled = _secondsAtLevel >= MinimumDwellSeconds;
        var missRate = (double)_windowMisses / _windowCount;

        if (settled && median > _budgetMs * OverloadMedianFactor)
        {
            return Degrade(
                $"median {median:F2}ms against a {_budgetMs:F2}ms budget over {_windowCount} frames");
        }

        // Only a paced loop can miss a sync: an uncapped one has no deadline to
        // miss, so counting "misses" there would be counting frames that were
        // merely slower than the floor.
        if (_paced && settled && _windowMisses >= MinimumMissesToDegrade && missRate > MissRateToDegrade)
        {
            return Degrade(
                $"{_windowMisses} of {_windowCount} frames missed a sync ({missRate:P0})",
                overload: true);
        }

        if (_level > 0 &&
            _quietSeconds >= _quietPeriodSeconds &&
            median < _budgetMs * RecoveryMedianFactor &&
            missRate <= RecoveryMissRate)
        {
            return Recover(median);
        }

        return null;
    }

    private GovernorChange? Degrade(string reason, bool overload = false)
    {
        if (_level >= Ladder.Length - 1)
        {
            return null;
        }

        // A recovery followed by a degrade is information. If it happened right
        // after the upgrade, the upgrade was wrong and the next probe should wait
        // longer. If the upgrade held for a while, this degrade is about
        // something else and the machine has earned the benefit of the doubt.
        //
        // A level that *missed syncs* is a different case, and it is the one that
        // went wrong on a machine with one logical processor: the misses take
        // longer than the failed-probe window to accumulate, so the quiet period
        // was halved - and the governor climbed back to the level it had just
        // failed every ten seconds. Measured: 42 misses in 600 frames, 7% of
        // everything drawn, all of it from the bouncing rather than from the pet.
        // Missing a sync at a paced rate is evidence about the level itself, so
        // that probe waits twice as long next time, however long it took to show.
        if (_secondsSinceUpgrade < double.PositiveInfinity)
        {
            _quietPeriodSeconds = overload || _secondsSinceUpgrade < FailedProbeWindowSeconds
                ? Math.Min(_quietPeriodSeconds * 2, MaxQuietPeriodSeconds)
                : Math.Max(_quietPeriodSeconds * 0.5, FirstQuietPeriodSeconds);
        }

        _level++;
        _degradations++;
        _consecutiveMisses = 0;
        _secondsSinceUpgrade = double.PositiveInfinity;
        _lastReason = reason;
        ResetLevelState();

        return new GovernorChange(RenderScale, _level, reason);
    }

    private GovernorChange? Recover(double median)
    {
        var previous = _level;

        _level--;
        _recoveries++;
        _secondsSinceUpgrade = 0;
        ResetLevelState();

        var reason = $"quiet {_quietPeriodSeconds:F0}s at level {previous + 1} with median {median:F2}ms";
        _lastReason = reason;
        return new GovernorChange(RenderScale, _level, reason);
    }

    private void Push(double frameMs)
    {
        _window[_windowCursor] = frameMs;
        _windowCursor = (_windowCursor + 1) % WindowCapacity;

        if (_windowCount < WindowCapacity)
        {
            _windowCount++;
        }
    }

    private void ResetLevelState()
    {
        _windowCount = 0;
        _windowCursor = 0;
        _windowMisses = 0;
        _framesSinceJudgement = 0;
        _quietSeconds = 0;
        _secondsAtLevel = 0;
    }

    private (double Median, double Tail) Percentiles()
    {
        Array.Copy(_window, _scratch, _windowCount);
        var span = _scratch.AsSpan(0, _windowCount);
        span.Sort();

        var last = span.Length - 1;
        var median = span[Math.Clamp((int)Math.Round(last * 0.50), 0, last)];
        var tail = span[Math.Clamp((int)Math.Round(last * 0.99), 0, last)];

        return (median, tail);
    }

    public string Describe()
    {
        if (!_enabled)
        {
            return "off";
        }

        var evidence = _windowCount < MinimumSamplesToJudge
            ? $"warming up ({_windowCount} samples)"
            : $"median {_windowMedian:F2}ms p99 {_windowTail:F2}ms over {_windowCount} frames, " +
              (_paced ? $"{_windowMisses} missed" : "uncapped (no deadline to miss)");

        var probe = _quietPeriodSeconds <= FirstQuietPeriodSeconds
            ? string.Empty
            : $", next recovery after {_quietPeriodSeconds:F0}s";

        return $"scale {RenderScale:F2} ({_level + 1}/{Ladder.Length}), {evidence}, " +
               $"quiet {_quietSeconds:F1}s{probe}, degraded {_degradations}, " +
               $"recovered {_recoveries} | last: {_lastReason}";
    }
}
