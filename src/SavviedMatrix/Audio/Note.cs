namespace SavviedMatrix.Audio;

public enum Waveform
{
    Sine,
    Triangle,
    Square,
    Saw
}

/// <summary>
/// A fully-specified synthesizer event. Nothing here is a sample or a file reference:
/// every field feeds the per-sample DSP in <see cref="Voice"/>.
/// </summary>
public readonly record struct Note(
    float Frequency,
    Waveform Wave,
    float AttackMs,
    float DecayMs,
    float Gain,
    float GlideSemitones,
    float GlideMs,
    float FilterCutoffHz)
{
    public static Note Default(float frequency) => new(
        Frequency: frequency,
        Wave: Waveform.Triangle,
        AttackMs: 4f,
        DecayMs: 220f,
        Gain: 0.6f,
        GlideSemitones: 1f,
        GlideMs: 25f,
        FilterCutoffHz: 6000f);

    /// <summary>Equal-tempered frequency for a MIDI note number.</summary>
    public static float FrequencyForMidi(int midi)
        => (float)(440.0 * Math.Pow(2.0, (midi - 69) / 12.0));
}

/// <summary>A note and the time, in seconds from the start of a phase, at which it fires.</summary>
public readonly record struct ScheduledNote(double Seconds, Note Note);
