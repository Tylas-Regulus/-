namespace Live2DPet.App;

/// <summary>A change of quality, with the evidence that caused it.</summary>
internal readonly record struct GovernorChange(float RenderScale, int Pixels, double Fps, int Level, string Reason);

/// <summary>
/// Which lever automatic mode spends first.
///
/// The two answers are not a matter of taste: they are the two things a person
/// can care about, and only one of them can be bought first.
///
/// <see cref="Quality"/> (画质优先) keeps the picture whole for as long as it can
/// and pays with frames: the ceiling walks down to the defended rate first, and
/// only then does the picture shrink, down to 480p.
///
/// <see cref="Framerate"/> (帧率优先) keeps the frame rate the owner asked for and
/// pays with the picture: it shrinks to 480p first, and only then does the rate
/// follow, down to the defended floor.
///
/// Both end at the same place - 480p at the defended rate - so the choice is
/// only about what the machine looks like on the way there.
/// </summary>
internal enum AutoPlan
{
    Quality,
    Framerate,
}

/// <summary>The names the UI and the state file use for <see cref="AutoPlan"/>.</summary>
internal static class AutoPlans
{
    /// <summary>What the editor and the log call this plan.</summary>
    public static string Label(AutoPlan plan) => plan switch
    {
        AutoPlan.Framerate => "帧率优先（先降画质）",
        _ => "画质优先（先降帧率）",
    };

    /// <summary>Reads <c>quality</c> or <c>framerate</c>, the two words the state
    /// file and <c>--auto-plan</c> take.</summary>
    public static bool TryParse(string text, out AutoPlan plan)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "quality":
            case "画质":
            case "画质优先":
                plan = AutoPlan.Quality;
                return true;
            case "framerate":
            case "fps":
            case "帧率":
            case "帧率优先":
                plan = AutoPlan.Framerate;
                return true;
            default:
                plan = AutoPlan.Quality;
                return false;
        }
    }

    /// <summary>The state file's spelling.</summary>
    public static string Key(AutoPlan plan) => plan == AutoPlan.Framerate ? "framerate" : "quality";
}

/// <summary>
/// One rung of the automatic ladder: how large the picture is drawn - in pixels
/// of height, and as the multiplier that produces them - and how many frames a
/// second the loop may reach there.
/// </summary>
internal readonly record struct GovernorStep(float Scale, int Pixels, double Fps);

