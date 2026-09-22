// Traces: MATRIX-SOUND-OUTPUT
using SavviedMatrix.Audio;

namespace SavviedMatrix.Tests;

/// <summary>
/// The synth's output is deterministic - same notes in, same floats out - so the things that
/// are heard as crackle can be asserted numerically: band-limited oscillators, no step
/// discontinuities when a voice is cut off or runs out, and enough headroom that the soft
/// clipper is not doing the mixing.
/// </summary>
public class SynthTests
{
    private const int Rate = 44100;

    private static Note Osc(Waveform wave, float hz, float gain = 1f) =>
        new(Frequency: hz,
            Wave: wave,
            AttackMs: 1f,
            DecayMs: 4000f,     // long, so a one-second render stays at a steady level
            Gain: gain,
            GlideSemitones: 0f, // no glide: these tests are about the oscillator, not the gesture
            GlideMs: 0f,
            FilterCutoffHz: 20000f);

    /// <summary>
    /// The same note with a decay so long the envelope is flat across a one-second render, so a
    /// spectrum measured from it is the oscillator's own and not the envelope's.
    /// </summary>
    private static Note Steady(Waveform wave, float hz) => Osc(wave, hz) with { DecayMs = 1_000_000f };

    private static float[] Render(Voice voice, int samples)
    {
        var buf = new float[samples];
        for (int i = 0; i < samples; i++) buf[i] = voice.NextSample();
        return buf;
    }

    private static int UpwardZeroCrossings(float[] samples)
    {
        int crossings = 0;
        float prev = 0f;
        foreach (float s in samples)
        {
            if (prev <= 0f && s > 0f) crossings++;
            prev = s;
        }
        return crossings;
    }

    private static float MaxJump(float[] samples, int from = 1)
    {
        float max = 0f;
        for (int i = Math.Max(from, 1); i < samples.Length; i++)
            max = Math.Max(max, Math.Abs(samples[i] - samples[i - 1]));
        return max;
    }

    private static float Peak(float[] samples)
    {
        float max = 0f;
        foreach (float s in samples) max = Math.Max(max, Math.Abs(s));
        return max;
    }

