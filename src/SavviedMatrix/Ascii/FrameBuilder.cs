using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SavviedMatrix.Audio;
using SavviedMatrix.Core;
using SavviedMatrix.Render;
using SavviedMatrix.Source;

namespace SavviedMatrix.Ascii;

/// <summary>
/// The whole per-image pipeline: fetch, decode, orient, downsample, measure, map.
/// Runs on a background thread while the previous image is on screen, so none of this
/// cost ever lands on the UI thread.
/// </summary>
public sealed class FrameBuilder
{
    private const int ExifOrientationId = 0x0112;

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
        using var source = new Bitmap(path);
        ApplyExifOrientation(source);

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
    /// One bilinear resize straight down to the character grid. This is the only pass over
    /// the full-resolution pixels, which is what keeps a large photo cheap on a weak CPU.
    /// </summary>
    private static PixelGrid Downsample(Bitmap source, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        using var small = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.None;
            g.DrawImage(source, new Rectangle(0, 0, width, height));
        }

        var buffer = new int[width * height];
        var data = small.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            if (data.Stride == width * 4)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    var rowStart = data.Scan0 + y * data.Stride;
                    System.Runtime.InteropServices.Marshal.Copy(rowStart, buffer, y * width, width);
                }
            }
        }
        finally
        {
            small.UnlockBits(data);
        }

        return new PixelGrid(buffer, width, height);
    }

    /// <summary>
    /// Rotates according to the EXIF orientation tag. Phone photographs are routinely
    /// stored sideways with a tag saying which way is up; ignoring it shows them rotated.
    /// </summary>
    public static void ApplyExifOrientation(Image image)
    {
        try
        {
            if (Array.IndexOf(image.PropertyIdList, ExifOrientationId) < 0) return;

            var prop = image.GetPropertyItem(ExifOrientationId);
            if (prop?.Value is null || prop.Value.Length < 2) return;

            int orientation = BitConverter.ToUInt16(prop.Value, 0);

            var transform = orientation switch
            {
                2 => RotateFlipType.RotateNoneFlipX,
                3 => RotateFlipType.Rotate180FlipNone,
                4 => RotateFlipType.Rotate180FlipX,
                5 => RotateFlipType.Rotate90FlipX,
                6 => RotateFlipType.Rotate90FlipNone,
                7 => RotateFlipType.Rotate270FlipX,
                8 => RotateFlipType.Rotate270FlipNone,
                _ => RotateFlipType.RotateNoneFlipNone
            };

            if (transform != RotateFlipType.RotateNoneFlipNone)
            {
                image.RotateFlip(transform);
                image.RemovePropertyItem(ExifOrientationId);
            }
        }
        catch (Exception ex)
        {
            // An unreadable orientation tag is not a reason to skip the picture.
            Log.Warn($"EXIF orientation could not be read: {ex.Message}");
        }
    }
}
