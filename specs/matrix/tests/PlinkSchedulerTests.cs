// Traces: MATRIX-SOUND-SCHEDULE
using SavviedMatrix.Audio;
using SavviedMatrix.Rain;

namespace SavviedMatrix.Tests;

/// <summary>
/// The schedule is computed before a phase runs, so these assertions pin the exact instants:
/// if a plink drifts off the moment its stream hits the bottom row, the whole illusion goes.
/// </summary>
public class PlinkSchedulerTests
{
    private const string Major = "major";
    private const int ScreenCols = 8;
    private const int Rows = 10;

    /// <summary>Four bands over eight columns, so the band centres are columns 1, 3, 5 and 7.</summary>
    private static BandStats[] FourBands() => new[]
    {
        new BandStats(0f, 0.6f, 0.5f, 1f),      // red
        new BandStats(30f, 0.6f, 0.5f, 1f),     // orange
        new BandStats(120f, 0.6f, 0.5f, 1f),    // green
        new BandStats(270f, 0.6f, 0.5f, 1f)     // indigo
    };

    /// <summary>One stream per column, each with a distinct delay so centres are identifiable.</summary>
    private static StreamParams[] Streams(bool descendingDelay = false)
    {
        var streams = new StreamParams[ScreenCols];
        for (int c = 0; c < ScreenCols; c++)
        {
            double delay = descendingDelay ? (ScreenCols - c) * 0.25 : c * 0.25;
            streams[c] = new StreamParams(delay, 10 + c, 6 + c % 5);
        }
        return streams;
    }

    [Fact]
    public void OneEventPerBand()
    {
        var notes = PlinkScheduler.ForRainPhase(Streams(), FourBands(), ScreenCols, Rows, RainPhaseKind.RainIn, Major);

        Assert.Equal(4, notes.Length);
    }

    [Fact]
    public void RestBandsAreSilent()
    {
        var bands = FourBands();
        bands[1] = bands[1] with { Coverage = 0.05f };  // below RestCoverageThreshold
        bands[2] = bands[2] with { Coverage = 0f };

        var streams = Streams();
        var notes = PlinkScheduler.ForRainPhase(streams, bands, ScreenCols, Rows, RainPhaseKind.RainIn, Major);

        Assert.Equal(2, notes.Length);
        Assert.Equal(streams[1].TimeAtRow(Rows - 1), notes[0].Seconds, 9);
        Assert.Equal(streams[7].TimeAtRow(Rows - 1), notes[1].Seconds, 9);
    }

    [Fact]
    public void AllRestsGiveAnEmptyArrayNotNull()
    {
        var bands = FourBands();
        for (int i = 0; i < bands.Length; i++) bands[i] = bands[i] with { Coverage = 0f };

        var notes = PlinkScheduler.ForRainPhase(Streams(), bands, ScreenCols, Rows, RainPhaseKind.RainIn, Major);

        Assert.NotNull(notes);
        Assert.Empty(notes);
    }

    [Fact]
    public void EachBandFiresWhenItsCentreColumnReachesTheBottomRow()
    {
        var streams = Streams();
        var notes = PlinkScheduler.ForRainPhase(streams, FourBands(), ScreenCols, Rows, RainPhaseKind.RainIn, Major);

        int[] centres = { 1, 3, 5, 7 };
        for (int i = 0; i < centres.Length; i++)
        {
            double expected = streams[centres[i]].DelaySeconds + (Rows - 1) / streams[centres[i]].RowsPerSecond;
            Assert.Equal(expected, notes[i].Seconds, 9);
            Assert.Equal(streams[centres[i]].TimeAtRow(Rows - 1), notes[i].Seconds, 9);
        }

        // Concretely: column 1 is delayed 0.25 s and falls at 11 rows/s.
        Assert.Equal(0.25 + 9.0 / 11.0, notes[0].Seconds, 9);
    }

