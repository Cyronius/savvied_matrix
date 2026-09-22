namespace SavviedMatrix.Rain;

/// <summary>
/// One column's falling stream. Fixed when a rain phase begins, which is what lets the
/// audio be scheduled up front: given these numbers, every future position of every
/// stream is known without running a single frame.
/// </summary>
public readonly record struct StreamParams(double DelaySeconds, double RowsPerSecond, int TrailLength)
{
    /// <summary>Head position in rows at phase time t. Negative means it has not entered yet.</summary>
    public double HeadAt(double t) => (t - DelaySeconds) * RowsPerSecond;

    /// <summary>Phase time at which the head reaches the given row.</summary>
    public double TimeAtRow(double row) => DelaySeconds + row / RowsPerSecond;

    /// <summary>Phase time at which this column is fully settled (trail has cleared the bottom).</summary>
    public double SettleTime(int rows) => TimeAtRow(rows - 1 + TrailLength);
}

public static class StreamParamsFactory
{
    public const double MaxDelayFraction = 0.35;
    public const int MinTrail = 6;
    public const int MaxTrail = 14;
    public const double MinJitter = 0.85;
    public const double MaxJitter = 1.15;

    /// <summary>
    /// Builds one stream per column. Every column is tuned to finish at roughly
    /// <paramref name="seconds"/>, with jitter so they do not land in lockstep.
    /// </summary>
    public static StreamParams[] Create(int cols, int rows, double seconds, Random rng)
    {
        if (cols <= 0) throw new ArgumentOutOfRangeException(nameof(cols));
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
        if (seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));

        var result = new StreamParams[cols];

        for (int c = 0; c < cols; c++)
        {
            double delay = rng.NextDouble() * MaxDelayFraction * seconds;
            int trail = rng.Next(MinTrail, MaxTrail + 1);
            double jitter = MinJitter + rng.NextDouble() * (MaxJitter - MinJitter);

            // Travel the full screen plus the trail within the time left after the delay.
            double remaining = Math.Max(seconds - delay, seconds * 0.25);
            double speed = (rows + trail) / remaining * jitter;

            result[c] = new StreamParams(delay, speed, trail);
        }

        return result;
    }

    /// <summary>The time by which every column in the set has settled.</summary>
    public static double TotalSettleTime(StreamParams[] streams, int rows)
    {
        double max = 0;
        foreach (var s in streams)
            max = Math.Max(max, s.SettleTime(rows));
        return max;
    }
}
