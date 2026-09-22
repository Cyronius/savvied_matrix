using NAudio.Wave;

namespace SavviedMatrix.Audio;

/// <summary>
/// Thrown when a sound device cannot be opened. The kiosk must keep showing pictures without
/// sound rather than fall over, so the caller catches this and drops to
/// <see cref="NullAudioOutput"/>.
/// </summary>
public sealed class AudioUnavailableException : Exception
{
    public AudioUnavailableException(string message) : base(message) { }
    public AudioUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The audio layer as the rest of the app sees it. Everything above this interface schedules
/// notes against a sample clock and never has to know whether a device actually exists.
/// </summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>Queues a phase's notes, with times measured from "now" on screen.</summary>
    void Schedule(IEnumerable<ScheduledNote> notes);

    /// <summary>The synth's sample clock, or 0 when there is no device.</summary>
    long Clock { get; }

    /// <summary>Silences everything immediately; used when a phase is cut short.</summary>
    void Panic();

    /// <summary>False when sound is disabled or the device failed.</summary>
    bool IsAvailable { get; }

    /// <summary>Output buffer depth in samples, i.e. how far ahead of the ear the clock runs.</summary>
    int LatencySamples { get; }
}

/// <summary>
/// The silent implementation, used when sound is switched off or no device could be opened.
/// It exists so callers never need a null check on the audio layer.
/// </summary>
public sealed class NullAudioOutput : IAudioOutput
{
    public void Schedule(IEnumerable<ScheduledNote> notes) { }
    public long Clock => 0;
    public void Panic() { }
    public bool IsAvailable => false;
    public int LatencySamples => 0;
    public void Dispose() { }
}

/// <summary>
/// Real output: a <see cref="Synth"/> feeding a NAudio <see cref="WaveOut"/>.
/// NAudio is only the transport here - it pulls the float buffers the synth computes and hands
/// them to the sound card. No sample data is loaded from anywhere.
/// <para>
/// <see cref="WaveOut"/> is deliberate: since NAudio 3 it is the event-callback player (what 2.x
/// called <c>WaveOutEvent</c>), so buffers are refilled on its own background thread.
/// <c>WaveOutWindow</c>, the window-message player, would deliver its callback on the UI thread
/// and every repaint would stall the audio.
/// </para>
/// </summary>
public sealed class NAudioOutput : IAudioOutput
{
    /// <summary>
    /// WaveOut below roughly this many milliseconds starves while the UI thread is composing a
    /// frame, and a starved buffer is a tick in the output. The app spends around 8 ms a frame
    /// blitting, so a configured latency under this is raised rather than honoured.
    /// </summary>
    public const int MinLatencyMs = 60;

    private const int DefaultSampleRate = 44100;

    /// <summary>
    /// NAudio 3 dropped <c>DesiredLatency</c> for the pair it was always made of: total latency
    /// is <see cref="BufferCount"/> buffers of <c>BufferMilliseconds</c> each.
    /// <para>
    /// Four small buffers beat two large ones at the same total latency. What protects against a
    /// glitch is how much audio is already queued when a buffer completes: with two buffers that
    /// is one buffer (half the latency), with four it is three (three quarters). Same delay to
    /// the ear, far more slack for the render thread to be late.
    /// </para>
    /// </summary>
    private const int BufferCount = 4;

    private const int MinBufferMs = 15;

    private readonly Synth _synth;
    private readonly WaveOut _device;
    private readonly int _latencySamples;

    private volatile bool _available;
    private bool _disposed;

    public NAudioOutput(SoundConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        int latencyMs = Math.Max(MinLatencyMs, config.LatencyMs);
        int bufferMs = Math.Max(MinBufferMs, latencyMs / BufferCount);

        // The device only delivers whole buffers, so this is the latency we actually get.
        int actualLatencyMs = bufferMs * BufferCount;
        _latencySamples = (int)((long)actualLatencyMs * DefaultSampleRate / 1000);

        _synth = new Synth(DefaultSampleRate, config.Polyphony)
        {
            MasterVolume = config.Volume
        };

        WaveOut? device = null;
        try
        {
            if (WaveOut.DeviceCount <= 0)
                throw new InvalidOperationException("No waveOut device is present.");

            device = new WaveOut
            {
                NumberOfBuffers = BufferCount,
                BufferMilliseconds = bufferMs
            };
            device.PlaybackStopped += OnPlaybackStopped;
            device.Init(_synth);
            device.Play();
        }
        catch (Exception ex)
        {
            device?.Dispose();
            throw new AudioUnavailableException($"Could not open an audio device: {ex.Message}", ex);
        }

        _device = device ?? throw new AudioUnavailableException("Could not open an audio device.");

        _available = true;
        Log.Info($"Audio ready: {DefaultSampleRate} Hz mono, {actualLatencyMs} ms latency, {_synth.Polyphony} voices.");
    }

    public long Clock => _synth.Clock;

    public bool IsAvailable => _available && !_disposed;

    public int LatencySamples => _latencySamples;

    /// <summary>
    /// Anchors the phase at the current clock minus the output latency, so a note written for
    /// "the head hits the bottom row now" leaves the speaker as the eye sees it rather than a
    /// buffer later. Notes that land before the clock are floored to it by the synth.
    /// </summary>
    public void Schedule(IEnumerable<ScheduledNote> notes)
    {
        if (!IsAvailable || notes is null) return;
        _synth.Schedule(notes, _synth.Clock - _latencySamples);
    }

    public void Panic() => _synth.Panic();

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        _available = false;

        if (e.Exception is not null)
            Log.Error("Audio playback stopped", e.Exception);
        else if (!_disposed)
            Log.Warn("Audio playback stopped unexpectedly; continuing without sound.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _available = false;

        try
        {
            _device.PlaybackStopped -= OnPlaybackStopped;
            _synth.Panic();
            _device.Stop();
            _device.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Audio device did not close cleanly: {ex.Message}");
        }
    }
}

/// <summary>Factory for the audio layer; the only place that decides sound or silence.</summary>
public static class AudioOutput
{
    /// <summary>
    /// Returns a working output, or a silent one when sound is disabled or the device
    /// refuses to open. Never throws: a kiosk with no sound card still shows pictures.
    /// </summary>
    public static IAudioOutput Create(SoundConfig config)
    {
        if (config is null || !config.Enabled)
            return new NullAudioOutput();

        try
        {
            return new NAudioOutput(config);
        }
        catch (AudioUnavailableException ex)
        {
            Log.Warn($"Sound disabled: {ex.Message}");
            return new NullAudioOutput();
        }
    }
}
