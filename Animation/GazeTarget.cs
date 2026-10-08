namespace Live2DPet.Animation;

/// <summary>
/// Smoothed follower, ported from the framework's <c>CubismTargetPoint</c>.
///
/// A naive lerp towards the cursor looks wrong: the head starts and stops
/// instantly, which reads as a machine. This accelerates up to a maximum speed,
/// then decelerates on approach using the stopping distance implied by that
/// acceleration. The constants below are frame-based because that is how the
/// original is written, so the update has to scale by the nominal frame rate.
/// </summary>
internal sealed class GazeTarget
{
    private const int FrameRate = 30;
    private const float Epsilon = 0.01f;

    /// <summary>Roughly "forty degrees of head turn in ten seconds".</summary>
    private const float FaceParameterMaxVelocity = 40f / 10f;

    private const float MaximumVelocity = FaceParameterMaxVelocity / FrameRate;
    private const float TimeToMaxSpeedSeconds = 0.15f;
    private const float FramesToMaxSpeed = TimeToMaxSpeedSeconds * FrameRate;

    private float _targetX;
    private float _targetY;
    private float _x;
    private float _y;
    private float _velocityX;
    private float _velocityY;
    private float _lastTime;
    private float _userTime;

    public float X => _x;

    public float Y => _y;

    public void Set(float x, float y)
    {
        _targetX = x;
        _targetY = y;
    }

    public void Update(float deltaSeconds)
    {
        _userTime += deltaSeconds;

        if (_lastTime == 0f)
        {
            _lastTime = _userTime;
            return;
        }

        var deltaTimeWeight = (_userTime - _lastTime) * FrameRate;
        _lastTime = _userTime;

        var maxAcceleration = deltaTimeWeight * MaximumVelocity / FramesToMaxSpeed;

        var dx = _targetX - _x;
        var dy = _targetY - _y;

        if (MathF.Abs(dx) <= Epsilon && MathF.Abs(dy) <= Epsilon)
        {
            return;
        }

        var distance = MathF.Sqrt(dx * dx + dy * dy);

        var desiredVelocityX = MaximumVelocity * dx / distance;
        var desiredVelocityY = MaximumVelocity * dy / distance;

        var accelerationX = desiredVelocityX - _velocityX;
        var accelerationY = desiredVelocityY - _velocityY;
        var acceleration = MathF.Sqrt(accelerationX * accelerationX + accelerationY * accelerationY);

        if (acceleration < -maxAcceleration || acceleration > maxAcceleration)
        {
            accelerationX *= maxAcceleration / acceleration;
            accelerationY *= maxAcceleration / acceleration;
        }

        _velocityX += accelerationX;
        _velocityY += accelerationY;

        // Cap the speed by what a stop at this acceleration allows, so the head
        // eases into the target instead of overshooting and correcting.
        var stoppingVelocity = 0.5f *
            (MathF.Sqrt(maxAcceleration * maxAcceleration + 16f * maxAcceleration * distance -
                        8f * maxAcceleration * distance) - maxAcceleration);

        var currentVelocity = MathF.Sqrt(_velocityX * _velocityX + _velocityY * _velocityY);
        if (currentVelocity > stoppingVelocity && currentVelocity > 0f)
        {
            _velocityX *= stoppingVelocity / currentVelocity;
            _velocityY *= stoppingVelocity / currentVelocity;
        }

        _x += _velocityX;
        _y += _velocityY;
    }
}
