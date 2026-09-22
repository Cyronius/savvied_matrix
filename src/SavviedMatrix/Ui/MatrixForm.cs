using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using SavviedMatrix.Ascii;
using SavviedMatrix.Audio;
using SavviedMatrix.Core;
using SavviedMatrix.Rain;
using SavviedMatrix.Render;
using SavviedMatrix.Source;

namespace SavviedMatrix.Ui;

/// <summary>
/// The display loop. Holds the phase machine, the prefetch of the next image, and the
/// hand-off of scheduled notes to the synthesizer.
/// Nothing here decodes, downloads or analyses anything: the UI thread only composes
/// pixels and blits them.
/// </summary>
public sealed class MatrixForm : Form
{
    private enum Phase
    {
        /// <summary>Waiting for the first image.</summary>
        Startup,

        /// <summary>Rain is carrying one image away and the next one in, in a single pass.</summary>
        Transition,

        /// <summary>An image is standing still on screen.</summary>
        Hold
    }

    private readonly AppConfig _config;
    private readonly GridGeometry _geometry;
    private readonly GlyphSet _glyphs;
    private readonly GlyphAtlas _atlas;
    private readonly Palette _palette;
    private readonly Compositor _compositor;
    private readonly RainCompositor _rain;
    private readonly FrameBuilder _builder;
    private readonly IImageSource _source;
    private readonly Playlist<ImageRef> _playlist;
    private readonly IAudioOutput _audio;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Stopwatch _phaseClock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Random _rng;
    private readonly bool _rainEnabled;
    private readonly CellGrid _blank;

    private Frame? _current;
    private Frame? _incoming;
    private Task<Frame?>? _prefetch;
    private Phase _phase = Phase.Startup;
    private StreamParams[]? _streams;
    private bool _holdComposed;
    private bool _closing;

    private double _frameTimeTotalMs;
    private int _frameTimeSamples;
    private DateTime _lastEmptyNotice = DateTime.MinValue;

    public MatrixForm(
        AppConfig config,
        GridGeometry geometry,
        GlyphSet glyphs,
        GlyphAtlas atlas,
        Palette palette,
        IImageSource source,
        IAudioOutput audio,
        Random rng)
    {
        _config = config;
        _geometry = geometry;
        _glyphs = glyphs;
        _atlas = atlas;
        _palette = palette;
        _source = source;
        _audio = audio;
        _rng = rng;
        _rainEnabled = config.Rain.Enabled;

        _compositor = new Compositor(geometry, atlas);
        _rain = new RainCompositor(geometry, glyphs, palette, rng);
        _builder = new FrameBuilder(geometry, config, glyphs, palette, new Random(rng.Next()));
        _playlist = new Playlist<ImageRef>(new Random(rng.Next()));
        _blank = new CellGrid(geometry.Cols, geometry.Rows, new GridPlacement(0, 0, 0, 0));

        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.UserPaint,
            true);

        FormBorderStyle = FormBorderStyle.None;
        BackColor = Palette.Background;
        StartPosition = FormStartPosition.Manual;
        Text = "SavviedMatrix";
        KeyPreview = true;

        if (config.WindowSize is { } size)
        {
            Location = new Point(0, 0);
            ClientSize = size;
        }
        else
        {
            var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            Location = bounds.Location;
            ClientSize = bounds.Size;
            TopMost = true;
        }

        _timer = new System.Windows.Forms.Timer
        {
            Interval = _rainEnabled
                ? Math.Max(8, 1000 / Math.Clamp(config.Rain.Fps, 1, 120))
                : 100
        };
        _timer.Tick += (_, _) => Tick();

