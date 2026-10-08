using System.Numerics;
using Live2DPet.App;
using Live2DPet.Cubism;

namespace Live2DPet.Physics;

internal enum PhysicsSource
{
    X,
    Y,
    Angle,
}

internal struct PhysicsNormalization
{
    public float Minimum;
    public float Maximum;
    public float Default;
}

/// <summary>One node of a pendulum chain ("strand").</summary>
internal sealed class PhysicsParticle
{
    public Vector2 InitialPosition;
    public float Mobility;
    public float Delay;
    public float Acceleration;
    public float Radius;

    public Vector2 Position;
    public Vector2 LastPosition;
    public Vector2 LastGravity;
    public Vector2 Force;
    public Vector2 Velocity;
}

internal sealed class PhysicsInput
{
    public string SourceId = string.Empty;
    public int SourceParameterIndex = -1;
    public float Weight;
    public PhysicsSource Type;
    public bool Reflect;
}

internal sealed class PhysicsOutput
{
    public string DestinationId = string.Empty;
    public int DestinationParameterIndex = -1;
    public int VertexIndex;
    public float AngleScale;
    public float Weight;
    public PhysicsSource Type;
    public bool Reflect;

    /// <summary>Extremes seen since load, for diagnostics only.</summary>
    public float ValueBelowMinimum;

    public float ValueExceededMaximum;
}

internal sealed class PhysicsSubRig
{
    public int InputCount;
    public int OutputCount;
    public int ParticleCount;
    public int BaseInputIndex;
    public int BaseOutputIndex;
    public int BaseParticleIndex;
    public PhysicsNormalization NormalizationPosition;
    public PhysicsNormalization NormalizationAngle;
}

/// <summary>
/// Port of the framework's <c>CubismPhysics</c>.
///
/// Each entry in <c>physics3.json</c> is a chain of particles. Parameters push
/// the first particle around, gravity and air resistance do the rest, and the
/// direction between successive particles is written back out as a parameter -
/// which is how a hair strand's swing becomes a rotation.
///
/// Two details are deliberate, unfaithful-looking ports of the original:
/// the 2D rotation in <see cref="Evaluate"/> and in <see cref="UpdateParticles"/>
/// both feed the already-updated X component into the Y component's expression.
/// That is aliasing in the C++ source, and reproducing it is the only way to get
/// the same motion out.
/// </summary>
internal sealed class PhysicsSimulator
{
    private const float AirResistance = 5f;
    private const float MaximumWeight = 100f;
    private const float MovementThreshold = 0.001f;
    private const float MaxDeltaTime = 5f;

    private readonly PhysicsSubRig[] _settings;
    private readonly PhysicsInput[] _inputs;
    private readonly PhysicsOutput[] _outputs;
    private readonly PhysicsParticle[] _particles;
    private readonly float[][] _currentRigOutputs;
    private readonly float[][] _previousRigOutputs;
    private readonly float[] _parameterCaches;
    private readonly float[] _parameterInputCaches;
    private readonly Vector2 _gravity;
    private readonly Vector2 _wind;
    private readonly float _fps;

    private float _currentRemainTime;
    private bool _cachesPrimed;

