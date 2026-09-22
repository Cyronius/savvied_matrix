using SavviedMatrix.Rain;

namespace SavviedMatrix.Audio;

/// <summary>
/// Which part of an image's life is making the sound. The phase changes the musical
/// gesture, not the pitch class: rain-in rises into the note, rain-out falls out of it
/// an octave down, and the static strum is the arpeggio played across a settled picture.
/// </summary>
public enum RainPhaseKind
{
    RainIn,
    RainOut,
    StaticStrum
}

/// <summary>
/// Turns a band of image colour into a synthesizer note.
/// <para>
/// This is deliberately a pure function of (colour, stream shape, phase, scale): the whole
/// soundtrack for a phase can therefore be computed up front, before a single frame is drawn,
/// which is what lets the audio be scheduled against a sample clock instead of chased
/// frame-by-frame. Nothing here reads a file or a wavetable - the <see cref="Note"/> it
/// returns is a recipe for oscillator maths, not a sample lookup.
/// </para>
/// </summary>
public static class NoteMapper
{
    // Semitone offsets from C, indexed by ROYGBIV scale degree 0..6.
    private static readonly int[] MajorScale = { 0, 2, 4, 5, 7, 9, 11 };
    private static readonly int[] PentatonicScale = { 0, 2, 4, 7, 9, 12, 14 };

    // Hue bucket edges in degrees. Red straddles 0, so it is the fall-through case.
    private const float RedStart = 345f;
    private const float OrangeStart = 15f;
    private const float YellowStart = 45f;
    private const float GreenStart = 70f;
    private const float BlueStart = 170f;
    private const float IndigoStart = 255f;
    private const float VioletStart = 285f;

    // Luminance -> octave.
    private const float DarkLuminance = 0.3f;
    private const float MidLuminance = 0.7f;

    // Saturation -> waveform. A washed-out band should sound soft, a vivid one should buzz.
    private const float SineSaturation = 0.25f;
    private const float TriangleSaturation = 0.5f;
    private const float SquareSaturation = 0.75f;

    // Stream speed -> attack. A fast stream has to click, a slow one can bloom.
    private const float SlowRowsPerSecond = 5f;
    private const float FastRowsPerSecond = 60f;
    private const float SlowAttackMs = 15f;
    private const float FastAttackMs = 2f;

    // Trail length -> decay. A long tail on screen should ring for longer.
    private const int ShortTrail = 6;
    private const int LongTrail = 14;
    private const float ShortDecayMs = 90f;
    private const float LongDecayMs = 450f;

    // Gain follows luminance, but never all the way to silence.
    private const float MinGain = 0.25f;
    private const float GainLuminanceSpan = 0.75f;

    // Bright waveforms get their top end shaved off; soft ones are left open.
    private const float BrightCutoffHz = 6000f;
    private const float OpenCutoffHz = 20000f;

    // Rain-out is a departure: quieter, shorter, and pitched down.
    private const float RainOutDecayScale = 0.6f;
    private const float RainOutGainScale = 0.8f;

    private static readonly object WarnGate = new();
    private static readonly HashSet<string> WarnedScales = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps one band of image colour onto a fully specified note.
    /// </summary>
    /// <param name="band">Mean colour under the band; hue picks the pitch, luminance the octave and loudness, saturation the timbre.</param>
    /// <param name="stream">The falling stream that will trigger it; its speed and trail shape the envelope.</param>
    /// <param name="phase">Which gesture is being played.</param>
    /// <param name="scaleName">"major" or "pentatonic"; anything else falls back to major.</param>
    public static Note Map(BandStats band, StreamParams stream, RainPhaseKind phase, string scaleName)
    {
        int[] scale = ScaleFor(scaleName);

        // Greyscale has no hue worth trusting, so it sings the root.
        int degree = band.IsGreyscale ? 0 : DegreeForHue(band.Hue);
        int semitone = scale[degree];

        int octave = OctaveForLuminance(band.Luminance);
        int midi = 12 * (octave + 1) + semitone;
        if (phase == RainPhaseKind.RainOut) midi -= 12;

        Waveform wave = WaveformForSaturation(band.Saturation);

        float attackMs = AttackForSpeed(stream.RowsPerSecond);
        float decayMs = DecayForTrail(stream.TrailLength);
        float gain = GainForLuminance(band.Luminance);

        if (phase == RainPhaseKind.RainOut)
        {
            decayMs *= RainOutDecayScale;
            gain *= RainOutGainScale;
        }

        // Glide is the gesture: rain-in slides up into pitch, rain-out sags away from it
        // over the whole of its (already shortened) decay, the strum gets a small scoop.
        (float glideSemitones, float glideMs) = phase switch
        {
            RainPhaseKind.RainIn => (1f, 25f),
            RainPhaseKind.RainOut => (-3f, decayMs),
            _ => (0.5f, 15f)
        };

        float cutoff = wave is Waveform.Square or Waveform.Saw ? BrightCutoffHz : OpenCutoffHz;

        return new Note(
            Frequency: Note.FrequencyForMidi(midi),
            Wave: wave,
            AttackMs: attackMs,
            DecayMs: decayMs,
            Gain: gain,
            GlideSemitones: glideSemitones,
            GlideMs: glideMs,
            FilterCutoffHz: cutoff);
    }

