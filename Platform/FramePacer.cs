using System.Diagnostics;
using System.Runtime.InteropServices;
using Live2DPet.App;

namespace Live2DPet.Platform;

/// <summary>
/// Caps the render loop to a target rate.
///
/// This is not optional politeness. <c>Present</c> on a composition swap chain
/// does not block on vertical sync, so without an explicit pacer the loop free
/// runs: measured here at over a thousand frames per second for a still model.
/// That burns GPU time, memory bandwidth and battery for frames nobody sees, and
/// on a laptop sharing one GPU with a language model it competes for exactly the
/// resources the pet is supposed to leave alone.
///
/// Timing uses a high resolution waitable timer rather than <c>Thread.Sleep</c>,
/// whose granularity is tied to the system tick and can be an order of magnitude
/// worse than the frame budget.
/// </summary>
internal sealed class FramePacer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint Infinite = 0xFFFFFFFF;

    private readonly double _targetSeconds;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private nint _timer;
    private double _nextFrameTime;

    public FramePacer(double targetFramesPerSecond)
    {
        _targetSeconds = targetFramesPerSecond > 0 ? 1.0 / targetFramesPerSecond : 0;
        _timer = CreateWaitableTimerExW(0, null, CreateWaitableTimerHighResolution, TimerAllAccess);

        if (_timer == 0)
        {
            Log.Warn("high resolution timer unavailable; frame pacing will rely on the system timer");
        }
    }

    /// <summary>Blocks until the next frame is due. No-op when pacing is disabled.</summary>
    public void WaitForNextFrame()
    {
        if (_targetSeconds <= 0)
        {
            return;
        }

        // First call establishes the phase without sleeping.
        if (_nextFrameTime == 0)
        {
            _nextFrameTime = _clock.Elapsed.TotalSeconds;
        }

        var remaining = _nextFrameTime - _clock.Elapsed.TotalSeconds;
        if (remaining > 0)
        {
            Sleep(remaining);
        }

        _nextFrameTime += _targetSeconds;

        // If a frame ran long, resynchronise instead of trying to catch up with a
        // burst of frames nobody would see.
        var now = _clock.Elapsed.TotalSeconds;
        if (_nextFrameTime < now)
        {
            _nextFrameTime = now;
        }
    }

    private void Sleep(double seconds)
    {
        if (_timer == 0)
        {
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            return;
        }

        // Negative due time means "relative", expressed in 100 nanosecond units.
        var dueTime = -(long)(seconds * 10_000_000);
        if (!SetWaitableTimer(_timer, ref dueTime, 0, 0, 0, false))
        {
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            return;
        }

        WaitForSingleObject(_timer, Infinite);
    }

    public void Dispose()
    {
        if (_timer != 0)
        {
            CloseHandle(_timer);
            _timer = 0;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(
        nint timer,
        ref long dueTime,
        int period,
        nint completionRoutine,
        nint argumentToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