    private PhysicsSimulator(Physics3Json json, int parameterCount)
    {
        var metadata = json.Metadata ?? new Physics3Json.Meta();
        var forces = metadata.EffectiveForces;
        _gravity = forces is null
            ? new Vector2(0f, -1f)
            : new Vector2(forces.Gravity.X, forces.Gravity.Y);
        _wind = forces is null
            ? Vector2.Zero
            : new Vector2(forces.Wind.X, forces.Wind.Y);
        _fps = metadata.Fps;

        _settings = new PhysicsSubRig[json.Settings.Count];
        _inputs = new PhysicsInput[metadata.TotalInputCount];
        _outputs = new PhysicsOutput[metadata.TotalOutputCount];
        _particles = new PhysicsParticle[metadata.VertexCount];
        _currentRigOutputs = new float[json.Settings.Count][];
        _previousRigOutputs = new float[json.Settings.Count][];
        _parameterCaches = new float[parameterCount];
        _parameterInputCaches = new float[parameterCount];

        var inputIndex = 0;
        var outputIndex = 0;
        var particleIndex = 0;

        for (var i = 0; i < json.Settings.Count; i++)
        {
            var source = json.Settings[i];
            var setting = new PhysicsSubRig
            {
                NormalizationPosition = ToNormalization(source.Normalization?.Position),
                NormalizationAngle = ToNormalization(source.Normalization?.Angle),
                InputCount = source.Inputs.Count,
                BaseInputIndex = inputIndex,
                OutputCount = source.Outputs.Count,
                BaseOutputIndex = outputIndex,
                ParticleCount = source.Vertices.Count,
                BaseParticleIndex = particleIndex,
            };

            _settings[i] = setting;

            foreach (var input in source.Inputs)
            {
                _inputs[inputIndex++] = new PhysicsInput
                {
                    SourceId = input.Source?.Id ?? string.Empty,
                    Weight = input.Weight,
                    Reflect = input.Reflect,
                    Type = input.Type switch
                    {
                        "Y" => PhysicsSource.Y,
                        "Angle" => PhysicsSource.Angle,
                        _ => PhysicsSource.X,
                    },
                };
            }

            _currentRigOutputs[i] = new float[setting.OutputCount];
            _previousRigOutputs[i] = new float[setting.OutputCount];

            foreach (var output in source.Outputs)
            {
                _outputs[outputIndex++] = new PhysicsOutput
                {
                    DestinationId = output.Destination?.Id ?? string.Empty,
                    VertexIndex = output.VertexIndex,
                    AngleScale = output.Scale,
                    Weight = output.Weight,
                    Reflect = output.Reflect,
                    Type = output.Type switch
                    {
                        "X" => PhysicsSource.X,
                        "Y" => PhysicsSource.Y,
                        _ => PhysicsSource.Angle,
                    },
                };
            }

            foreach (var vertex in source.Vertices)
            {
                _particles[particleIndex++] = new PhysicsParticle
                {
                    InitialPosition = new Vector2(vertex.Position.X, vertex.Position.Y),
                    Position = new Vector2(vertex.Position.X, vertex.Position.Y),
                    LastPosition = new Vector2(vertex.Position.X, vertex.Position.Y),
                    Mobility = vertex.Mobility,
                    Delay = vertex.Delay,
                    Acceleration = vertex.Acceleration,
                    Radius = vertex.Radius,
                };
            }
        }

        Reset();
    }

    public int SubRigCount => _settings.Length;

    public static PhysicsSimulator Load(string path, CubismModel model)
    {
        var json = Physics3Json.Load(path);
        var simulator = new PhysicsSimulator(json, model.ParameterCount);

        Log.Info(
            $"physics: {simulator.SubRigCount} settings, {simulator._inputs.Length} inputs, " +
            $"{simulator._outputs.Length} outputs, {simulator._particles.Length} particles, " +
            $"{simulator._fps:F0} fps");

        return simulator;
    }

    /// <summary>Puts every particle back at its authored rest position.</summary>
    public void Reset()
    {
        // The authored rest layout is a straight vertical chain built from the
        // radii, not the positions in the file. LastGravity is the *opposite* of
        // the default gravity direction, which is what the original does with
        // its "assign then negate Y" pair.
        foreach (var setting in _settings)
        {
            var baseIndex = setting.BaseParticleIndex;
            _particles[baseIndex].InitialPosition = Vector2.Zero;

            for (var i = 0; i < setting.ParticleCount; i++)
            {
                var particle = _particles[baseIndex + i];

                if (i > 0)
                {
                    particle.InitialPosition = _particles[baseIndex + i - 1].InitialPosition
                                               + new Vector2(0f, particle.Radius);
                }

                particle.Position = particle.InitialPosition;
                particle.LastPosition = particle.InitialPosition;
                particle.LastGravity = new Vector2(0f, 1f);
                particle.Velocity = Vector2.Zero;
                particle.Force = Vector2.Zero;
            }
        }

        _currentRemainTime = 0f;
        _cachesPrimed = false;
        Array.Clear(_parameterCaches);
        Array.Clear(_parameterInputCaches);
    }

