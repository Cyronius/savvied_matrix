// Traces: MATRIX-SOUND-NOTE
using SavviedMatrix.Audio;
using SavviedMatrix.Rain;

namespace SavviedMatrix.Tests;

/// <summary>
/// The colour-to-note mapping is pure and deterministic, so it is asserted against exact
/// expected values: a specific MIDI pitch, a specific envelope time, a specific waveform.
/// </summary>
public class NoteMapperTests
{
    private const string Major = "major";

    /// <summary>Non-greyscale, mid-luminance band: octave 4, so degree 0 is middle C (MIDI 60).</summary>
    private static BandStats Band(float hue, float saturation = 0.6f, float luminance = 0.5f)
        => new(hue, saturation, luminance, Coverage: 1f);

    private static StreamParams Stream(double rowsPerSecond = 30, int trail = 10)
        => new(DelaySeconds: 0, RowsPerSecond: rowsPerSecond, TrailLength: trail);

    private static void AssertMidi(int expectedMidi, Note note)
        => Assert.Equal((double)Note.FrequencyForMidi(expectedMidi), note.Frequency, 3);

    [Fact]
    public void RootAtOctaveFourIsMiddleC()
    {
        var note = NoteMapper.Map(Band(hue: 0f), Stream(), RainPhaseKind.RainIn, Major);

        // Absolute anchor, so the MIDI-based assertions below are not circular.
        Assert.Equal(261.626, note.Frequency, 2);
    }

    [Fact]
    public void IndigoAtOctaveFourIsConcertA()
    {
        var note = NoteMapper.Map(Band(hue: 270f), Stream(), RainPhaseKind.RainIn, Major);

        Assert.Equal(440.0, note.Frequency, 2);
    }

    [Theory]
    // red wraps around 0 degrees: [345, 360) and [0, 15)
    [InlineData(0f, 0)]
    [InlineData(5f, 0)]
    [InlineData(14.9f, 0)]
    [InlineData(345f, 0)]
    [InlineData(359.9f, 0)]
    // orange [15, 45)
    [InlineData(15f, 2)]
    [InlineData(30f, 2)]
    [InlineData(44.9f, 2)]
    // yellow [45, 70)
    [InlineData(45f, 4)]
    [InlineData(69.9f, 4)]
    // green [70, 170)
    [InlineData(70f, 5)]
    [InlineData(120f, 5)]
    [InlineData(169.9f, 5)]
    // blue [170, 255)
    [InlineData(170f, 7)]
    [InlineData(254.9f, 7)]
    // indigo [255, 285)
    [InlineData(255f, 9)]
    [InlineData(284.9f, 9)]
    // violet [285, 345)
    [InlineData(285f, 11)]
    [InlineData(344.9f, 11)]
    public void HueBucketPicksMajorScaleDegree(float hue, int expectedSemitone)
    {
        var note = NoteMapper.Map(Band(hue), Stream(), RainPhaseKind.RainIn, Major);

        AssertMidi(60 + expectedSemitone, note);
    }

    [Fact]
    public void RedWrapsAcrossZeroToTheSameDegree()
    {
        var below = NoteMapper.Map(Band(hue: 350f), Stream(), RainPhaseKind.RainIn, Major);
        var above = NoteMapper.Map(Band(hue: 10f), Stream(), RainPhaseKind.RainIn, Major);

        Assert.Equal(below.Frequency, above.Frequency);
        AssertMidi(60, below);
    }

    [Fact]
    public void GreyscaleBandForcesTheRootWhateverTheHue()
    {
        // Hue 200 would be blue (degree 4, semitone 7) if the saturation were trusted.
        var note = NoteMapper.Map(Band(hue: 200f, saturation: 0.1f), Stream(), RainPhaseKind.RainIn, Major);

        AssertMidi(60, note);
    }

    [Theory]
    [InlineData(0.0f, 48)]    // octave 3 -> 12 * (3 + 1)
    [InlineData(0.29f, 48)]
    [InlineData(0.3f, 60)]    // octave 4 -> middle C
    [InlineData(0.69f, 60)]
    [InlineData(0.7f, 72)]    // octave 5
    [InlineData(1.0f, 72)]
    public void LuminancePicksTheOctave(float luminance, int expectedMidi)
    {
        var note = NoteMapper.Map(Band(hue: 0f, luminance: luminance), Stream(), RainPhaseKind.RainIn, Major);

        AssertMidi(expectedMidi, note);
    }

    [Theory]
    [InlineData(0.0f, Waveform.Sine)]
    [InlineData(0.24f, Waveform.Sine)]
    [InlineData(0.25f, Waveform.Triangle)]
    [InlineData(0.49f, Waveform.Triangle)]
    [InlineData(0.5f, Waveform.Square)]
    [InlineData(0.74f, Waveform.Square)]
    [InlineData(0.75f, Waveform.Saw)]
    [InlineData(1.0f, Waveform.Saw)]
    public void SaturationPicksTheWaveform(float saturation, Waveform expected)
    {
        var note = NoteMapper.Map(Band(hue: 0f, saturation: saturation), Stream(), RainPhaseKind.RainIn, Major);

        Assert.Equal(expected, note.Wave);
    }