/// <summary>
/// Decides what to give up, and in what order, to hold the frame rate.
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
/// What is left is two signals that separate with a wide margin, plus one rule
/// about when they mean anything:
///
///   * the <b>median</b> frame time clearing the budget by several percent, which
///     means the loop is not keeping up at all (measured 16.65ms healthy against
///     20.8ms failing). It is consulted only while the loop may run faster than
///     the rate being defended - see <see cref="Observe"/>;
///   * the <b>fraction of frames that missed a sync</b>, which catches a machine
///     whose median is fine but which stutters anyway. At a ceiling of 60 with a
///     floor of 60 - the setting a game player wants - this is the whole of the
///     evidence.
///
/// It does not consult the GPU timestamp. That was the original plan - watch
/// headroom, degrade before a miss happens - and milestone 5 killed it: the
/// timestamp turned out to be blind to the levers themselves. Eighty per cent
/// fewer draw calls moved it by nothing, and it reads zero under WARP, which is
/// the only machine here that can actually be made to fail. A signal that cannot
/// see the effect of the action is not a control input.
///
/// <b>What it gives up, and in what order.</b> There are two levers, not one,
/// and the order between them is <see cref="AutoPlan"/>: the owner says which of
/// the two things they would rather keep, and the ladder is written accordingly.
///
/// 画质优先 (<see cref="AutoPlan.Quality"/>, the default) spends frames first:
/// the ceiling walks down the standard rates to the defended one, and only then
/// does the picture shrink - in one run, down to 480. 帧率优先
/// (<see cref="AutoPlan.Framerate"/>) spends the picture first, down to 480, and
/// only then the frame rate. Both stop at 480p at the defended rate: past that
/// the pet is a smear with a silhouette and the upscale blit is paying for
/// pixels nobody is getting.
///
/// All three phases are one list, and a level is an index into it: "give up" is
/// one step forward, "get it back" is one step back. There is no second set of
/// rules to keep in step with the first, and the picture and the frame rate can
/// never disagree about how far down the ladder the machine has been walked.
///
/// The frame-rate lever only ever lowers a ceiling. The ceiling itself is the
/// owner's number, wherever it was typed - the 其他 page, the file, or
/// <c>--fps</c> - and an automatic scheme that overruled it would be answering a
/// question nobody asked. What the scheme does own is the rate it <em>defends</em>:
/// sixty, or whatever was asked for, and never above the ceiling, because a
/// rate the loop is not allowed to reach is not a floor but a contradiction.
///
/// Down the ladder is fast, up is slow, and the ladder cannot cascade: every
/// level gets a dwell period and a window of evidence before it is judged, and
/// coming back up needs a quiet period that doubles each time a step up turns
/// out to have been a mistake.
///
/// Every count it keeps is windowed, the miss counter included. A counter that
/// only ever grew was the reason a machine that had been busy once could never
/// be sharp again: two stalls at a level held the miss rate above the recovery
/// threshold for the rest of the run, and a level that cannot be left upward is
/// the floor. See <see cref="Push"/>.
/// </summary>
internal sealed class QualityGovernor
{
    /// <summary>
    /// The pixel heights the picture may be drawn at, best first.
    ///
    /// Absolute pixels of height rather than multipliers, because a multiplier
    /// means a different picture at every pet size: 0.50 on a full-sized pet is
    /// 450 rows, and the same 0.50 on a pet kept at 0.47 is 211 - below anything
    /// worth drawing, and below the point where the offscreen upscale blit costs
    /// more than the pixels it saves. Height is also the thing a person can
    /// judge: "720p" is a promise, "0.80" is arithmetic.
    ///
    /// The ladder used to end at a quarter of the window and was walked by
    /// multiplier. Two measurements took the bottom off it: below a half the
    /// upscale blit is a fixed cost the shrinking picture has stopped paying
    /// for, so one logical processor went from 10.5ms at 0.35 to 9.4ms at 0.25 -
    /// the last two rungs together buy about a millisecond, and they buy it with
    /// a picture that is no longer the character.
    ///
    /// The list is the same for both plans; what differs is when it is walked.
    /// </summary>
    private static readonly int[] QualityRungs = [900, 810, 720, 640, 560, 480];

    /// <summary>
    /// The floor on picture size. Below 480 the pet is a blur with a silhouette
    /// rather than a smaller picture of the character, and a pet that small on
    /// screen has proportionally fewer pixels to give away in the first place -
    /// so a pet already below this height has no quality rungs at all, and the
    /// frame rate is the only thing the governor may spend.
    ///
    /// Public because the 画质 page states the same floor: a page that promises
    /// "先降画质到 480p" about a pet that is already smaller than 480 is
    /// promising something the ladder has no rung for. One number, two places
    /// that have to agree about it.
    /// </summary>
    public const int FloorPixels = 480;

    /// <summary>
    /// The frame rates the frame lever may choose, best first.
    ///
    /// Standard rates rather than a ratio: a cap lands on a period a panel can
    /// actually hold, so a 144 Hz display asked for 120 is asked for 8.33ms and
    /// not for 8.57. The list is generous at the top and tight at the bottom
    /// because that is where the money is - 144 to 120 is 17% of the work, 72 to
    /// 60 is another 17% - and because the last rung is the one that has to be
    /// true for the pet to look alive.
    /// </summary>
    private static readonly double[] RateRungs = [240, 165, 144, 120, 90, 72, 60];

    /// <summary>
    /// The floor on frame rate: the rate the whole automatic scheme is built
    /// around, and the one rung the ladder never passes.
    ///
    /// It is a floor on the rate the scheme *defends*, not on the owner's
    /// ceiling: a pet deliberately asked for 30 frames to save power runs at 30,
    /// and the governor's only lever there is the picture. See
    /// <see cref="RecomputeRates"/>.
    /// </summary>
    public const double FrameFloor = 60;

    /// <summary>Same threshold <see cref="FrameStats"/> uses to count a missed
    /// sync: a frame has to overrun by half a refresh before it certainly lost a
    /// whole slot rather than merely being late.</summary>
    private const double MissFactor = 1.5;

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

    /// <summary>Five minutes was the first answer, and it promises that a machine
    /// which had one bad minute spends the next twenty-five climbing back out of
    /// the floor. A minute bounds that without bringing back the bouncing the
    /// doubling was written to stop - the bouncing came from probing too *often*,
    /// not from how long a failed probe waits.</summary>
    private const double MaxQuietPeriodSeconds = 60.0;

