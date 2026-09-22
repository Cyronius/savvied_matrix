namespace SavviedMatrix.Audio;

/// <summary>
/// One monophonic synthesizer voice: oscillator, envelope, glide and a one-pole filter.
/// <para>
/// Every sample is computed from oscillator maths - there is no wavetable, no sample bank and
/// nothing loaded from disk. The saw and square are band-limited with PolyBLEP, which removes the
/// fold-back noise that made high notes read as digital crackle while leaving the waveforms
/// recognisably saw and square: the cheap mono-synth character survives, the aliasing does not.
/// </para>
/// <para>
/// A voice is owned by the audio thread. <see cref="NextSample"/> allocates nothing and never
/// throws, because it runs inside the sound card's callback where a stall is an audible glitch.
/// </para>
/// </summary>
public sealed class Voice
{
    /// <summary>Envelope level at which the decay hands over to the release ramp.</summary>
    public const float SilenceThreshold = 0.001f;

    /// <summary>
    /// Length of the ramp that takes a finished note's envelope to exactly zero. Without it the
    /// voice stops on whatever sample it happened to be on, which is a step, which is a click.
    /// </summary>
    public const float ReleaseMs = 2f;

    /// <summary>
    /// Length of the ramp that carries a cut-off voice's last output down to zero. Stealing a
    /// voice mid-waveform is a full-scale step otherwise - the loudest click the synth can make.
    /// </summary>
    public const float DeclickMs = 3f;

    private const float OpenFilterHz = 19000f;

    /// <summary>Below this the filter state is flushed, so it cannot fall into denormal arithmetic.</summary>
    private const float DenormalFloor = 1e-20f;

    private enum Stage { Idle, Attack, Decay, Release }

    private double _phase;
    private double _sampleRate = 1.0;

    private Waveform _wave;
    private float _targetFrequency;
    private float _gain;

    private Stage _stage = Stage.Idle;
    private float _envelope;
    private float _attackIncrement;
    private float _decayCoefficient;
    private float _releaseStep;
    private int _releaseSamples;

    // Glide: a pitch offset in semitones that walks linearly to zero.
    private float _glideSemitones;
    private int _glideSamplesTotal;
    private int _glideSamplesLeft;

    // One-pole low-pass state.
    private bool _filterOn;
    private float _filterAlpha;
    private float _filterState;

    // Declick: the tail of whatever this voice was doing when it was cut off.
    private float _tailLevel;
    private float _tailStep;

    private float _lastOutput;

    /// <summary>
    /// True while this voice still contributes sound, including the few milliseconds of declick
    /// tail after it has been cut off. The pool only reuses voices that are fully finished.
    /// </summary>
    public bool IsActive => _stage != Stage.Idle || _tailLevel != 0f;

    /// <summary>Begins a note. Anything already sounding on this voice is faded out, not chopped.</summary>
    public void Start(in Note note, int sampleRate)
    {
        BeginDeclick(sampleRate);
        _stage = Stage.Idle;

        // A bad device or a bad note must not produce NaN on the audio path; just stay idle.
        if (sampleRate <= 0) return;
        if (!float.IsFinite(note.Frequency) || note.Frequency <= 0f) return;

        _sampleRate = sampleRate;
        _wave = note.Wave;
        _targetFrequency = note.Frequency;
        _gain = float.IsFinite(note.Gain) ? Math.Clamp(note.Gain, 0f, 1f) : 0f;

        int attackSamples = MsToSamples(note.AttackMs, sampleRate);
        if (attackSamples > 0)
        {
            _stage = Stage.Attack;
            _envelope = 0f;
            _attackIncrement = 1f / attackSamples;
        }
        else
        {
            // A zero attack starts at full level and decays immediately.
            _stage = Stage.Decay;
            _envelope = 1f;
            _attackIncrement = 1f;
        }

        double decaySeconds = float.IsFinite(note.DecayMs) ? Math.Max(note.DecayMs, 0f) / 1000.0 : 0.0;
        _decayCoefficient = decaySeconds > 0.0
            ? (float)Math.Exp(-1.0 / (decaySeconds * sampleRate))
            : 0f;

        _releaseSamples = Math.Max(1, MsToSamples(ReleaseMs, sampleRate));
        _releaseStep = 0f;

        _glideSemitones = float.IsFinite(note.GlideSemitones) ? note.GlideSemitones : 0f;
        _glideSamplesTotal = MsToSamples(note.GlideMs, sampleRate);
        _glideSamplesLeft = _glideSemitones == 0f ? 0 : _glideSamplesTotal;

        _filterOn = float.IsFinite(note.FilterCutoffHz) && note.FilterCutoffHz < OpenFilterHz;
        if (_filterOn)
        {
            double cutoff = Math.Max(note.FilterCutoffHz, 1.0);
            float alpha = (float)(1.0 - Math.Exp(-2.0 * Math.PI * cutoff / sampleRate));
            _filterAlpha = Math.Clamp(alpha, 1e-6f, 1f);
        }

        _phase = 0.0;
        _filterState = 0f;
    }

    /// <summary>
    /// Stops the note. The voice still renders a <see cref="DeclickMs"/> ramp from its last
    /// output down to zero, because even a panic stop has to land on silence rather than jump
    /// to it.
    /// </summary>
    public void Silence()
    {
        int rate = _sampleRate > 0 ? (int)_sampleRate : 0;
        BeginDeclick(rate);
        _stage = Stage.Idle;
        _envelope = 0f;
    }