        ShowMessage("LOADING");
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_config.WindowSize is null) Cursor.Hide();

        _prefetch = StartPrefetch();
        _phaseClock.Restart();
        _timer.Start();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // The compositor's surface already covers every pixel.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var clip = Rectangle.Intersect(
            e.ClipRectangle,
            new Rectangle(0, 0, _geometry.ScreenWidth, _geometry.ScreenHeight));

        if (clip.Width <= 0 || clip.Height <= 0) return;

        // The surface is fully opaque and drawn 1:1, so blending and interpolation are
        // pure waste. SourceCopy turns the blit into a straight memory transfer, which
        // is most of the per-frame cost during rain.
        e.Graphics.CompositingMode = CompositingMode.SourceCopy;
        e.Graphics.CompositingQuality = CompositingQuality.HighSpeed;
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.SmoothingMode = SmoothingMode.None;

        e.Graphics.DrawImage(_compositor.Surface, clip, clip, GraphicsUnit.Pixel);
    }

    // ---------------------------------------------------------------- phase machine

    private void Tick()
    {
        if (_closing) return;

        try
        {
            switch (_phase)
            {
                case Phase.Startup:
                    TryStartNextImage();
                    break;

                case Phase.Transition:
                    TickTransition();
                    break;

                case Phase.Hold:
                    TickHold();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Display tick failed", ex);
        }
    }

    private void TickTransition()
    {
        if (_incoming is null || _streams is null)
        {
            BeginHold();
            return;
        }

        var started = Stopwatch.GetTimestamp();

        double t = _phaseClock.Elapsed.TotalSeconds;
        _rain.Evaluate(_streams, t, _current?.Cells ?? _blank, _incoming.Cells);
        Present();

        RecordFrameTime(started);

        if (!RainField.IsComplete(_streams, _geometry.Rows, t)) return;

        LogFrameTime("transition");

        // The incoming image has fully settled; it is now the one on screen.
        _current = _incoming;
        _incoming = null;
        _prefetch = StartPrefetch();

        BeginHold();
    }

    private void TickHold()
    {
        if (!_holdComposed)
        {
            if (_current is not null)
            {
                _rain.SetTarget(_current.Cells);
                Present();
            }
            _holdComposed = true;
        }

        if (_phaseClock.Elapsed.TotalSeconds < _config.DisplaySeconds) return;

        TryStartNextImage();
    }

    /// <summary>
    /// Begins the handover to the next image if one is ready. When the prefetch is still
    /// running, the current image simply stays up a little longer, which is the right
    /// failure mode for a slow network on a wall display.
    /// </summary>
    private bool TryStartNextImage()
    {
        if (_prefetch is null)
        {
            _prefetch = StartPrefetch();
            return false;
        }

        if (!_prefetch.IsCompleted) return false;

        Frame? frame = null;

        if (_prefetch.IsFaulted)
            Log.Error($"Image prefetch failed: {_prefetch.Exception?.GetBaseException().Message}");
        else if (_prefetch.IsCompletedSuccessfully)
            frame = _prefetch.Result;

        if (frame is null)
        {
            // Nothing to show: try again on the next interval rather than spinning.
            _prefetch = StartPrefetch();
            ShowEmptyNotice();
            return false;
        }

        if (_rainEnabled)
        {
            _incoming = frame;
            BeginTransition();
        }
        else
        {
            _current = frame;
            _prefetch = StartPrefetch();
            BeginHoldWithStrum();
        }

        return true;
    }

    private void BeginTransition()
    {
        double seconds = _config.Rain.TransitionSeconds;

        _streams = StreamParamsFactory.Create(_geometry.Cols, _geometry.Rows, seconds, _rng);
        _rain.ReseedStreamGlyphs();

        ScheduleNotes();

        _phase = Phase.Transition;
        ResetFrameTime();
        _phaseClock.Restart();
    }

    private void BeginHold()
    {
        _phase = Phase.Hold;
        _holdComposed = false;
        _phaseClock.Restart();
    }

    private void BeginHoldWithStrum()
    {
        if (_config.Sound.StaticStrum && _current is { Bands.Length: > 0 })
        {
            var notes = PlinkScheduler.ForStaticStrum(_current.Bands, 0.5, _config.Sound.Scale);
            _audio.Schedule(notes);
        }

        BeginHold();
    }

    /// <summary>
    /// Queues the whole phase's plinks up front. The stream parameters already determine
    /// when every column lands, so the sound does not depend on frames being delivered
    /// on time.
    /// </summary>
    private void ScheduleNotes()
    {
        if (_incoming is null || _streams is null || _incoming.Bands.Length == 0) return;

        try
        {
            var notes = PlinkScheduler.ForRainPhase(
                _streams, _incoming.Bands, _geometry.Cols, _geometry.Rows,
                RainPhaseKind.RainIn, _config.Sound.Scale);

            _audio.Schedule(notes);
        }
        catch (Exception ex)
        {
            Log.Error("Could not schedule the sound for this phase", ex);
        }
    }

    // ---------------------------------------------------------------- prefetch

    /// <summary>
    /// Fetches and converts the next image on a thread-pool thread. Skips images that
    /// cannot be read rather than letting one bad file end the show.
    /// </summary>
    private Task<Frame?> StartPrefetch()
    {
        var ct = _cts.Token;

        return Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                if (_playlist.IsEmpty)
                {
                    var listing = await _source.ListAsync(ct).ConfigureAwait(false);
                    _playlist.Refill(listing);

                    if (_playlist.IsEmpty) return null;
                }

                if (!_playlist.TryNext(out var image)) return null;

                var frame = await _builder.BuildAsync(_source, image, ct).ConfigureAwait(false);
                if (frame is not null) return frame;
            }

            Log.Warn("Ten images in a row could not be read; waiting for the next cycle.");
            return null;
        }, ct);
    }

    // ---------------------------------------------------------------- painting

    private void Present()
    {
        var dirty = _compositor.Compose(_rain.Glyphs, _rain.Colours, _rain.Lowers);
        if (dirty.IsEmpty) return;
        Invalidate(dirty);
        Update();
    }

    private void ShowMessage(params string[] lines)
    {
        var grid = GridText.Message(_geometry, _glyphs, _palette, lines);
        _rain.SetTarget(grid);
        _compositor.Compose(_rain.Glyphs, _rain.Colours, _rain.Lowers);
        Invalidate();
    }

    private void ShowEmptyNotice()
    {
        // Rate-limited so a persistently empty folder does not spam the log.
        if ((DateTime.UtcNow - _lastEmptyNotice).TotalSeconds < 30) return;
        _lastEmptyNotice = DateTime.UtcNow;

        Log.Warn($"No usable images in {_source.Description}.");
        ShowMessage("NO IMAGES", _source.Description);

        _current = null;
        _phase = Phase.Startup;
        _phaseClock.Restart();
    }

    // ---------------------------------------------------------------- telemetry

    private void ResetFrameTime()
    {
        _frameTimeTotalMs = 0;
        _frameTimeSamples = 0;
    }

    private void RecordFrameTime(long startedTimestamp)
    {
        double ms = (Stopwatch.GetTimestamp() - startedTimestamp) * 1000.0 / Stopwatch.Frequency;
        _frameTimeTotalMs += ms;
        _frameTimeSamples++;
    }

    private void LogFrameTime(string phase)
    {
        if (_frameTimeSamples == 0) return;

        double average = _frameTimeTotalMs / _frameTimeSamples;
        double budget = 1000.0 / Math.Clamp(_config.Rain.Fps, 1, 120);

        Log.Info(
            $"{phase}: {_frameTimeSamples} frames, {average:F1} ms average compose+paint, " +
            $"{budget:F0} ms budget at {_config.Rain.Fps} fps.");
    }

    // ---------------------------------------------------------------- input and teardown

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        BeginClose();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        BeginClose();
    }

    private void BeginClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        _timer.Stop();
        _cts.Cancel();
        _audio.Panic();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _compositor.Dispose();
            _atlas.Dispose();
            _audio.Dispose();
            _cts.Dispose();
            if (_config.WindowSize is null) Cursor.Show();
        }

        base.Dispose(disposing);
    }
}