    /// <summary>A degrade this soon after an upgrade means the upgrade was wrong
    /// rather than that the machine got slower.</summary>
    private const double FailedProbeWindowSeconds = 5.0;

    /// <summary>Startup allocates targets and compiles shaders; those frames are
    /// not evidence about steady state.</summary>
    private const double WarmupSeconds = 2.0;

    private bool _enabled;

    /// <summary>The rate the owner asked the scheme to defend, before the
    /// ceiling gets a say. Sixty unless <c>--min-fps</c> said otherwise.</summary>
    private readonly double _floorAsked;

    /// <summary>The budget a frame is measured against: the reciprocal of the
    /// rate actually defended. Recomputed when the ceiling moves, because a
    /// ceiling below the asked-for floor wins - see <see cref="RecomputeRates"/>.</summary>
    private double _budgetMs;

    /// <summary>The rate the frame lever must not go below. The scheme's own
    /// floor of 60, or the asked-for one, and never above the ceiling.</summary>
    private double _rateFloor;

    /// <summary>Which lever is spent first: see <see cref="AutoPlan"/>.</summary>
    private AutoPlan _plan;

    /// <summary>The ladder, best rung first. Rebuilt whenever the window changes
    /// size or the owner changes the frame ceiling, because both of them decide
    /// which rungs exist at all.</summary>
    private readonly List<GovernorStep> _steps = [];

    /// <summary>How tall the pet is drawn on screen, in pixels. The picture's
    /// height is this multiplied by the current rung's scale, which is why the
    /// ladder is written in pixels rather than in multipliers.</summary>
    private float _windowHeight = 900f;

    /// <summary>The ceiling the owner chose, as typed, or zero for no ceiling.
    /// Only a ceiling above the defended rate has any frame-rate rungs to
    /// give.</summary>
    private double _ceilingFps;

    private readonly double[] _window = new double[WindowCapacity];
    private readonly double[] _scratch = new double[WindowCapacity];

    /// <summary>Whether each slot of <see cref="_window"/> was a missed sync.
    /// Kept beside the frame times so a miss ages out of the window along with the
    /// frame that caused it instead of being counted for the rest of the level.</summary>
    private readonly bool[] _windowIsMiss = new bool[WindowCapacity];

    private int _windowCount;
    private int _windowCursor;
    private int _windowMisses;
    private int _framesSinceJudgement;

    private int _level;

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

    /// <summary>The last "this lever is empty" sentence that was logged, so a
    /// rebuild that changes nothing about it does not repeat itself. Empty means
    /// both levers have rungs, which is also the state that re-arms the message
    /// for the next time one of them runs out.</summary>
    private string _leverNote = string.Empty;

    /// <param name="floorFramesPerSecond">
    /// The rate to defend. This is a floor, not a ceiling: on a machine that can
    /// do more it has nothing to do, and on one that cannot it gives up quality
    /// until the floor is met.
    /// </param>
    /// <param name="ceilingFramesPerSecond">
    /// The rate the loop may reach, or zero for no ceiling. It decides which
    /// signals exist - only a paced loop can miss a sync - and, together with the
    /// floor, whether the median says anything at all. It is also the top of the
    /// frame-rate ladder.
    /// </param>
    /// <param name="windowHeight">
    /// How tall the pet is drawn, in pixels, so the ladder can be built out of
    /// real pixel heights. Kept up to date by <see cref="SetWindowHeight"/>.
    /// </param>
    /// <param name="plan">
    /// Which lever is spent first: frames (画质优先) or picture (帧率优先).
    /// </param>
    public QualityGovernor(
        bool enabled,
        double floorFramesPerSecond,
        double ceilingFramesPerSecond,
        float windowHeight,
        AutoPlan plan)
    {
        _enabled = enabled && floorFramesPerSecond > 0;
        _floorAsked = floorFramesPerSecond;
        _plan = plan;
        _ceilingFps = Math.Max(0, ceilingFramesPerSecond);
        _windowHeight = MathF.Max(1f, windowHeight);

        RecomputeRates();
        Rebuild();
    }