    [Theory]
    [InlineData(5.0, 15.0)]     // slow end
    [InlineData(60.0, 2.0)]     // fast end
    [InlineData(32.5, 8.5)]     // midpoint
    [InlineData(1.0, 15.0)]     // clamped below
    [InlineData(500.0, 2.0)]    // clamped above
    public void AttackTracksStreamSpeed(double rowsPerSecond, double expectedMs)
    {
        var note = NoteMapper.Map(Band(hue: 0f), Stream(rowsPerSecond), RainPhaseKind.RainIn, Major);

        Assert.Equal(expectedMs, note.AttackMs, 3);
    }

    [Theory]
    [InlineData(6, 90.0)]       // short trail
    [InlineData(14, 450.0)]     // long trail
    [InlineData(10, 270.0)]     // midpoint
    [InlineData(1, 90.0)]       // clamped below
    [InlineData(40, 450.0)]     // clamped above
    public void DecayTracksTrailLength(int trail, double expectedMs)
    {
        var note = NoteMapper.Map(Band(hue: 0f), Stream(trail: trail), RainPhaseKind.RainIn, Major);

        Assert.Equal(expectedMs, note.DecayMs, 3);
    }

    [Fact]
    public void RainOutDropsAnOctave()
    {
        var band = Band(hue: 170f); // blue -> semitone 7
        var inNote = NoteMapper.Map(band, Stream(), RainPhaseKind.RainIn, Major);
        var outNote = NoteMapper.Map(band, Stream(), RainPhaseKind.RainOut, Major);

        AssertMidi(67, inNote);
        AssertMidi(55, outNote);
        Assert.Equal((double)(inNote.Frequency / 2f), outNote.Frequency, 3);
    }

    [Fact]
    public void RainOutShortensDecayAndSoftensGain()
    {
        var band = Band(hue: 0f, luminance: 0.5f);
        var inNote = NoteMapper.Map(band, Stream(trail: 10), RainPhaseKind.RainIn, Major);
        var outNote = NoteMapper.Map(band, Stream(trail: 10), RainPhaseKind.RainOut, Major);

        Assert.Equal(270.0, inNote.DecayMs, 3);
        Assert.Equal(162.0, outNote.DecayMs, 3);   // 270 * 0.6

        Assert.Equal(0.625, inNote.Gain, 3);       // 0.25 + 0.75 * 0.5
        Assert.Equal(0.5, outNote.Gain, 3);        // 0.625 * 0.8
    }

    [Theory]
    [InlineData(0.0f, 0.25)]
    [InlineData(0.5f, 0.625)]
    [InlineData(1.0f, 1.0)]
    public void GainTracksLuminance(float luminance, double expectedGain)
    {
        var note = NoteMapper.Map(Band(hue: 0f, luminance: luminance), Stream(), RainPhaseKind.RainIn, Major);

        Assert.Equal(expectedGain, note.Gain, 3);
    }

    [Fact]
    public void GlideDependsOnThePhase()
    {
        var band = Band(hue: 0f);

        var rainIn = NoteMapper.Map(band, Stream(trail: 10), RainPhaseKind.RainIn, Major);
        Assert.Equal(1.0, rainIn.GlideSemitones, 3);
        Assert.Equal(25.0, rainIn.GlideMs, 3);

        var rainOut = NoteMapper.Map(band, Stream(trail: 10), RainPhaseKind.RainOut, Major);
        Assert.Equal(-3.0, rainOut.GlideSemitones, 3);
        Assert.Equal((double)rainOut.DecayMs, rainOut.GlideMs, 3);
        Assert.Equal(162.0, rainOut.GlideMs, 3);

        var strum = NoteMapper.Map(band, Stream(trail: 10), RainPhaseKind.StaticStrum, Major);
        Assert.Equal(0.5, strum.GlideSemitones, 3);
        Assert.Equal(15.0, strum.GlideMs, 3);
    }

    [Theory]
    [InlineData(0.1f, 20000.0)]   // sine, open
    [InlineData(0.3f, 20000.0)]   // triangle, open
    [InlineData(0.6f, 6000.0)]    // square, shaved
    [InlineData(0.9f, 6000.0)]    // saw, shaved
    public void BrightWaveformsGetALowPass(float saturation, double expectedCutoff)
    {
        var note = NoteMapper.Map(Band(hue: 120f, saturation: saturation), Stream(), RainPhaseKind.RainIn, Major);

        Assert.Equal(expectedCutoff, note.FilterCutoffHz, 3);
    }

    [Fact]
    public void PentatonicScaleUsesItsOwnOffsets()
    {
        // Violet is degree 6: major -> 11 semitones, pentatonic -> 14.
        var band = Band(hue: 300f);

        AssertMidi(71, NoteMapper.Map(band, Stream(), RainPhaseKind.RainIn, Major));
        AssertMidi(74, NoteMapper.Map(band, Stream(), RainPhaseKind.RainIn, "pentatonic"));
    }

    [Fact]
    public void UnknownScaleFallsBackToMajor()
    {
        var band = Band(hue: 300f);

        var fallback = NoteMapper.Map(band, Stream(), RainPhaseKind.RainIn, "lydian-dominant");

        AssertMidi(71, fallback);
    }
}
