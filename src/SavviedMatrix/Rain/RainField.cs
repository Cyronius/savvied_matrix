namespace SavviedMatrix.Rain;

public enum CellPhase
{
    /// <summary>The stream has not reached this row yet.</summary>
    Hidden,

    /// <summary>The bright leading character of the stream.</summary>
    Head,

    /// <summary>Behind the head, fading. Distance from the head is carried separately.</summary>
    Trail,

    /// <summary>The stream has passed; this cell has taken its final value.</summary>
    Settled
}

public readonly record struct CellState(CellPhase Phase, int TrailDistance)
{
    public static readonly CellState Hidden = new(CellPhase.Hidden, 0);
    public static readonly CellState Settled = new(CellPhase.Settled, 0);
    public static readonly CellState Head = new(CellPhase.Head, 0);
}

/// <summary>
/// Pure evaluation of a rain phase: given the streams and a time, what is every cell doing.
/// No state, no frame counter, so a dropped frame costs nothing but a skipped picture.
/// </summary>
public static class RainField
{
    /// <summary>State of one cell at phase time t.</summary>
    public static CellState StateAt(in StreamParams stream, int row, double t)
    {
        double head = stream.HeadAt(t);

        if (head < 0) return CellState.Hidden;

        int headRow = (int)Math.Floor(head);

        if (row > headRow) return CellState.Hidden;
        if (row == headRow) return CellState.Head;

        int distance = headRow - row;
        if (distance < stream.TrailLength)
            return new CellState(CellPhase.Trail, distance);

        return CellState.Settled;
    }

    /// <summary>
    /// Fills <paramref name="into"/> (length cols*rows, row-major) with the state of every cell.
    /// </summary>
    public static void Evaluate(StreamParams[] streams, int cols, int rows, double t, CellState[] into)
    {
        if (into.Length < cols * rows)
            throw new ArgumentException("Destination buffer is too small.", nameof(into));

        for (int c = 0; c < cols; c++)
        {
            ref readonly var stream = ref streams[c];
            double head = stream.HeadAt(t);
            int headRow = (int)Math.Floor(head);
            bool entered = head >= 0;

            for (int r = 0; r < rows; r++)
            {
                int i = r * cols + c;

                if (!entered || r > headRow)
                {
                    into[i] = CellState.Hidden;
                }
                else if (r == headRow)
                {
                    into[i] = CellState.Head;
                }
                else
                {
                    int distance = headRow - r;
                    into[i] = distance < stream.TrailLength
                        ? new CellState(CellPhase.Trail, distance)
                        : CellState.Settled;
                }
            }
        }
    }

    /// <summary>True when every column has fully settled at time t.</summary>
    public static bool IsComplete(StreamParams[] streams, int rows, double t)
    {
        foreach (var s in streams)
            if (t < s.SettleTime(rows)) return false;
        return true;
    }
}