    public void Evaluate(CubismModel model, float deltaSeconds)
    {
        if (deltaSeconds <= 0f || _settings.Length == 0)
        {
            return;
        }

        _currentRemainTime += deltaSeconds;
        if (_currentRemainTime > MaxDeltaTime)
        {
            _currentRemainTime = 0f;
        }

        if (_parameterInputCaches.Length < model.ParameterCount)
        {
            return;
        }

        // The input cache has to start from the model's current values. Starting
        // from zero makes the first step read every input as zero, which throws
        // the strands far enough to pin the outputs at their limits.
        if (!_cachesPrimed)
        {
            for (var j = 0; j < model.ParameterCount; j++)
            {
                _parameterInputCaches[j] = model.GetParameterValue(j);
                _parameterCaches[j] = _parameterInputCaches[j];
            }

            _cachesPrimed = true;
        }

        // A fixed physics step decouples hair motion from the render rate: the
        // simulation runs at its authored 30 Hz no matter what the frame rate is.
        var physicsDeltaTime = _fps > 0f ? 1f / _fps : deltaSeconds;

        while (_currentRemainTime >= physicsDeltaTime)
        {
            for (var settingIndex = 0; settingIndex < _settings.Length; settingIndex++)
            {
                var count = _settings[settingIndex].OutputCount;
                Array.Copy(_currentRigOutputs[settingIndex], _previousRigOutputs[settingIndex], count);
            }

            // Blend the incoming parameter values over the step so a parameter
            // that jumps does not teleport the particles.
            var inputWeight = physicsDeltaTime / _currentRemainTime;
            for (var j = 0; j < model.ParameterCount; j++)
            {
                _parameterCaches[j] = _parameterInputCaches[j] * (1f - inputWeight)
                                      + model.GetParameterValue(j) * inputWeight;
                _parameterInputCaches[j] = _parameterCaches[j];
            }

            for (var settingIndex = 0; settingIndex < _settings.Length; settingIndex++)
            {
                var setting = _settings[settingIndex];
                var totalAngle = 0f;
                var totalTranslation = Vector2.Zero;

                for (var i = 0; i < setting.InputCount; i++)
                {
                    var input = _inputs[setting.BaseInputIndex + i];

                    if (input.SourceParameterIndex == -1)
                    {
                        input.SourceParameterIndex = model.GetParameterIndex(input.SourceId);
                    }

                    var index = input.SourceParameterIndex;
                    if (index < 0)
                    {
                        continue;
                    }

                    var weight = input.Weight / MaximumWeight;
                    var value = _parameterCaches[index];
                    var minimum = model.GetParameterMinimum(index);
                    var maximum = model.GetParameterMaximum(index);
                    var fallback = model.GetParameterDefault(index);

                    switch (input.Type)
                    {
                        case PhysicsSource.X:
                            totalTranslation.X += Normalize(
                                value, minimum, maximum, fallback,
                                setting.NormalizationPosition, input.Reflect) * weight;
                            break;

                        case PhysicsSource.Y:
                            totalTranslation.Y += Normalize(
                                value, minimum, maximum, fallback,
                                setting.NormalizationPosition, input.Reflect) * weight;
                            break;

                        default:
                            totalAngle += Normalize(
                                value, minimum, maximum, fallback,
                                setting.NormalizationAngle, input.Reflect) * weight;
                            break;
                    }
                }

                var radian = -totalAngle * MathF.PI / 180f;

                // Aliased rotation, copied from the original. The second line
                // reads the X written by the first.
                totalTranslation.X = totalTranslation.X * MathF.Cos(radian)
                                     - totalTranslation.Y * MathF.Sin(radian);
                totalTranslation.Y = totalTranslation.X * MathF.Sin(radian)
                                     + totalTranslation.Y * MathF.Cos(radian);

                UpdateParticles(
                    setting,
                    totalTranslation,
                    totalAngle,
                    MovementThreshold * setting.NormalizationPosition.Maximum,
                    physicsDeltaTime);

                for (var i = 0; i < setting.OutputCount; i++)
                {
                    var output = _outputs[setting.BaseOutputIndex + i];
                    var particleIndex = output.VertexIndex;

                    if (output.DestinationParameterIndex == -1)
                    {
                        output.DestinationParameterIndex = model.GetParameterIndex(output.DestinationId);
                    }

                    if (particleIndex < 1 || particleIndex >= setting.ParticleCount ||
                        output.DestinationParameterIndex < 0)
                    {
                        continue;
                    }

                    var current = _particles[setting.BaseParticleIndex + particleIndex];
                    var previous = _particles[setting.BaseParticleIndex + particleIndex - 1];
                    var translation = current.Position - previous.Position;

                    var outputValue = GetOutputValue(output, translation, setting, particleIndex);
                    _currentRigOutputs[settingIndex][i] = outputValue;

                    UpdateOutputParameterValue(
                        model,
                        output,
                        outputValue,
                        ref _parameterCaches[output.DestinationParameterIndex]);
                }
            }

            _currentRemainTime -= physicsDeltaTime;
        }

        Interpolate(model, _currentRemainTime / physicsDeltaTime);
    }

