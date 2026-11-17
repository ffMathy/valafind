namespace Valafind.Core.Indexing;

/// <summary>Gains and limits of a <see cref="PidController"/>.</summary>
/// <param name="Kp">Proportional gain (output units per unit of error).</param>
/// <param name="Ki">Integral gain (output units per unit of error per second).</param>
/// <param name="Kd">Derivative gain (output units per unit of error-rate × seconds). Applied to the measurement, not
/// the error, so a setpoint change does not kick the output.</param>
/// <param name="OutputMin">Lowest output.</param>
/// <param name="OutputMax">Highest output.</param>
/// <param name="DerivativeSmoothing">Low-pass factor for the derivative term in (0, 1]: 1 = raw, smaller = smoother.
/// CPU readings are noisy; an unfiltered derivative would make the output jitter.</param>
public sealed record PidGains(double Kp, double Ki, double Kd, double OutputMin, double OutputMax, double DerivativeSmoothing = 0.3);

/// <summary>
/// Discrete PID controller in positional form with anti-windup: the integral only accumulates while the output is
/// not saturated in the direction of the error (conditional integration) and is always kept inside the output range,
/// so a long stretch at a limit (no work to do, or the hardware cannot go faster) does not leave a backlog that
/// overshoots later. Not thread-safe; the caller serialises <see cref="Update"/>.
/// </summary>
public sealed class PidController
{
    private readonly PidGains _gains;
    private double _integral;
    private double? _previousMeasurement;
    private double _derivative;

    /// <summary>Creates a controller whose output starts at <paramref name="initialOutput"/> (bumpless start).</summary>
    public PidController(PidGains gains, double initialOutput)
    {
        ArgumentNullException.ThrowIfNull(gains);
        if (gains.OutputMax < gains.OutputMin)
            throw new ArgumentException("OutputMax must be >= OutputMin.", nameof(gains));
        if (gains.DerivativeSmoothing is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(gains), "DerivativeSmoothing must be in (0, 1].");
        _gains = gains;
        _integral = Math.Clamp(initialOutput, gains.OutputMin, gains.OutputMax);
        Output = _integral;
    }

    /// <summary>The gains in use.</summary>
    public PidGains Gains => _gains;

    /// <summary>The last output (or the initial one before the first update).</summary>
    public double Output { get; private set; }

    /// <summary>Feeds one measurement taken <paramref name="dt"/> after the previous one and returns the new output.</summary>
    /// <param name="setpoint">Where the measurement should be.</param>
    /// <param name="measurement">Where it is.</param>
    /// <param name="dt">Time since the previous update. Non-positive values only recompute the output.</param>
    /// <param name="allowIncrease">False when more output would not be used (e.g. there is less work than the output
    /// already allows): the output may fall but not rise, and the integral does not grow.</param>
    public double Update(double setpoint, double measurement, TimeSpan dt, bool allowIncrease = true)
    {
        var previousOutput = Output;
        var seconds = dt.TotalSeconds;
        var error = setpoint - measurement;

        if (seconds > 0)
        {
            if (_previousMeasurement is { } previous)
            {
                var raw = -(measurement - previous) / seconds;
                _derivative += _gains.DerivativeSmoothing * (raw - _derivative);
            }
            _previousMeasurement = measurement;

            // Conditional integration: do not push further into a limit the output already sits at.
            var pushingUp = error > 0 && (Output >= _gains.OutputMax || !allowIncrease);
            var pushingDown = error < 0 && Output <= _gains.OutputMin;
            if (!pushingUp && !pushingDown)
                _integral += _gains.Ki * error * seconds;
        }

        var p = _gains.Kp * error;
        var d = _gains.Kd * _derivative;
        // The integral never holds more than the output range can use.
        _integral = Math.Clamp(_integral, _gains.OutputMin, _gains.OutputMax);
        var output = Math.Clamp(p + _integral + d, _gains.OutputMin, _gains.OutputMax);
        if (!allowIncrease && output > previousOutput)
            output = previousOutput;
        Output = output;
        return Output;
    }

    /// <summary>Forgets history (derivative and previous measurement) and restarts from <paramref name="output"/>.</summary>
    public void Reset(double output)
    {
        _integral = Math.Clamp(output, _gains.OutputMin, _gains.OutputMax);
        _previousMeasurement = null;
        _derivative = 0;
        Output = _integral;
    }

    /// <summary>Forgets the previous measurement, so the next update does not see the gap as a jump (used after a pause).</summary>
    public void ForgetMeasurement()
    {
        _previousMeasurement = null;
        _derivative = 0;
    }
}