    /// <summary>
    /// Works out what the scheme defends from what it was told.
    ///
    /// The ceiling is the owner's number and is never raised: a pet asked for 30
    /// frames runs at 30, and the governor's job there is the picture alone. What
    /// is adjusted is the rate it defends - sixty by default - which cannot be
    /// above the ceiling, because a floor the loop is forbidden to reach would
    /// have it chasing a deadline it can never make: every frame a "miss", the
    /// picture walked to 480 for nothing.
    ///
    /// That case is not hypothetical: the 其他 page offers 30 frames to save
    /// power, and the note beside it promised "按 60 帧跑" - a pet asked for a
    /// quiet 30 that ran at 60 instead. The clamp here is what makes the page's
    /// own answer true, and it is also what the 5-second notice recommends when
    /// even 480p cannot hold sixty.
    /// </summary>
    private void RecomputeRates()
    {
        var floor = Math.Max(FrameFloor, _floorAsked);

        if (_ceilingFps > 0 && floor > _ceilingFps)
        {
            floor = _ceilingFps;

            if (_enabled && floor < Math.Max(FrameFloor, _floorAsked))
            {
                Log.Info(
                    $"quality: ceiling {_ceilingFps:F0} FPS 低于要守的 {Math.Max(FrameFloor, _floorAsked):F0} 帧，" +
                    $"按 {floor:F0} 帧守");
            }
        }

        _rateFloor = floor;
        _budgetMs = _enabled ? 1000.0 / floor : 0;
    }

    /// <summary>Whether the loop is paced at all. Only a paced loop has a
    /// deadline to miss, so this decides whether the miss signal exists.</summary>
    private bool Paced => _enabled && _ceilingFps > 0;

    /// <summary>
    /// Whether the median frame time says anything about the machine.
    ///
    /// It says "the achieved rate is below the floor" only while the loop is
    /// *trying* to reach the floor - that is, while the ceiling is at or above
    /// it. A ceiling below the floor is a pacer deliberately holding every frame
    /// longer than the budget (ask for 30 frames and defend 60 and the median
    /// reads 33.5ms against a 16.7ms budget forever), and there the median is a
    /// description of the ceiling rather than of the machine, so missed syncs are
    /// the only evidence left.
    ///
    /// A ceiling equal to the floor is *not* that case, and it is worth saying
    /// because it looks like one: measured on an idle machine, a 30 FPS ceiling
    /// against a 30 FPS floor reads p50 33.48ms against a 33.33ms budget - four
    /// hundredths of a percent of the pacer's own overshoot against a five per
    /// cent threshold. The signal still works there.
    /// </summary>
    private bool MedianInforms =>
        _enabled && (!Paced || 1000.0 / _ceilingFps <= _budgetMs);

    public bool Enabled => _enabled;

    /// <summary>
    /// Turns the governor on or off while the pet is running, from the editor's
    /// 画质 page.
    ///
    /// Off means "the numbers in the file are the owner's; leave them alone" - the
    /// render scale *and* the frame ceiling, which is why switching it off puts
    /// the ceiling back where the file says it should be. On resumes from the
    /// scale that is already on screen rather than from the top of the ladder - a
    /// page that turned it off and on again should not throw the picture away -
    /// and re-arms the warm-up, because the frames around the switch are evidence
    /// about neither state.
    /// </summary>
    public void SetEnabled(bool enabled, float currentScale)
    {
        if (enabled == _enabled)
        {
            return;
        }

        _enabled = enabled;

        if (!enabled)
        {
            _steps.Clear();
            _level = 0;
            return;
        }

        Rebuild();
        StartAt(currentScale);
        ResetLevelState();
        _elapsedSeconds = 0;
    }

    public float RenderScale => _steps.Count == 0 ? 1f : _steps[_level].Scale;

    /// <summary>How tall the picture is drawn right now, in pixels - the number
    /// the ladder is actually written in.</summary>
    public int Pixels => _steps.Count == 0 ? 0 : _steps[_level].Pixels;

    /// <summary>The rung the ladder is standing on. A rung of zero when the
    /// governor is off, which is what "build the ladder out of this" gets.</summary>
    private GovernorStep Current =>
        _steps.Count == 0 ? default : _steps[Math.Clamp(_level, 0, _steps.Count - 1)];

    /// <summary>The ceiling this rung allows. Zero means the rung has no cap.</summary>
    public double TargetFps => _steps.Count == 0 ? _ceilingFps : _steps[_level].Fps;

    /// <summary>The ceiling the ladder is built around: the owner's number with
    /// the automatic floor applied. Not the same as <see cref="TargetFps"/>,
    /// which is the rung the ladder is standing on right now.</summary>
    public double CeilingFps => _ceilingFps;

    public int Level => _level;

    public int LevelCount => _steps.Count;

    public int Degradations => _degradations;

