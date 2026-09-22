using SavviedMatrix.Audio;
using SavviedMatrix.Core;
using SavviedMatrix.Render;
using SavviedMatrix.Source;
using SkiaSharp;

namespace SavviedMatrix.Ascii;

/// <summary>
/// The whole per-image pipeline: fetch, decode, orient, downsample, measure, map.
/// Runs on a background thread while the previous image is on screen, so none of this
/// cost ever lands on the UI thread.
/// </summary>
public sealed class FrameBuilder
{
    private readonly GridGeometry _geometry;
    private readonly AppConfig _config;
    private readonly GlyphSet _glyphs;
    private readonly Palette _palette;
    private readonly GlyphMode _mode;
    private readonly Random _rng;

    public FrameBuilder(GridGeometry geometry, AppConfig config, GlyphSet glyphs, Palette palette, Random rng)
    {
        _geometry = geometry;
        _config = config;
        _glyphs = glyphs;
        _palette = palette;
        _mode = GlyphSet.ParseMode(config.GlyphMode);
        _rng = rng;
    }

    /// <summary>
    /// Builds a frame, or returns null when the image could not be fetched or decoded
    /// so the caller can move on to the next one.
    /// </summary>
    public async Task<Frame?> BuildAsync(IImageSource source, ImageRef image, CancellationToken ct)
    {
        string path;
        try
        {
            path = await source.FetchAsync(image, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not fetch '{image.Name}': {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        ct.ThrowIfCancellationRequested();

        try
        {
            return BuildFromFile(path, image.Name);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not decode '{image.Name}': {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public Frame BuildFromFile(string path, string name)
    {
        using var source = DecodeOriented(path);

        bool halfBlock = _mode == GlyphMode.HalfBlock;

        GridPlacement placement;
        PixelGrid pixels;

        if (halfBlock)
        {
            // Each cell holds two stacked samples, so the fit is worked out in sample space
            // with square cells: half of a 1:2 character cell is a square.
            var samples = GridFit.Fit(
                source.Width, source.Height,
                _geometry.Cols, _geometry.Rows * 2,
                _geometry.CellWidth, _geometry.CellWidth);

            int fitRows = Math.Max(1, samples.FitRows / 2);
            placement = new GridPlacement(
                samples.FitCols, fitRows, samples.OffsetCol, samples.OffsetRow / 2);

            pixels = Downsample(source, placement.FitCols, placement.FitRows * 2);
        }
        else
        {
            placement = GridFit.Fit(
                source.Width, source.Height,
                _geometry.Cols, _geometry.Rows,
                _geometry.CellWidth, _geometry.CellHeight);

            pixels = Downsample(source, placement.FitCols, placement.FitRows);
        }

        var raw = Luminance.Compute(pixels);
        var stretched = Luminance.ContrastStretch(raw, _config.Gamma);

        // Three tone stages. The stretch makes the picture span the range, local contrast
        // pulls each region away from its own average, and equalisation goes last to spread
        // the result evenly over the glyph levels.
        //
        // Equalisation has to come last. Local contrast clamps at both ends, so anything
        // it pushes past the edge piles up on the first and last character; equalising
        // afterwards redistributes those cells instead of leaving them stacked. Measured
        // on photographs, the other order carries less detail than doing nothing at all.
        var toned = LocalContrast.Apply(
            stretched, pixels.Width, pixels.Height, _config.Detail);

        var levelled = Luminance.Equalize(toned, _config.Equalize);

        float saturationScale = Saturation.ScaleFor(pixels, _config.ColorBoost);

        var cells = halfBlock
            ? AsciiMapper.MapHalfBlock(
                pixels, levelled, placement,
                _geometry.Cols, _geometry.Rows,
                _palette, saturationScale, _config.Dither)
            : AsciiMapper.Map(
                pixels, levelled, placement,
                _geometry.Cols, _geometry.Rows,
                _mode, _glyphs, _palette, _rng, saturationScale, _config.Dither);
        var bands = BandAnalyzer.Analyze(pixels, placement, _geometry.Cols, Math.Max(1, _config.Sound.Bands));

        return new Frame(cells, bands, name);
    }

    /// <summary>
    /// Decodes an image and puts it the right way up. Public so the orientation handling
    /// can be tested directly against images carrying each EXIF tag.
    /// <para>
    /// Phone photographs are routinely stored sideways with an EXIF tag saying which way is
    /// up; ignoring it shows them rotated. The tag is applied as a single transformed draw
    /// rather than as a sequence of rotates and flips, and skipped entirely for the usual
    /// case of an upright image.
    /// </para>
    /// </summary>
    public static SKBitmap DecodeOriented(string path)
    {
        using var codec = SKCodec.Create(path)
            ?? throw new InvalidOperationException("Not an image this build can decode.");

        var origin = codec.EncodedOrigin;

        // Bgra8888 is B,G,R,A in memory, which on a little-endian machine reads back as the
        // 0xAARRGGBB int PixelGrid expects, so no per-pixel conversion is ever needed.
        var info = new SKImageInfo(
            codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);

        var decoded = SKBitmap.Decode(codec, info)
            ?? throw new InvalidOperationException("The image could not be decoded.");

        if (origin == SKEncodedOrigin.TopLeft) return decoded;

        try
        {
            bool transposed = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

            int w = decoded.Width;
            int h = decoded.Height;

            var oriented = new SKBitmap(new SKImageInfo(
                transposed ? h : w, transposed ? w : h, SKColorType.Bgra8888, SKAlphaType.Premul));

            using (var canvas = new SKCanvas(oriented))
            {
                ApplyOrigin(canvas, origin, w, h);

                // The orientation draw is 1:1 and axis-aligned, so there is nothing to
                // interpolate and nearest keeps it exact.
                canvas.DrawBitmap(decoded, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
            }

            return oriented;
        }
        finally
        {
            decoded.Dispose();
        }
    }

    /// <summary>
    /// Sets the canvas transform that maps the stored pixels onto the displayed ones.
    /// Canvas operations compose outermost first, so each case reads as the outer move
    /// followed by the inner one.
    /// </summary>
    private static void ApplyOrigin(SKCanvas canvas, SKEncodedOrigin origin, int w, int h)
    {
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:      // mirrored
                canvas.Translate(w, 0);
                canvas.Scale(-1, 1);
                break;

            case SKEncodedOrigin.BottomRight:   // rotated 180
                canvas.Translate(w, h);
                canvas.Scale(-1, -1);
                break;

            case SKEncodedOrigin.BottomLeft:    // mirrored vertically
                canvas.Translate(0, h);
                canvas.Scale(1, -1);
                break;

            case SKEncodedOrigin.LeftTop:       // transposed
                canvas.Scale(-1, 1);
                canvas.RotateDegrees(90);
                break;

            case SKEncodedOrigin.RightTop:      // rotated 90 clockwise
                canvas.Translate(h, 0);
                canvas.RotateDegrees(90);
                break;

            case SKEncodedOrigin.RightBottom:   // transverse
                canvas.Translate(h, w);
                canvas.Scale(1, -1);
                canvas.RotateDegrees(90);
                break;

            case SKEncodedOrigin.LeftBottom:    // rotated 90 anticlockwise
                canvas.Translate(0, w);
                canvas.Scale(-1, -1);
                canvas.RotateDegrees(90);
                break;
        }
    }

    /// <summary>
    /// Resizes down to the character grid and reads the result out as a plain pixel buffer.
    /// <para>
    /// The reduction is done by repeated halving until the image is within a factor of two
    /// of the target, then one filtered step onto the exact size. A resampler has a fixed
    /// kernel a few pixels wide, so asking it to go from four thousand pixels to three
    /// hundred in one step reads a handful of the thousands of pixels each cell covers and
    /// turns a detailed photograph into noise. Halving keeps every source pixel contributing,
    /// which is what an area average would do, at a fraction of the cost.
    /// </para>
    /// </summary>
    private static PixelGrid Downsample(SKBitmap source, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);

        SKBitmap current = source;
        SKBitmap? owned = null;

        try
        {
            while (current.Width >= width * 2 && current.Height >= height * 2
                   && current.Width > 1 && current.Height > 1)
            {
                int halfWidth = Math.Max(width, current.Width / 2);
                int halfHeight = Math.Max(height, current.Height / 2);

                var step = current.Resize(
                    new SKImageInfo(halfWidth, halfHeight, SKColorType.Bgra8888, SKAlphaType.Premul),
                    sampling);

                if (step is null) break;

                owned?.Dispose();
                owned = step;
                current = step;
            }

            SKBitmap final;
            bool finalOwned = false;

            if (current.Width == width && current.Height == height)
            {
                final = current;
            }
            else
            {
                final = current.Resize(
                    new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
                    sampling) ?? throw new InvalidOperationException(
                        $"Could not resize the image to {width}x{height}.");

                finalOwned = true;
            }

            try
            {
                return ToPixelGrid(final, width, height);
            }
            finally
            {
                if (finalOwned) final.Dispose();
            }
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>Copies a Bgra8888 bitmap into the plain ARGB buffer the analysis works over.</summary>
    private static PixelGrid ToPixelGrid(SKBitmap bitmap, int width, int height)
    {
        var buffer = new int[width * height];
        var bytes = bitmap.GetPixelSpan();
        int rowBytes = bitmap.RowBytes;

        for (int y = 0; y < height; y++)
        {
            var row = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(
                bytes.Slice(y * rowBytes, width * 4));

            row.CopyTo(buffer.AsSpan(y * width, width));
        }

        if (!BitConverter.IsLittleEndian)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(buffer[i]);
        }

        return new PixelGrid(buffer, width, height);
    }
}