    /// <summary>
    /// Verlet-style integration followed by a distance constraint, one particle
    /// at a time down the chain.
    /// </summary>
    private void UpdateParticles(
        PhysicsSubRig setting,
        Vector2 totalTranslation,
        float totalAngle,
        float threshold,
        float deltaSeconds)
    {
        var baseIndex = setting.BaseParticleIndex;

        _particles[baseIndex].Position = totalTranslation;

        var radian = totalAngle * MathF.PI / 180f;
        var currentGravity = new Vector2(MathF.Sin(radian), MathF.Cos(radian));
        currentGravity = SafeNormalize(currentGravity);

        for (var i = 1; i < setting.ParticleCount; i++)
        {
            var particle = _particles[baseIndex + i];
            var parent = _particles[baseIndex + i - 1];

            particle.Force = currentGravity * particle.Acceleration + _wind;
            particle.LastPosition = particle.Position;

            var delay = particle.Delay * deltaSeconds * 30f;

            var direction = particle.Position - parent.Position;
            var airResistance = DirectionToRadian(particle.LastGravity, currentGravity) / AirResistance;

            // Aliased rotation, copied from the original.
            direction.X = MathF.Cos(airResistance) * direction.X - direction.Y * MathF.Sin(airResistance);
            direction.Y = MathF.Sin(airResistance) * direction.X + direction.Y * MathF.Cos(airResistance);

            particle.Position = parent.Position + direction;

            var velocity = particle.Velocity * delay;
            var force = particle.Force * delay * delay;
            particle.Position += velocity + force;

            var newDirection = SafeNormalize(particle.Position - parent.Position);
            particle.Position = parent.Position + newDirection * particle.Radius;

            if (MathF.Abs(particle.Position.X) < threshold)
            {
                particle.Position.X = 0f;
            }

            if (delay != 0f)
            {
                particle.Velocity = (particle.Position - particle.LastPosition) / delay;
                particle.Velocity *= particle.Mobility;
            }

            particle.Force = Vector2.Zero;
            particle.LastGravity = currentGravity;
        }
    }

    /// <summary>
    /// Returns the raw value read from the strand. Scaling happens later, in
    /// <see cref="UpdateOutputParameterValue"/>, because interpolation blends
    /// these raw values first.
    /// </summary>
    private float GetOutputValue(
        PhysicsOutput output,
        Vector2 translation,
        PhysicsSubRig setting,
        int particleIndex)
    {
        var value = output.Type switch
        {
            PhysicsSource.X => translation.X,
            PhysicsSource.Y => translation.Y,
            _ => GetOutputAngle(output, translation, setting, particleIndex),
        };

        if (output.Reflect)
        {
            value = -value;
        }

        return value;
    }

    private float GetOutputAngle(
        PhysicsOutput output,
        Vector2 translation,
        PhysicsSubRig setting,
        int particleIndex)
    {
        Vector2 parentGravity;

        if (particleIndex >= 2)
        {
            var baseIndex = setting.BaseParticleIndex;
            parentGravity = _particles[baseIndex + particleIndex - 1].Position
                            - _particles[baseIndex + particleIndex - 2].Position;
        }
        else
        {
            parentGravity = -_gravity;
        }

        return DirectionToRadian(parentGravity, translation);
    }

