using Live2DPet.Cubism;

namespace Live2DPet.Animation;

/// <summary>
/// Blinks on its own schedule.
///
/// This is a faithful port of the framework's <c>CubismEyeBlink</c>, including
/// the asymmetric timings: closing is quick, staying shut is very brief, and
/// opening is slower. Those three numbers are the difference between a blink
/// that reads as alive and one that reads as a mechanical shutter.
///
/// The interval between blinks is randomised over roughly zero to eight
/// seconds, which is what stops a row of models from blinking in unison.
/// </summary>
internal sealed class EyeBlink : IEffect
{
    private enum BlinkState
    {
        First,
        Interval,
        Closing,
        Closed,
        Opening,
    }

    private const float BlinkingIntervalSeconds = 4f;
    private const float ClosingSeconds = 0.1f;
    private const float ClosedSeconds = 0.05f;
    private const float OpeningSeconds = 0.15f;

    /// <summary>
    /// True for rigs where the eye-open parameter is 0 rather than 1 when the
    /// eye is shut. Cubism's own default is 1 means open.
    /// </summary>
    private const bool CloseIfZero = true;

    private readonly int[] _parameterIndices;
    private readonly Random _random = new();

    private BlinkState _state = BlinkState.First;
    private float _nextBlinkingTime;
    private float _stateStartTime;
    private float _userTime;

    public EyeBlink(CubismModel model, IReadOnlyList<string> parameterIds)
    {
        _parameterIndices = parameterIds
            .Select(model.GetParameterIndex)
            .Where(index => index >= 0)
            .ToArray();

        if (_parameterIndices.Length == 0)
        {
            throw new InvalidOperationException(
                "eye blink needs at least one parameter; none of the supplied ids exist in this model");
        }
    }

    public string Name => "blink";

    public void Update(CubismModel model, float deltaSeconds)
    {
        _userTime += deltaSeconds;

        float value;

        switch (_state)
        {
            case BlinkState.Closing:
            {
                var t = (_userTime - _stateStartTime) / ClosingSeconds;
                if (t >= 1f)
                {
                    t = 1f;
                    _state = BlinkState.Closed;
                    _stateStartTime = _userTime;
                }

                value = 1f - t;
                break;
            }

            case BlinkState.Closed:
            {
                var t = (_userTime - _stateStartTime) / ClosedSeconds;
                if (t >= 1f)
                {
                    _state = BlinkState.Opening;
                    _stateStartTime = _userTime;
                }

                value = 0f;
                break;
            }

            case BlinkState.Opening:
            {
                var t = (_userTime - _stateStartTime) / OpeningSeconds;
                if (t >= 1f)
                {
                    t = 1f;
                    _state = BlinkState.Interval;
                    _nextBlinkingTime = DetermineNextBlinkTime();
                }

                value = t;
                break;
            }

            case BlinkState.Interval:
                if (_nextBlinkingTime < _userTime)
                {
                    _state = BlinkState.Closing;
                    _stateStartTime = _userTime;
                }

                value = 1f;
                break;

            case BlinkState.First:
            default:
                _state = BlinkState.Interval;
                _nextBlinkingTime = DetermineNextBlinkTime();
                value = 1f;
                break;
        }

        if (!CloseIfZero)
        {
            value = -value;
        }

        foreach (var index in _parameterIndices)
        {
            model.SetParameterValue(index, value);
        }
    }

    private float DetermineNextBlinkTime() =>
        _userTime + (float)_random.NextDouble() * (2f * BlinkingIntervalSeconds - 1f);
}