    public int Recoveries => _recoveries;

    /// <summary>
    /// Whether a degrade was wanted at the last rung and there was nowhere left
    /// to go - the machine cannot hold the defended rate even at 480p.
    ///
    /// Latched for the run on purpose: the governor keeps judging and keeps
    /// finding the same answer, and the person in front of the screen only needs
    /// to be told once. It is also what the notice above the pet is for; the
    /// governor itself has nothing left to spend.
    /// </summary>
    public bool SunkToFloor { get; private set; }

    public double BudgetMs => _budgetMs;

    /// <summary>The rate this governor is defending, which is the floor it was
    /// built with rather than the ceiling it may be reined in to.</summary>
    public double FloorFps => _budgetMs > 0 ? 1000.0 / _budgetMs : 0;

    /// <summary>
    /// Starts the ladder at the rung a remembered decision points at.
    ///
    /// The state file keeps both halves of that decision - the multiplier and the
    /// frame rate that went with it - and the pair names a rung even where the
    /// multiplier cannot, because five rungs share one: 720 pixels at 144, 120,
    /// 90, 72 and 60 frames are all 0.80 of a 900 pixel window.
    ///
    /// <paramref name="renderFps"/> is null on a first run, and in a file written
    /// before the frame rate was kept. Then the tie goes to the freshest rung -
    /// the highest rate - because being wrong downward is the expensive way to be
    /// wrong: the ladder is built to walk down in seconds and back up in tens of
    /// seconds, so it can afford to find out that it aimed too high and cannot
    /// afford to sit at a frame rate it never needed.
    ///
    /// Not to be confused with the file's `fps` line, which is the ceiling the
    /// owner chose. That one is passed to the constructor as the ceiling and is
    /// never treated as "where the governor was".
    ///
    /// Only the starting point is restored, not the state: the governor still
    /// has to earn the next step up with a quiet period, so a machine that was
    /// busy last time but idle now climbs back on its own.
    /// </summary>
    public void StartAt(float renderScale, double? renderFps = null)
    {
        if (!_enabled || _steps.Count == 0)
        {
            return;
        }

        var best = 0;
        for (var i = 1; i < _steps.Count; i++)
        {
            if (Closer(_steps[i], renderScale, renderFps, _steps[best]))
            {
                best = i;
            }
        }

        _level = best;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> answers "which rung is this decision
    /// about" better than <paramref name="than"/> does.
    ///
    /// The multiplier first, because that is the half a person can see - and
    /// because comparing picture height instead is wrong in a way that only shows
    /// up after a resize, which <see cref="Reseat"/> has the long version of. The
    /// frame rate breaks a tie, but only when there is one to break: a decision
    /// with no frame rate recorded leaves the tie to the earliest rung, and the
    /// ladder is written best first.
    /// </summary>
    private static bool Closer(GovernorStep candidate, float scale, double? fps, GovernorStep than)
    {
        var byScale = MathF.Abs(candidate.Scale - scale);
        var thanScale = MathF.Abs(than.Scale - scale);

        if (byScale != thanScale)
        {
            return byScale < thanScale;
        }

        if (fps is not { } wanted)
        {
            return false;
        }

        return Math.Abs(candidate.Fps - wanted) < Math.Abs(than.Fps - wanted);
    }

    /// <summary>
    /// Says how tall the pet is drawn right now, which is half of what decides
    /// the ladder.
    ///
    /// The floor is defined in pixels of picture rather than in fractions of the
    /// window, so a pet kept small on screen has proportionally fewer rungs to
    /// give, and a pet small enough ends up with none at all. Making the *pet*
    /// smaller is an honest way to spend less on a weak machine; making the
    /// *picture* blurry is not: the first keeps every pixel sharp and the second
    /// throws pixels away.
    ///
    /// Called once a frame, so it does nothing at all unless the answer changed.
    /// </summary>
    public void SetWindowHeight(float height)
    {
        if (height < 1f || MathF.Abs(height - _windowHeight) < 0.5f)
        {
            return;
        }

        var previous = Current;
        _windowHeight = height;

        // Remembered even while the governor is off: the pet remembers its size
        // across a restart, and the frame the 画质 page hands the ladder back is
        // not the frame to build it out of a window that no longer exists.
        if (!_enabled)
        {
            return;
        }

        Rebuild();
        Reseat(previous);
        ResetLevelState();
        Log.Info($"quality: window {height:F0}px tall -> ladder {DescribeLadder()}");
    }

    /// <summary>
    /// Says what ceiling the owner chose, so the frame-rate rungs can be rebuilt
    /// around it. Called when the 其他 page's 帧率上限 changes under a running
    /// pet.
    ///
    /// A ceiling at or below the defended rate leaves the frame-rate lever with
    /// nothing to give; a ceiling below the asked-for floor also lowers the rate
    /// the scheme defends - see <see cref="RecomputeRates"/>.
    /// </summary>
    public void SetCeiling(double framesPerSecond)
    {
        var wanted = Math.Max(0, framesPerSecond);

        if (Math.Abs(wanted - _ceilingFps) < 0.001)
        {
            return;
        }

        var previous = Current;
        _ceilingFps = wanted;

        if (!_enabled)
        {
            return;
        }

        RecomputeRates();
        Rebuild();
        Reseat(previous);
        ResetLevelState();
        Log.Info($"quality: ceiling {_ceilingFps:F0} FPS -> ladder {DescribeLadder()}");
    }

    /// <summary>
    /// Changes which lever is spent first. Rebuilt rather than re-ordered in
    /// place: every rung is a (pixels, frames) pair, and the pairs differ
    /// between plans, so the ladder is a different list rather than the same one
    /// sorted differently.
    /// </summary>
    public void SetPlan(AutoPlan plan)
    {
        if (plan == _plan)
        {
            return;
        }

        _plan = plan;

        if (!_enabled)
        {
            return;
        }

        var previous = Current;
        Rebuild();
        Reseat(previous);
        ResetLevelState();
        Log.Info($"quality: plan {AutoPlans.Label(_plan)} -> ladder {DescribeLadder()}");
    }

    /// <summary>Which lever is spent first.</summary>
    public AutoPlan Plan => _plan;

    /// <summary>
    /// Takes one frame's duration. Returns a change when the governor moved the
    /// quality level, otherwise null.
    /// </summary>
    public GovernorChange? Observe(double frameMs)
    {
        if (!_enabled || _steps.Count == 0)
        {
            return null;
        }

        var seconds = frameMs / 1000.0;
        _elapsedSeconds += seconds;

        if (_elapsedSeconds < WarmupSeconds)
        {
            return null;
        }

        // Whether this frame missed its slot is decided here and travels with the
        // sample, so the counter and the window can never disagree about how many
        // of the frames still in evidence were late.
        Push(frameMs, frameMs > _budgetMs * MissFactor);

        _quietSeconds += seconds;
        _secondsAtLevel += seconds;
        _secondsSinceUpgrade += seconds;

        if (frameMs > _budgetMs * MissFactor)
        {
            _quietSeconds = 0;
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

        if (settled && MedianInforms && median > _budgetMs * OverloadMedianFactor)
        {
            return Degrade(
                $"median {median:F2}ms against a {_budgetMs:F2}ms budget over {_windowCount} frames");
        }

        // Only a paced loop can miss a sync: an uncapped one has no deadline to
        // miss, so counting "misses" there would be counting frames that were
        // merely slower than the floor.
        if (Paced && settled && _windowMisses >= MinimumMissesToDegrade && missRate > MissRateToDegrade)
        {
            return Degrade(
                $"{_windowMisses} of {_windowCount} frames missed a sync ({missRate:P0})",
                overload: true);
        }

        if (_level > 0 &&
            _quietSeconds >= _quietPeriodSeconds &&
            (!MedianInforms || median < _budgetMs * RecoveryMedianFactor) &&
            missRate <= RecoveryMissRate)
        {
            return Recover(median);
        }

        return null;
    }

    private GovernorChange? Degrade(string reason, bool overload = false)
    {
        if (_level >= _steps.Count - 1)
        {
            SunkToFloor = true;
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
        _secondsSinceUpgrade = double.PositiveInfinity;
        _lastReason = reason;
        ResetLevelState();

        return new GovernorChange(RenderScale, Pixels, TargetFps, _level, reason);
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
        return new GovernorChange(RenderScale, Pixels, TargetFps, _level, reason);
    }

    /// <summary>
    /// Lays the ladder out for the current window and ceiling.
    ///
    /// Two phases, in the order <see cref="AutoPlan"/> asks for.
    ///
    /// 画质优先: the frame rate down to its floor at full size, then the picture
    /// down to 480 at that floor. 帧率优先: the picture down to 480 at the
    /// ceiling, then the frame rate down to its floor.
    ///
    /// A ceiling at or below the defended rate makes the frame phase empty, and a
    /// pet already at or below 480 pixels tall makes the picture phase empty -
    /// either way the ladder can come out with a single rung, which
    /// <see cref="ExplainLeverCounts"/> says out loud.
    /// </summary>
    private void Rebuild()
    {
        _steps.Clear();

        if (!_enabled)
        {
            _level = 0;
            return;
        }

        var top = MathF.Max(1f, _windowHeight);
        var bottom = MathF.Min(top, FloorPixels);
        var frameLever = _rateFloor < _ceilingFps;
        var bottomFps = frameLever ? _rateFloor : _ceilingFps;

        GovernorStep At(float pixels, double fps) => new(pixels / top, (int)MathF.Round(pixels), fps);

        // A rung that repeats the one before it is not a rung: at a pet already
        // at the floor both phases can land on the same (pixels, frames) pair.
        void Add(GovernorStep step)
        {
            if (_steps.Count > 0 && _steps[^1].Pixels == step.Pixels &&
                Math.Abs(_steps[^1].Fps - step.Fps) < 0.001)
            {
                return;
            }

            _steps.Add(step);
        }

        // Counted, not inferred afterwards, because "no rung of this kind" is
        // what has to be said out loud - see ExplainLeverCounts.
        var pictureRungs = 0;
        var frameRungs = 0;

        _steps.Add(At(top, _ceilingFps));

        if (_plan == AutoPlan.Quality)
        {
            // Frames first, at the size the owner is looking at.
            if (frameLever)
            {
                foreach (var rate in RateRungs)
                {
                    if (rate < _ceilingFps && rate > _rateFloor)
                    {
                        Add(At(top, rate));
                        frameRungs++;
                    }
                }

                Add(At(top, _rateFloor));
                frameRungs++;
            }

            // Then the picture, at the rate that is left.
            foreach (var pixels in QualityRungs)
            {
                if (pixels < top && pixels >= FloorPixels)
                {
                    Add(At(pixels, bottomFps));
                    pictureRungs++;
                }
            }
        }
        else
        {
            // The picture first, at the ceiling the owner asked for.
            foreach (var pixels in QualityRungs)
            {
                if (pixels < top && pixels >= FloorPixels)
                {
                    Add(At(pixels, _ceilingFps));
                    pictureRungs++;
                }
            }

            // Then the frame rate, at the smallest picture there is.
            if (frameLever)
            {
                foreach (var rate in RateRungs)
                {
                    if (rate < _ceilingFps && rate > _rateFloor)
                    {
                        Add(At(bottom, rate));
                        frameRungs++;
                    }
                }

                Add(At(bottom, _rateFloor));
                frameRungs++;
            }
        }

        _level = Math.Clamp(_level, 0, Math.Max(0, _steps.Count - 1));

        ExplainLeverCounts(top, pictureRungs, frameRungs);
    }

    /// <summary>
    /// Says out loud when one of the two levers has nothing in it.
    ///
    /// A ladder with one rung looks exactly like a broken governor from the
    /// outside: the per-second line keeps printing, the picture never changes,
    /// and the frame rate drops anyway. Worse, it has two causes that need
    /// opposite fixes from whoever reads the log - a pet smaller than the floor
    /// has no quality to give, and a ceiling already sitting on the floor has no
    /// frames to give - and the old log let the reader infer neither, because a
    /// ladder that silently lost its rungs between two lines looks the same as
    /// one that never had them.
    ///
    /// Printed once per change rather than once per rebuild: the note is the
    /// same on every notch of a wheel resize, and a log that repeats it is a log
    /// somebody stops reading.
    /// </summary>
    private void ExplainLeverCounts(float top, int pictureRungs, int frameRungs)
    {
        var pictureReason = pictureRungs > 0
            ? string.Empty
            : $"the pet is {top:F0}px tall, at or under the {FloorPixels}px floor, " +
              "so there is nothing left to shrink";

        var frameReason = frameRungs > 0
            ? string.Empty
            : _ceilingFps <= 0
                ? "there is no ceiling, so there is no deadline to defend"
                : $"the ceiling ({_ceilingFps:F0} FPS) is already at the {_rateFloor:F0} FPS floor";

        var note = (pictureReason, frameReason) switch
        {
            ("", "") => string.Empty,
            (_, "") => $"no picture rungs - {pictureReason}; the frame rate is the only lever",
            ("", _) => $"no frame-rate rungs - {frameReason}; the picture is the only lever",
            _ => $"nothing to spend - {pictureReason}, and {frameReason}",
        };

        if (note.Length > 0 && note != _leverNote)
        {
            Log.Info($"quality: {note}");
        }

        _leverNote = note;
    }

    /// <summary>
    /// Puts the ladder back where it was, after a rebuild has changed what a
    /// level means.
    ///
    /// The list is not the same afterwards - a smaller window has fewer quality
    /// rungs and a lower ceiling fewer frame ones - so the level *index* now
    /// points at a rung nobody chose. Clamping it is not enough, and the case
    /// that shows why is the cheap one: standing on 640p@60 and raising the
    /// ceiling to 144 leaves index 3 on 720p@144, which is a sharper picture and
    /// two and a half times the frames, handed over in one frame. That is not a
    /// recovery, it is the stutter the ladder exists to avoid.
    ///
    /// What has to survive the rebuild is the answer to "how much has been given
    /// up", and the multiplier is what that is. Comparing pixels instead looks
    /// right and is not: a pet grown from 423 to 900 pixels, standing on the only
    /// rung it had (1.00, which is no *degradation* at all) would be reseated onto
    /// 480 - the same picture height, and half the sharpness of the window it was
    /// just made bigger to show. The frame rate then breaks the tie between the
    /// rungs that share a multiplier, and that is every rung the frame lever has
    /// been down to.
    /// </summary>
    private void Reseat(GovernorStep previous)
    {
        if (_steps.Count == 0)
        {
            _level = 0;
            return;
        }

        var best = 0;

        for (var i = 1; i < _steps.Count; i++)
        {
            if (Closer(_steps[i], previous.Scale, previous.Fps, _steps[best]))
            {
                best = i;
            }
        }

        _level = best;
    }

    /// <summary>
    /// Records one frame, and the slot it lands in takes the place of the oldest
    /// one - including its miss, which leaves the counter as the frame leaves the
    /// window. That subtraction is the whole difference between a rate and a
    /// tally, and the tally was a one-way door.
    /// </summary>
    private void Push(double frameMs, bool missed)
    {
        if (_windowIsMiss[_windowCursor])
        {
            _windowMisses--;
        }

        _window[_windowCursor] = frameMs;
        _windowIsMiss[_windowCursor] = missed;

        if (missed)
        {
            _windowMisses++;
        }

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
        Array.Clear(_windowIsMiss);
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

    /// <summary>The ladder as a sentence, for the startup log and for whoever
    /// reads it afterwards.</summary>
    public string DescribeLadder()
    {
        if (_steps.Count == 0)
        {
            return "none (governor off)";
        }

        var parts = new string[_steps.Count];

        for (var i = 0; i < _steps.Count; i++)
        {
            var step = _steps[i];
            parts[i] = step.Fps > 0 ? $"{step.Pixels}p@{step.Fps:F0}" : $"{step.Pixels}p";
        }

        return string.Join(" -> ", parts);
    }

    public string Describe()
    {
        if (!_enabled || _steps.Count == 0)
        {
            return "off";
        }

        var evidence = _windowCount < MinimumSamplesToJudge
            ? $"warming up ({_windowCount} samples)"
            : (MedianInforms ? $"median {_windowMedian:F2}ms " : "median not consulted ")
              + $"p99 {_windowTail:F2}ms over {_windowCount} frames, "
              + (Paced ? $"{_windowMisses} missed" : "uncapped (no deadline to miss)")
              + (_level < _steps.Count - 1 ? $", floor {_steps[^1].Pixels}p" : string.Empty);

        var probe = _quietPeriodSeconds <= FirstQuietPeriodSeconds
            ? string.Empty
            : $", next recovery after {_quietPeriodSeconds:F0}s";

        var cap = TargetFps > 0 ? $"{TargetFps:F0} FPS" : "no cap";

        // "1/1" on its own reads like a governor that is doing its job at the
        // only level there is. It is the opposite: a one-rung ladder is a
        // governor that cannot move, and the line says so where the count is.
        var rungs = _steps.Count == 1
            ? "1/1, nothing to spend"
            : $"{_level + 1}/{_steps.Count}";

        return $"scale {RenderScale:F2} ({Pixels}p, {cap}, {rungs}), {evidence}, " +
               $"quiet {_quietSeconds:F1}s{probe}, degraded {_degradations}, " +
               $"recovered {_recoveries} | last: {_lastReason}";
    }
}