    /// <summary>ROYGBIV hue bucket to scale degree 0..6. Red wraps around 0 degrees.</summary>
    public static int DegreeForHue(float hue)
    {
        float h = Normalize(hue);

        if (h >= OrangeStart && h < YellowStart) return 1;   // orange
        if (h >= YellowStart && h < GreenStart) return 2;    // yellow
        if (h >= GreenStart && h < BlueStart) return 3;      // green
        if (h >= BlueStart && h < IndigoStart) return 4;     // blue
        if (h >= IndigoStart && h < VioletStart) return 5;   // indigo
        if (h >= VioletStart && h < RedStart) return 6;      // violet
        return 0;                                            // red: [345, 360) and [0, 15)
    }

    /// <summary>Semitone offsets from C for a named scale; unknown names fall back to major.</summary>
    public static int[] ScaleFor(string? scaleName)
    {
        if (string.Equals(scaleName, "major", StringComparison.OrdinalIgnoreCase)) return MajorScale;
        if (string.Equals(scaleName, "pentatonic", StringComparison.OrdinalIgnoreCase)) return PentatonicScale;

        WarnOnce(scaleName);
        return MajorScale;
    }

    public static int OctaveForLuminance(float luminance)
    {
        if (luminance < DarkLuminance) return 3;
        if (luminance < MidLuminance) return 4;
        return 5;
    }

    public static Waveform WaveformForSaturation(float saturation)
    {
        if (saturation < SineSaturation) return Waveform.Sine;
        if (saturation < TriangleSaturation) return Waveform.Triangle;
        if (saturation < SquareSaturation) return Waveform.Square;
        return Waveform.Saw;
    }

    /// <summary>Linear from 5 rows/s -> 15ms down to 60 rows/s -> 2ms, clamped at both ends.</summary>
    public static float AttackForSpeed(double rowsPerSecond)
    {
        float t = (float)((rowsPerSecond - SlowRowsPerSecond) / (FastRowsPerSecond - SlowRowsPerSecond));
        t = Math.Clamp(t, 0f, 1f);
        return SlowAttackMs + t * (FastAttackMs - SlowAttackMs);
    }

    /// <summary>Linear from trail 6 -> 90ms to trail 14 -> 450ms, clamped at both ends.</summary>
    public static float DecayForTrail(int trailLength)
    {
        float t = (float)(trailLength - ShortTrail) / (LongTrail - ShortTrail);
        t = Math.Clamp(t, 0f, 1f);
        return ShortDecayMs + t * (LongDecayMs - ShortDecayMs);
    }

    public static float GainForLuminance(float luminance)
        => Math.Clamp(MinGain + GainLuminanceSpan * luminance, MinGain, 1f);

    private static float Normalize(float hue)
    {
        if (float.IsNaN(hue)) return 0f;
        hue %= 360f;
        if (hue < 0f) hue += 360f;
        return hue;
    }

    /// <summary>
    /// A misspelt scale in config.json is a once-per-run complaint, not a per-note one:
    /// this is called for every band of every image and would otherwise flood the log.
    /// </summary>
    private static void WarnOnce(string? scaleName)
    {
        string key = scaleName ?? "<null>";
        lock (WarnGate)
        {
            if (!WarnedScales.Add(key)) return;
        }
        Log.Warn($"Unknown scale '{key}'; falling back to major.");
    }
}
