namespace SavviedMatrix.Audio;

/// <summary>
/// The mixer and sample clock: a fixed pool of <see cref="Voice"/> objects, a queue of notes
/// waiting for their sample, and the render loop the audio device pulls on.
/// <para>
/// The sample counter is the only clock the music uses. The UI reads <see cref="Clock"/> when a
/// phase begins and schedules the whole phase against it, so timing never depends on frame rate,
/// timer drift or how long a repaint took.
/// </para>
/// <para>
/// <see cref="Read"/> runs on the audio thread: it allocates nothing, takes the queue lock once
/// per buffer rather than once per sample, and swallows any exception, because throwing out of a
/// sound card callback kills playback for the rest of the run.
/// </para>
/// </summary>
public sealed class Synth
{
    /// <summary>Most notes that can be handed to one render pass; the rest wait for the next.</summary>
    private const int MaxDuePerBuffer = 512;

    private readonly object _gate = new();
    private readonly PriorityQueue<Note, long> _pending = new();

    private readonly Voice[] _voices;
    private readonly long[] _voiceStart;

    /// <summary>Notes drained from <see cref="_pending"/> for the buffer being rendered, in time order.</summary>
    private readonly PendingNote[] _due = new PendingNote[MaxDuePerBuffer];
    private int _dueCount;

    private readonly int _sampleRate;
    private readonly float _mixGain;
    private long _samplesRendered;
    private float _masterVolume = 1f;
    private float _peakLevel;
    private int _panicRequested;

    public Synth(int sampleRate = 44100, int polyphony = 8)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        _sampleRate = sampleRate;
        int voices = Math.Max(1, polyphony);

        _voices = new Voice[voices];
        _voiceStart = new long[voices];
        for (int i = 0; i < voices; i++) _voices[i] = new Voice();