    private void UpdateOutputParameterValue(
        CubismModel model,
        PhysicsOutput output,
        float value,
        ref float parameterCache)
    {
        var index = output.DestinationParameterIndex;
        var minimum = model.GetParameterMinimum(index);
        var maximum = model.GetParameterMaximum(index);

        // Translation outputs carry no scale in this format version: the file
        // stores a single number and the SDK reads it as the angle scale only,
        // leaving the translation scale at zero. Every output in the sample
        // model is an angle output, so this only matters for other models.
        var scale = output.Type == PhysicsSource.Angle ? output.AngleScale : 0f;
        value *= scale;

        if (value < minimum)
        {
            if (value < output.ValueBelowMinimum)
            {
                output.ValueBelowMinimum = value;
            }

            value = minimum;
        }
        else if (value > maximum)
        {
            if (value > output.ValueExceededMaximum)
            {
                output.ValueExceededMaximum = value;
            }

            value = maximum;
        }

        var weight = output.Weight / MaximumWeight;

        parameterCache = weight >= 1f
            ? value
            : parameterCache * (1f - weight) + value * weight;
    }

    /// <summary>Blends the physics result into the model with the leftover time.</summary>
    private void Interpolate(CubismModel model, float weight)
    {
        for (var settingIndex = 0; settingIndex < _settings.Length; settingIndex++)
        {
            var setting = _settings[settingIndex];

            for (var i = 0; i < setting.OutputCount; i++)
            {
                var output = _outputs[setting.BaseOutputIndex + i];
                var index = output.DestinationParameterIndex;

                if (index < 0)
                {
                    continue;
                }

                var previous = _previousRigOutputs[settingIndex][i];
                var current = _currentRigOutputs[settingIndex][i];

                var scale = output.Type == PhysicsSource.Angle ? output.AngleScale : 0f;
                var value = (previous * (1f - weight) + current * weight) * scale;
                value = Math.Clamp(value, model.GetParameterMinimum(index), model.GetParameterMaximum(index));

                var outputWeight = output.Weight / MaximumWeight;
                var existing = model.GetParameterValue(index);

                model.SetParameterValue(
                    index,
                    outputWeight >= 1f
                        ? value
                        : existing * (1f - outputWeight) + value * outputWeight);
            }
        }
    }

    /// <summary>Maps a parameter value onto the range the setting normalises to.</summary>
    private static float Normalize(
        float value,
        float parameterMinimum,
        float parameterMaximum,
        float parameterDefault,
        PhysicsNormalization normalization,
        bool inverted)
    {
        _ = parameterDefault;

        var maxValue = MathF.Max(parameterMaximum, parameterMinimum);
        if (maxValue < value)
        {
            value = maxValue;
        }

        var minValue = MathF.Min(parameterMaximum, parameterMinimum);
        if (minValue > value)
        {
            value = minValue;
        }

        var minNorm = MathF.Min(normalization.Minimum, normalization.Maximum);
        var maxNorm = MathF.Max(normalization.Minimum, normalization.Maximum);
        var middleNorm = normalization.Default;

        var middleValue = minValue + (maxValue - minValue) / 2f;
        var parameterValue = value - middleValue;

        var result = 0f;

        if (parameterValue > 0f)
        {
            var length = maxNorm - middleNorm;
            var span = maxValue - middleValue;
            if (span != 0f)
            {
                result = parameterValue * (length / span) + middleNorm;
            }
        }
        else if (parameterValue < 0f)
        {
            var length = minNorm - middleNorm;
            var span = minValue - middleValue;
            if (span != 0f)
            {
                result = parameterValue * (length / span) + middleNorm;
            }
        }
        else
        {
            result = middleNorm;
        }

        // The original negates for the non-inverted case.
        return inverted ? result : -result;
    }

    private static float DirectionToRadian(Vector2 from, Vector2 to)
    {
        var result = MathF.Atan2(to.Y, to.X) - MathF.Atan2(from.Y, from.X);

        while (result < -MathF.PI)
        {
            result += MathF.PI * 2f;
        }

        while (result > MathF.PI)
        {
            result -= MathF.PI * 2f;
        }

        return result;
    }

    private static Vector2 SafeNormalize(Vector2 value)
    {
        var length = value.Length();
        return length > 0f ? value / length : Vector2.Zero;
    }

    private static PhysicsNormalization ToNormalization(Physics3Json.NormalizationRange? range) =>
        range is null
            ? default
            : new PhysicsNormalization
            {
                Minimum = range.Minimum,
                Maximum = range.Maximum,
                Default = range.Default,
            };
}
