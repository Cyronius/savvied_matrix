using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Enums;
using SoundFlow.Structs;

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
/// Adapts <see cref="Synth"/> to the device graph. The engine pulls this on its own audio
/// thread and the synth fills the buffer; nothing else is in the path.
/// </summary>
internal sealed class SynthComponent : SoundComponent
{
    private readonly Synth _synth;

    public SynthComponent(AudioEngine engine, AudioFormat format, Synth synth)
        : base(engine, format)
    {
        _synth = synth;
    }

    public override string Name { get; set; } = "SavviedMatrix synth";

    protected override void GenerateAudio(Span<float> buffer, int channels)
    {
        if (channels <= 1)
        {
            _synth.Read(buffer);
            return;
        }

        // The device was asked for mono but handed us interleaved frames. Render one sample
        // per frame and fan it out, rather than letting the synth fill the whole buffer:
        // its sample clock counts frames, and the schedule the picture is aligned to would
        // otherwise run fast by exactly the channel count.
        int frames = buffer.Length / channels;
        if (frames == 0) { buffer.Clear(); return; }

        Span<float> mono = frames <= 4096 ? stackalloc float[frames] : new float[frames];
        _synth.Read(mono);

        for (int f = 0, i = 0; f < frames; f++)
        {
            float s = mono[f];
            for (int c = 0; c < channels; c++) buffer[i++] = s;
        }
    }
}

/// <summary>
/// Real output: a <see cref="Synth"/> feeding a miniaudio playback device.
/// The engine is only the transport here - it pulls the float buffers the synth computes and
/// hands them to the sound card. No sample data is loaded from anywhere.
/// <para>
/// miniaudio rather than a per-platform API: it resolves to CoreAudio on macOS, WASAPI on
/// Windows and ALSA or PulseAudio on Linux, and the native library travels in the NuGet
/// package, so a kiosk needs nothing installed beyond the app itself.
/// </para>
/// </summary>
public sealed class MiniAudioOutput : IAudioOutput
{
    /// <summary>
    /// A device below roughly this many milliseconds starves while the UI thread is composing
    /// a frame, and a starved buffer is a tick in the output. The app spends around 8 ms a
    /// frame blitting, so a configured latency under this is raised rather than honoured.
    /// </summary>
    public const int MinLatencyMs = 60;

    private const int DefaultSampleRate = 44100;

    /// <summary>
    /// Total latency is <see cref="BufferCount"/> periods of <c>PeriodSizeInMilliseconds</c>.
    /// <para>
    /// Four small buffers beat two large ones at the same total latency. What protects against
    /// a glitch is how much audio is already queued when a buffer completes: with two buffers
    /// that is one buffer (half the latency), with four it is three (three quarters). Same
    /// delay to the ear, far more slack for the render thread to be late.
    /// </para>
    /// </summary>
    private const int BufferCount = 4;

    private const int MinBufferMs = 15;

    private readonly Synth _synth;
    private readonly MiniAudioEngine _engine;
    private readonly AudioPlaybackDevice _device;
    private readonly SynthComponent _component;
    private readonly int _latencySamples;

    private volatile bool _available;
    private bool _disposed;

    public MiniAudioOutput(SoundConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        int latencyMs = Math.Max(MinLatencyMs, config.LatencyMs);
        int bufferMs = Math.Max(MinBufferMs, latencyMs / BufferCount);

        // The device only delivers whole periods, so this is the latency we actually get.
        int actualLatencyMs = bufferMs * BufferCount;
        _latencySamples = (int)((long)actualLatencyMs * DefaultSampleRate / 1000);

        _synth = new Synth(DefaultSampleRate, config.Polyphony)
        {
            MasterVolume = config.Volume
        };

        MiniAudioEngine? engine = null;
        AudioPlaybackDevice? device = null;

        try
        {
            engine = new MiniAudioEngine();
            engine.UpdateAudioDevicesInfo();

            if (engine.PlaybackDevices.Length == 0)
                throw new InvalidOperationException("No playback device is present.");

            var format = new AudioFormat
            {
                Format = SampleFormat.F32,
                Channels = Synth.Channels,
                SampleRate = DefaultSampleRate
            };

            var deviceConfig = new MiniAudioDeviceConfig
            {
                Periods = BufferCount,
                PeriodSizeInMilliseconds = (uint)bufferMs,

                // The synth always fills the whole buffer, so pre-silencing it is a memset
                // of the entire period that is immediately overwritten.
                NoPreSilencedOutputBuffer = true
            };

            // Null asks for the system default, which is what follows the operator's own
            // output choice when a TV is plugged in or unplugged.
            device = engine.InitializePlaybackDevice(null, format, deviceConfig);

            _component = new SynthComponent(engine, format, _synth);
            device.MasterMixer.AddComponent(_component);
            device.Start();
        }
        catch (Exception ex)
        {
            device?.Dispose();
            engine?.Dispose();
            throw new AudioUnavailableException($"Could not open an audio device: {ex.Message}", ex);
        }

        _engine = engine;
        _device = device;
        _available = true;

        var name = device.Info?.Name ?? "default device";
        Log.Info(
            $"Audio ready: {name}, {DefaultSampleRate} Hz mono, "
            + $"{actualLatencyMs} ms latency, {_synth.Polyphony} voices.");
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _available = false;

        try
        {
            _synth.Panic();
            _device.Stop();
            _device.MasterMixer.RemoveComponent(_component);
            _component.Dispose();
            _device.Dispose();
            _engine.Dispose();
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
            return new MiniAudioOutput(config);
        }
        catch (AudioUnavailableException ex)
        {
            Log.Warn($"Sound disabled: {ex.Message}");
            return new NullAudioOutput();
        }
        catch (Exception ex)
        {
            // A missing or unloadable native library surfaces here rather than as
            // AudioUnavailableException, and must not take the slideshow down with it.
            Log.Warn($"Sound disabled: {ex.GetType().Name}: {ex.Message}");
            return new NullAudioOutput();
        }
    }
}