    /// <summary>Renders the next sample, or 0 when the voice is idle.</summary>
    public float NextSample()
    {
        if (_stage == Stage.Idle && _tailLevel == 0f) return 0f;

        float output = 0f;

        if (_stage != Stage.Idle)
        {
            // --- Pitch, with the glide offset walking linearly back to zero. ---
            float frequency = _targetFrequency;
            if (_glideSamplesLeft > 0 && _glideSamplesTotal > 0)
            {
                float semis = _glideSemitones * ((float)_glideSamplesLeft / _glideSamplesTotal);
                frequency = _targetFrequency * float.Exp2(semis / 12f);
                _glideSamplesLeft--;
            }

            // --- Oscillator. ---
            double increment = frequency / _sampleRate;
            _phase += increment;
            if (_phase >= 1.0 || _phase < 0.0)
            {
                _phase -= Math.Floor(_phase);
                if (!double.IsFinite(_phase)) _phase = 0.0;
            }

            float osc = Oscillate((float)_phase, (float)increment);

            // --- Envelope. ---
            switch (_stage)
            {
                case Stage.Attack:
                    _envelope += _attackIncrement;
                    if (_envelope >= 1f)
                    {
                        _envelope = 1f;
                        _stage = Stage.Decay;
                    }
                    break;

                case Stage.Decay:
                    _envelope *= _decayCoefficient;
                    if (_envelope < SilenceThreshold)
                    {
                        // Hand over to a short linear ramp so the note ends on a real zero.
                        _stage = Stage.Release;
                        _releaseStep = _envelope / _releaseSamples;
                    }
                    break;

                default:
                    _envelope -= _releaseStep;
                    if (_envelope <= 0f)
                    {
                        _envelope = 0f;
                        _stage = Stage.Idle;
                    }
                    break;
            }

            // --- One-pole low-pass, only when the note asked for one. ---
            if (_filterOn)
            {
                _filterState += (osc - _filterState) * _filterAlpha;
                if (MathF.Abs(_filterState) < DenormalFloor) _filterState = 0f;
                osc = _filterState;
            }

            output = osc * _envelope * _gain;
        }

        // --- Declick tail from a cut-off note, summed under whatever is playing now. ---
        if (_tailLevel != 0f)
        {
            output += _tailLevel;
            _tailLevel -= _tailStep;

            // Stop on the crossing rather than overshooting into the opposite sign.
            if (!float.IsFinite(_tailLevel)
                || (_tailStep >= 0f && _tailLevel <= 0f)
                || (_tailStep < 0f && _tailLevel >= 0f))
            {
                _tailLevel = 0f;
            }
        }

        if (!float.IsFinite(output))
        {
            // Something went numerically wrong; drop the voice rather than hand NaN to the DAC.
            HardReset();
            return 0f;
        }

        _lastOutput = output;
        return output;
    }

    /// <summary>
    /// Naive shapes for sine and triangle, which are continuous and do not alias badly; PolyBLEP
    /// correction on the saw and square, whose jumps are what fold back as noise.
    /// </summary>
    private float Oscillate(float phase, float increment)
    {
        switch (_wave)
        {
            case Waveform.Sine:
                return (float)Math.Sin(2.0 * Math.PI * _phase);

            case Waveform.Triangle:
                return 4f * Math.Abs(phase - 0.5f) - 1f;

            case Waveform.Square:
            {
                float square = phase < 0.5f ? 1f : -1f;
                float dt = BlepWidth(increment);
                if (dt > 0f)
                {
                    // One correction for the rising edge at phase 0, one for the fall at 0.5.
                    square += PolyBlep(phase, dt);
                    float half = phase + 0.5f;
                    if (half >= 1f) half -= 1f;
                    square -= PolyBlep(half, dt);
                }
                return square;
            }

            default:
            {
                float saw = 2f * phase - 1f;
                float dt = BlepWidth(increment);
                if (dt > 0f) saw -= PolyBlep(phase, dt);
                return saw;
            }
        }
    }

    /// <summary>
    /// The correction is only meaningful while a cycle spans several samples; above that the
    /// oscillator is past Nyquist anyway and the naive shape is left alone.
    /// </summary>
    private static float BlepWidth(float increment)
    {
        if (!float.IsFinite(increment) || increment <= 0f || increment >= 0.5f) return 0f;
        return increment;
    }

    /// <summary>
    /// Polynomial approximation of a band-limited step, sampled either side of a discontinuity.
    /// Adding it to the naive shape replaces the instantaneous jump with a two-sample ramp,
    /// which is what kills the fold-back without smearing the waveform.
    /// </summary>
    private static float PolyBlep(float t, float dt)
    {
        if (t < dt)
        {
            t /= dt;
            return t + t - t * t - 1f;
        }

        if (t > 1f - dt)
        {
            t = (t - 1f) / dt;
            return t * t + t + t + 1f;
        }

        return 0f;
    }

    /// <summary>Arms the declick ramp from wherever the output currently sits.</summary>
    private void BeginDeclick(int sampleRate)
    {
        float level = _lastOutput;
        if (level == 0f || !float.IsFinite(level)) return;

        int samples = MsToSamples(DeclickMs, sampleRate > 0 ? sampleRate : 0);
        if (samples <= 0)
        {
            _tailLevel = 0f;
            return;
        }

        _tailLevel = level;
        _tailStep = level / samples;
    }

    private void HardReset()
    {
        _stage = Stage.Idle;
        _envelope = 0f;
        _phase = 0.0;
        _filterState = 0f;
        _glideSamplesLeft = 0;
        _glideSemitones = 0f;
        _tailLevel = 0f;
        _tailStep = 0f;
        _lastOutput = 0f;
    }

    private static int MsToSamples(float ms, int sampleRate)
    {
        if (sampleRate <= 0) return 0;
        if (!float.IsFinite(ms) || ms <= 0f) return 0;
        double samples = ms / 1000.0 * sampleRate;
        if (samples <= 0.0) return 0;
        if (samples > int.MaxValue) return int.MaxValue;
        return (int)samples;
    }
}