        // Headroom. Without it a cluster of plinks sums to several times full scale and the
        // soft clipper has to flatten it, which is heard as harshness rather than as saturation.
        // 1/sqrt(n) is the usual compromise: uncorrelated voices land near unity, a lone note
        // is only a few dB down instead of the n-fold cut a flat 1/n would cost it.
        _mixGain = 1f / MathF.Sqrt(voices);
    }

    /// <summary>Mono. The plinks carry no stereo information, so a second channel would
    /// only double the render cost for an identical signal.</summary>
    public const int Channels = 1;

    public int SampleRate => _sampleRate;

    public int Polyphony => _voices.Length;

    /// <summary>
    /// Samples rendered so far. Read from the UI thread at the start of a phase to anchor
    /// that phase's schedule; written only by the audio thread.
    /// </summary>
    public long Clock => Interlocked.Read(ref _samplesRendered);

    /// <summary>The per-voice headroom factor applied before the master volume.</summary>
    public float MixGain => _mixGain;

    /// <summary>
    /// Largest absolute level the soft clipper was handed during the last render, i.e. the mix
    /// *before* tanh. Anything at or under 1.0 means the clipper had nothing to do. Diagnostic
    /// only - it exists so the headroom can be asserted rather than assumed.
    /// </summary>
    public float PeakLevel => Volatile.Read(ref _peakLevel);

    /// <summary>Final output gain, applied before the soft clipper.</summary>
    public float MasterVolume
    {
        get => Volatile.Read(ref _masterVolume);
        set => Volatile.Write(ref _masterVolume, float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f);
    }

    /// <summary>
    /// Queues notes relative to <paramref name="startSample"/> (normally the <see cref="Clock"/>
    /// captured when the phase began, offset for output latency). Safe to call from the UI thread
    /// while the audio thread renders. Notes whose moment has already gone off start at once
    /// rather than being dropped - a late plink beats a missing one.
    /// </summary>
    public void Schedule(IEnumerable<ScheduledNote> notes, long startSample)
    {
        if (notes is null) return;

        long now = Clock;

        lock (_gate)
        {
            foreach (var scheduled in notes)
            {
                double seconds = scheduled.Seconds;
                if (double.IsNaN(seconds)) seconds = 0;

                double offset = seconds * _sampleRate;
                long at = offset >= long.MaxValue - startSample
                    ? long.MaxValue
                    : startSample + (long)Math.Round(offset);

                if (at < now) at = now;

                _pending.Enqueue(scheduled.Note, at);
            }
        }
    }

    /// <summary>
    /// Drops everything: pending notes now, sounding voices at the top of the next buffer.
    /// Used when the user cuts a phase short, where a clean silence beats a tidy release.
    /// </summary>
    public void Panic()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
        Interlocked.Exchange(ref _panicRequested, 1);
    }

    /// <summary>
    /// The audio thread's entry point. Always fills the whole buffer and always returns its
    /// full length: returning short would end the stream.
    /// </summary>
    public int Read(Span<float> buffer)
    {
        int count = buffer.Length;
        if (count <= 0) return 0;

        try
        {
            RenderCore(buffer);
        }
        catch (Exception ex)
        {
            // Never let an exception escape into the sound card callback.
            buffer.Clear();
            Log.Error("Synth render failed", ex);
        }

        // An infinite stream: always hand back a full buffer or the device stops playback.
        return count;
    }

    /// <summary>Array form of <see cref="Read(Span{float})"/>, for callers and tests that hold arrays.</summary>
    public int Read(float[] buffer, int offset, int count)
    {
        if (buffer is null || count <= 0) return Math.Max(count, 0);
        if (offset < 0 || offset >= buffer.Length) return count;

        int usable = Math.Min(count, buffer.Length - offset);
        Read(buffer.AsSpan(offset, usable));
        return count;
    }

    private void RenderCore(Span<float> buffer)
    {
        if (Interlocked.Exchange(ref _panicRequested, 0) == 1)
        {
            foreach (var v in _voices) v.Silence();
            _dueCount = 0;
        }

        int count = buffer.Length;
        long clock = _samplesRendered;
        DrainDue(clock, count);

        float volume = Volatile.Read(ref _masterVolume) * _mixGain;
        int dueIndex = 0;
        float peak = 0f;

        for (int i = 0; i < count; i++)
        {
            while (dueIndex < _dueCount && _due[dueIndex].StartSample <= clock)
            {
                StartNote(_due[dueIndex].Note, clock);
                dueIndex++;
            }

            float mix = 0f;
            for (int v = 0; v < _voices.Length; v++)
            {
                var voice = _voices[v];
                if (voice.IsActive) mix += voice.NextSample();
            }

            mix *= volume;

            float magnitude = MathF.Abs(mix);
            if (magnitude > peak) peak = magnitude;

            // Soft clip: the headroom above should keep the mix under unity on its own, so this
            // is the last resort for an unlucky in-phase pile-up, not the thing doing the work.
            buffer[i] = MathF.Tanh(mix);

            clock++;
        }

        _dueCount = 0;
        Volatile.Write(ref _peakLevel, peak);
        Interlocked.Exchange(ref _samplesRendered, clock);
    }

    /// <summary>Moves every note due inside this buffer out of the shared queue in one lock.</summary>
    private void DrainDue(long clock, int count)
    {
        _dueCount = 0;
        long limit = clock + count;

        lock (_gate)
        {
            while (_dueCount < _due.Length
                   && _pending.TryPeek(out _, out long at)
                   && at < limit)
            {
                var note = _pending.Dequeue();
                if (at < clock) at = clock;
                _due[_dueCount++] = new PendingNote(at, note);
            }
        }
    }

    private void StartNote(in Note note, long clock)
    {
        int index = -1;

        for (int i = 0; i < _voices.Length; i++)
        {
            if (!_voices[i].IsActive)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            // Pool exhausted: steal the oldest sounding voice, which is the one furthest
            // into its decay and therefore the least missed.
            long oldest = long.MaxValue;
            index = 0;
            for (int i = 0; i < _voices.Length; i++)
            {
                if (_voiceStart[i] < oldest)
                {
                    oldest = _voiceStart[i];
                    index = i;
                }
            }
        }

        _voices[index].Start(note, _sampleRate);
        _voiceStart[index] = clock;
    }

    private readonly record struct PendingNote(long StartSample, Note Note);
}
