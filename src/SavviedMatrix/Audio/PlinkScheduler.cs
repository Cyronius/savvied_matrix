using SavviedMatrix.Rain;

namespace SavviedMatrix.Audio;

/// <summary>
/// Builds the complete list of notes for a phase before the phase starts.
/// <para>
/// The rain is deterministic once its <see cref="StreamParams"/> are fixed, so every plink's
/// moment is known in advance. Scheduling up front - rather than firing a note when a frame
/// happens to notice a head hitting the bottom - means the music stays in time even when the
/// UI drops frames, and it lets the audio thread place notes on an exact sample boundary.
/// </para>
/// </summary>
public static class PlinkScheduler
{
    /// <summary>Shape used for the static strum, which has no real falling stream behind it.</summary>
    private static readonly StreamParams StrumShape = new(DelaySeconds: 0, RowsPerSecond: 30, TrailLength: 10);

    private static readonly ScheduledNote[] Empty = Array.Empty<ScheduledNote>();

    /// <summary>
    /// One note per non-rest band, fired at the moment that band's centre column reaches
    /// the bottom row of the screen.
    /// </summary>
    /// <param name="streams">One stream per screen column.</param>
    /// <param name="bands">Colour analysis, one entry per vertical band.</param>
    /// <param name="screenCols">Total character columns across the screen.</param>
    /// <param name="rows">Character rows down the screen; the bottom row is <c>rows - 1</c>.</param>
    public static ScheduledNote[] ForRainPhase(
        StreamParams[] streams,
        BandStats[] bands,
        int screenCols,
        int rows,
        RainPhaseKind phase,
        string scaleName)
    {
        if (streams is null || streams.Length == 0) return Empty;
        if (bands is null || bands.Length == 0) return Empty;
        if (screenCols <= 0 || rows <= 0) return Empty;

        var result = new List<ScheduledNote>(bands.Length);

        for (int b = 0; b < bands.Length; b++)
        {
            var band = bands[b];
            if (band.IsRest) continue;

            int centreCol = CentreColumn(b, bands.Length, screenCols);
            if (centreCol >= streams.Length) centreCol = streams.Length - 1;

            ref readonly var stream = ref streams[centreCol];
            double seconds = stream.TimeAtRow(rows - 1);

            result.Add(new ScheduledNote(seconds, NoteMapper.Map(band, stream, phase, scaleName)));
        }

        if (result.Count == 0) return Empty;

        // OrderBy is stable, so bands that land on the same instant keep left-to-right order.
        return result.OrderBy(static n => n.Seconds).ToArray();
    }

    /// <summary>
    /// The arpeggio played across a settled picture: the non-rest bands sound left to right,
    /// evenly spaced across <paramref name="totalSeconds"/>. Rests leave audible gaps, which is
    /// the point - the silence is part of the picture.
    /// </summary>
    public static ScheduledNote[] ForStaticStrum(BandStats[] bands, double totalSeconds, string scaleName)
    {
        if (bands is null || bands.Length == 0) return Empty;
        if (double.IsNaN(totalSeconds) || totalSeconds <= 0) return Empty;

        var result = new List<ScheduledNote>(bands.Length);

        for (int i = 0; i < bands.Length; i++)
        {
            var band = bands[i];
            if (band.IsRest) continue;

            double seconds = i * totalSeconds / bands.Length;
            result.Add(new ScheduledNote(
                seconds,
                NoteMapper.Map(band, StrumShape, RainPhaseKind.StaticStrum, scaleName)));
        }

        if (result.Count == 0) return Empty;

        return result.OrderBy(static n => n.Seconds).ToArray();
    }

    /// <summary>
    /// Centre of band <paramref name="band"/>'s half-open column range
    /// <c>[band*cols/bands, (band+1)*cols/bands)</c>, matching how
    /// <see cref="BandAnalyzer"/> slices the screen.
    /// </summary>
    public static int CentreColumn(int band, int bands, int screenCols)
    {
        int startCol = (int)((long)band * screenCols / bands);
        int endCol = (int)((long)(band + 1) * screenCols / bands);
        if (endCol <= startCol) endCol = startCol + 1;

        int centre = startCol + (endCol - startCol) / 2;
        return Math.Clamp(centre, 0, screenCols - 1);
    }
}