    /// <summary>Amplitude of one frequency in a buffer, by Goertzel. Used to weigh aliasing.</summary>
    private static double Amplitude(float[] samples, double frequency)
    {
        double w = 2 * Math.PI * frequency / Rate;
        double cosine = Math.Cos(w);
        double coefficient = 2 * cosine;
        double s1 = 0, s2 = 0;

        foreach (float sample in samples)
        {
            double s0 = sample + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        double real = s1 - s2 * cosine;
        double imaginary = s2 * Math.Sin(w);
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / samples.Length;
    }

    /// <summary>The unfiltered shapes, as a reference to measure the band-limited ones against.</summary>
    private static float[] NaiveOscillator(Waveform wave, double frequency, int samples)
    {
        var output = new float[samples];
        double phase = 0, increment = frequency / Rate;

        for (int i = 0; i < samples; i++)
        {
            phase += increment;
            if (phase >= 1.0) phase -= Math.Floor(phase);
            output[i] = wave == Waveform.Saw
                ? 2f * (float)phase - 1f
                : phase < 0.5 ? 1f : -1f;
        }

        return output;
    }

    // --- Oscillators -------------------------------------------------------------------

    [Theory]
    [InlineData(Waveform.Saw)]
    [InlineData(Waveform.Square)]
    [InlineData(Waveform.Sine)]
    [InlineData(Waveform.Triangle)]
    public void OscillatorRunsAtTheRequestedPitch(Waveform wave)
    {
        var v = new Voice();
        v.Start(Osc(wave, 441f), Rate);

        // 441 Hz at 44100 is exactly 100 samples a cycle, so one second is 441 cycles.
        int crossings = UpwardZeroCrossings(Render(v, Rate));

        Assert.InRange(crossings, 439, 443);
    }

    [Theory]
    [InlineData(Waveform.Saw)]
    [InlineData(Waveform.Square)]
    [InlineData(Waveform.Sine)]
    [InlineData(Waveform.Triangle)]
    public void OscillatorStaysInRange(Waveform wave)
    {
        var v = new Voice();
        v.Start(Osc(wave, 987f), Rate);

        // PolyBLEP corrections must not push the shape outside unity.
        Assert.All(Render(v, Rate / 4), s => Assert.InRange(s, -1.001f, 1.001f));
    }

    [Theory]
    // C6 is the top of the mapper's range. For a saw the 40th harmonic folds back to 2240 Hz,
    // for a square the 41st folds to 1193.5 Hz - neither is a harmonic of the note, so both are
    // heard as grit sitting under the pitch. This is the fold-back the user hears as crackle.
    [InlineData(Waveform.Saw, 2240.0)]
    [InlineData(Waveform.Square, 1193.5)]
    public void PolyBlepRemovesTheFoldBackNoise(Waveform wave, double aliasHz)
    {
        const double note = 1046.5;

        var v = new Voice();
        v.Start(Steady(wave, (float)note), Rate);
        var limited = Render(v, Rate);
        var naive = NaiveOscillator(wave, note, Rate);

        double aliasNaive = Amplitude(naive, aliasHz);
        double aliasLimited = Amplitude(limited, aliasHz);

        Assert.True(aliasNaive > 0.01, $"reference alias too quiet to judge: {aliasNaive}");
        Assert.True(aliasLimited < aliasNaive * 0.1,
            $"alias at {aliasHz} Hz was {aliasLimited} against {aliasNaive} naive");
    }

    [Theory]
    [InlineData(Waveform.Saw)]
    [InlineData(Waveform.Square)]
    public void PolyBlepKeepsTheWaveformItself(Waveform wave)
    {
        const double note = 1046.5;

        var v = new Voice();
        v.Start(Steady(wave, (float)note), Rate);
        var limited = Render(v, Rate);
        var naive = NaiveOscillator(wave, note, Rate);

        // The point is to lose the fold-back, not the brightness: the fundamental has to survive
        // intact, otherwise we have just built an expensive sine.
        double fundamentalNaive = Amplitude(naive, note);
        double fundamentalLimited = Amplitude(limited, note);
        Assert.Equal(fundamentalNaive, fundamentalLimited, 2);

        // A saw is 2/pi at the fundamental, a square 4/pi. Still recognisably themselves.
        double expected = wave == Waveform.Saw ? 2.0 / Math.PI : 4.0 / Math.PI;
        Assert.Equal(expected, fundamentalLimited, 2);

        // And the third harmonic, the thing that makes them sound cheap, is still there.
        Assert.True(Amplitude(limited, note * 3) > 0.15,
            $"third harmonic was only {Amplitude(limited, note * 3)}");
    }

    [Fact]
    public void HighNotesKeepTheirPitch()
    {
        var v = new Voice();
        v.Start(Osc(Waveform.Saw, 1046.5f), Rate);

        // Band limiting must not cost or add a cycle.
        Assert.InRange(UpwardZeroCrossings(Render(v, Rate)), 1044, 1048);
    }

    // --- Declick -----------------------------------------------------------------------

    [Fact]
    public void StealingAVoiceDoesNotStepTheOutput()
    {
        var v = new Voice();
        v.Start(Osc(Waveform.Sine, 440f), Rate);

        // Run to a point where the voice is loud, so a hard cut would be a big step.
        float last = 0f;
        for (int i = 0; i < Rate && last < 0.8f; i++) last = v.NextSample();
        Assert.True(last > 0.8f, $"setup failed: level was {last}");

        // Steal it for a different note, exactly as the pool does when it runs out.
        v.Start(new Note(220f, Waveform.Sine, 5f, 4000f, 1f, 0f, 0f, 20000f), Rate);
        var after = Render(v, 1000);

        // Slope budget: a 220 Hz sine moves 2*pi*220/44100 = 0.031 a sample, its 5 ms attack
        // adds 1/220 = 0.005, and the 3 ms declick ramp from 0.8 adds 0.006. Without the ramp
        // the first sample here would drop the whole 0.8 to nothing in one step.
        Assert.True(Math.Abs(after[0] - last) < 0.05f, $"step of {Math.Abs(after[0] - last)} on steal");
        Assert.True(MaxJump(after) < 0.05f, $"step of {MaxJump(after)} during declick");
    }

    [Fact]
    public void SilencingAVoiceRampsRatherThanChops()
    {
        var v = new Voice();
        v.Start(Osc(Waveform.Sine, 440f), Rate);

        float last = 0f;
        for (int i = 0; i < Rate && last < 0.8f; i++) last = v.NextSample();

        v.Silence();
        var after = Render(v, 1000);

        Assert.True(Math.Abs(after[0] - last) < 0.1f, $"step of {Math.Abs(after[0] - last)} on silence");
        Assert.True(MaxJump(after) < 0.1f, $"step of {MaxJump(after)} during declick");

        // The ramp is short: 3 ms at 44100 is 132 samples, so it is long over by 1000.
        Assert.False(v.IsActive);
        Assert.Equal(0f, after[^1]);
    }

    [Fact]
    public void ANoteEndsOnSilenceRatherThanOnAStep()
    {
        var v = new Voice();
        var note = new Note(440f, Waveform.Sine, 2f, 90f, 1f, 0f, 0f, 20000f);
        v.Start(note, Rate);

        var samples = Render(v, Rate);   // a second is far longer than a 90 ms decay

        Assert.False(v.IsActive);
        Assert.Equal(0f, samples[^1]);

        // Attack, decay and the closing release ramp all stay inside a sine's own slope limit.
        Assert.True(MaxJump(samples) < 0.1f, $"step of {MaxJump(samples)} across the note");
    }

    [Fact]
    public void DeadVoiceStaysSilentAndInactive()
    {
        var v = new Voice();
        v.Start(new Note(440f, Waveform.Saw, 1f, 50f, 1f, 0f, 0f, 6000f), Rate);
        Assert.True(v.IsActive);

        Render(v, Rate);

        Assert.False(v.IsActive);
        Assert.Equal(0f, v.NextSample());
    }

    [Fact]
    public void ZeroSampleRateIsRefusedRatherThanDividingByZero()
    {
        var v = new Voice();
        v.Start(Note.Default(440f), 0);

        Assert.False(v.IsActive);
        Assert.Equal(0f, v.NextSample());
    }

    // --- Mix headroom ------------------------------------------------------------------

    [Fact]
    public void ARealisticClusterNeverReachesTheSoftClipper()
    {
        var synth = new Synth(Rate, 8) { MasterVolume = 1f };

        // What the app actually produces when streams bunch up: four bands landing together at
        // the gain a mid-luminance band maps to (0.25 + 0.75 * 0.5).
        var notes = new List<ScheduledNote>();
        for (int i = 0; i < 4; i++)
            notes.Add(new ScheduledNote(0.0, Osc((Waveform)i, 220f + 37f * i, gain: 0.625f)));
        synth.Schedule(notes, synth.Clock);

        var buf = new float[Rate / 4];
        synth.Read(buf, 0, buf.Length);

        // 4 * 0.625 * (1/sqrt(8)) = 0.88 even with every peak aligned, so tanh has nothing to do.
        // Without the headroom this was 2.5 and the clipper was flattening every plink.
        Assert.True(synth.PeakLevel <= 1.0f, $"pre-clip peak was {synth.PeakLevel}");
        Assert.All(buf, s => Assert.InRange(s, -1f, 1f));
    }

    [Fact]
    public void AFullPolyphonyPileUpStaysWithinTheClippersReach()
    {
        var synth = new Synth(Rate, 8) { MasterVolume = 1f };

        // The other end: all eight voices at once at the maximum gain the mapper can emit,
        // which needs eight pure-white bands. The headroom cannot rescue this on its own.
        var notes = new List<ScheduledNote>();
        for (int i = 0; i < 8; i++)
            notes.Add(new ScheduledNote(0.0, Osc((Waveform)(i % 4), 220f + 37f * i)));
        synth.Schedule(notes, synth.Clock);

        var buf = new float[Rate / 4];
        synth.Read(buf, 0, buf.Length);

        // Bounded by polyphony * mixGain = sqrt(8) = 2.83, against 8.0 before the headroom.
        Assert.InRange(synth.PeakLevel, 1.0f, 2.83f);
        Assert.All(buf, s => Assert.InRange(s, -1f, 1f));
    }

    [Fact]
    public void MixGainIsTheReciprocalRootOfPolyphony()
    {
        Assert.Equal(1.0, new Synth(Rate, 1).MixGain, 6);
        Assert.Equal(0.5, new Synth(Rate, 4).MixGain, 6);
        Assert.Equal(1.0 / Math.Sqrt(8), new Synth(Rate, 8).MixGain, 6);
    }

    [Fact]
    public void ASingleNoteIsStillClearlyAudible()
    {
        var synth = new Synth(Rate, 8) { MasterVolume = 1f };
        synth.Schedule(new[] { new ScheduledNote(0.0, Osc(Waveform.Triangle, 440f, gain: 0.625f)) }, synth.Clock);

        var buf = new float[Rate / 10];
        synth.Read(buf, 0, buf.Length);

        // 0.625 gain through the 1/sqrt(8) headroom is about -13 dBFS; the headroom must not
        // bury a lone plink.
        Assert.InRange(Peak(buf), 0.15f, 0.25f);
    }

    [Fact]
    public void OutputIsBoundedEvenWhenEveryVoiceIsInPhase()
    {
        var synth = new Synth(Rate, 8) { MasterVolume = 1f };

        // The pathological case: identical square waves, perfectly correlated. This is what the
        // soft clipper is still there for.
        var notes = new List<ScheduledNote>();
        for (int i = 0; i < 8; i++) notes.Add(new ScheduledNote(0.0, Osc(Waveform.Square, 300f)));
        synth.Schedule(notes, synth.Clock);

        var buf = new float[8192];
        synth.Read(buf, 0, buf.Length);

        Assert.All(buf, s => Assert.InRange(s, -1f, 1f));
    }

    // --- Clock and scheduling ----------------------------------------------------------

    [Fact]
    public void ReadFillsTheBufferAndAdvancesTheClock()
    {
        var synth = new Synth(Rate, 4) { MasterVolume = 1f };
        Assert.Equal(Rate, synth.WaveFormat.SampleRate);
        Assert.Equal(1, synth.WaveFormat.Channels);

        var buf = new float[4410];
        int written = synth.Read(buf, 0, buf.Length);

        Assert.Equal(buf.Length, written);
        Assert.Equal(buf.Length, synth.Clock);
        Assert.All(buf, s => Assert.True(float.IsFinite(s)));
    }

    [Fact]
    public void ANoteStartsOnItsScheduledSample()
    {
        var synth = new Synth(1000, 4) { MasterVolume = 1f };
        var note = new Note(100f, Waveform.Square, 0f, 500f, 1f, 0f, 0f, 20000f);
        synth.Schedule(new[] { new ScheduledNote(0.5, note) }, 0);

        var buf = new float[1000];
        synth.Read(buf, 0, buf.Length);

        for (int i = 0; i < 500; i++) Assert.Equal(0f, buf[i]);
        Assert.True(Math.Abs(buf[500]) > 0.01f, "note did not start at sample 500");
    }

    [Fact]
    public void ANoteScheduledInThePastStillPlays()
    {
        var synth = new Synth(Rate, 4) { MasterVolume = 1f };

        var buf = new float[4410];
        synth.Read(buf, 0, buf.Length);      // clock is now 4410

        // NAudioOutput anchors phases at Clock - LatencySamples, which is deliberately behind.
        synth.Schedule(new[] { new ScheduledNote(0.0, Osc(Waveform.Triangle, 440f)) }, 0);
        Array.Clear(buf);
        synth.Read(buf, 0, buf.Length);

        Assert.True(Peak(buf) > 0.05f, $"late note was dropped (peak {Peak(buf)})");
    }

    [Fact]
    public void PanicSilencesEverythingWithinTheDeclickRamp()
    {
        var synth = new Synth(Rate, 4) { MasterVolume = 1f };
        synth.Schedule(new[] { new ScheduledNote(0.0, Osc(Waveform.Triangle, 440f)) }, synth.Clock);

        var buf = new float[1024];
        synth.Read(buf, 0, buf.Length);
        synth.Panic();

        // 1024 samples is 23 ms, far longer than the 3 ms declick, so the tail is gone by the end.
        synth.Read(buf, 0, buf.Length);
        Assert.Equal(0f, buf[^1]);
        Assert.True(MaxJump(buf, from: 0) < 0.1f, $"panic stepped by {MaxJump(buf, from: 0)}");
    }

    [Fact]
    public void StealingKeepsThePoolPlayingWhenItIsExhausted()
    {
        var synth = new Synth(Rate, 1) { MasterVolume = 1f };

        var notes = new List<ScheduledNote>();
        for (int i = 0; i < 8; i++)
            notes.Add(new ScheduledNote(i * 0.01, Osc(Waveform.Sine, 220f + 40f * i)));
        synth.Schedule(notes, synth.Clock);

        var buf = new float[Rate / 2];
        synth.Read(buf, 0, buf.Length);

        Assert.All(buf, s => Assert.True(float.IsFinite(s)));

        // Seven steals in a row, and none of them may click.
        Assert.True(MaxJump(buf, from: 1) < 0.15f, $"steal stepped by {MaxJump(buf, from: 1)}");
    }
}