    [Fact]
    public void OutputIsSortedByTime()
    {
        // Delays now fall as the column index rises, so band order is not time order.
        var streams = Streams(descendingDelay: true);
        var notes = PlinkScheduler.ForRainPhase(streams, FourBands(), ScreenCols, Rows, RainPhaseKind.RainIn, Major);

        Assert.Equal(4, notes.Length);
        for (int i = 1; i < notes.Length; i++)
            Assert.True(notes[i - 1].Seconds <= notes[i].Seconds, "schedule is not ascending");

        // The rightmost band (centre column 7) has the smallest delay, so it comes first.
        Assert.Equal(streams[7].TimeAtRow(Rows - 1), notes[0].Seconds, 9);
        Assert.Equal(streams[1].TimeAtRow(Rows - 1), notes[3].Seconds, 9);
    }

    [Fact]
    public void BandColourDrivesTheNote()
    {
        var notes = PlinkScheduler.ForRainPhase(Streams(), FourBands(), ScreenCols, Rows, RainPhaseKind.RainIn, Major);

        // Band 3 is indigo at mid luminance: degree 5, semitone 9, octave 4 -> MIDI 69.
        Assert.Equal((double)Note.FrequencyForMidi(69), notes[3].Note.Frequency, 3);
    }

    [Fact]
    public void EmptyInputGivesAnEmptyArray()
    {
        Assert.Empty(PlinkScheduler.ForRainPhase(
            Array.Empty<StreamParams>(), FourBands(), ScreenCols, Rows, RainPhaseKind.RainIn, Major));

        Assert.Empty(PlinkScheduler.ForRainPhase(
            Streams(), Array.Empty<BandStats>(), ScreenCols, Rows, RainPhaseKind.RainIn, Major));

        Assert.Empty(PlinkScheduler.ForStaticStrum(Array.Empty<BandStats>(), 2.0, Major));
    }

    [Fact]
    public void StaticStrumSpacesNotesEvenlyAcrossTheWindow()
    {
        var notes = PlinkScheduler.ForStaticStrum(FourBands(), totalSeconds: 2.0, Major);

        Assert.Equal(4, notes.Length);
        Assert.Equal(0.0, notes[0].Seconds, 9);
        Assert.Equal(0.5, notes[1].Seconds, 9);
        Assert.Equal(1.0, notes[2].Seconds, 9);
        Assert.Equal(1.5, notes[3].Seconds, 9);
    }

    [Fact]
    public void StaticStrumKeepsTheSlotOfASilencedBand()
    {
        var bands = FourBands();
        bands[1] = bands[1] with { Coverage = 0f };

        var notes = PlinkScheduler.ForStaticStrum(bands, totalSeconds: 2.0, Major);

        // A rest leaves a gap rather than closing it up: the picture's silence is audible.
        Assert.Equal(3, notes.Length);
        Assert.Equal(0.0, notes[0].Seconds, 9);
        Assert.Equal(1.0, notes[1].Seconds, 9);
        Assert.Equal(1.5, notes[2].Seconds, 9);
    }

    [Fact]
    public void StaticStrumUsesTheStrumGesture()
    {
        var notes = PlinkScheduler.ForStaticStrum(FourBands(), totalSeconds: 2.0, Major);

        Assert.Equal(0.5, notes[0].Note.GlideSemitones, 3);
        Assert.Equal(15.0, notes[0].Note.GlideMs, 3);
        // The default strum shape is (0 s delay, 30 rows/s, trail 10): attack 9.091 ms, decay 270 ms.
        Assert.Equal(9.091, notes[0].Note.AttackMs, 3);
        Assert.Equal(270.0, notes[0].Note.DecayMs, 3);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 3)]
    [InlineData(2, 5)]
    [InlineData(3, 7)]
    public void CentreColumnIsTheMiddleOfTheHalfOpenRange(int band, int expectedColumn)
    {
        Assert.Equal(expectedColumn, PlinkScheduler.CentreColumn(band, bands: 4, screenCols: ScreenCols));
    }
}
