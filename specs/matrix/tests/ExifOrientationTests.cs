using SavviedMatrix.Ascii;
using SkiaSharp;

namespace SavviedMatrix.Tests;

/// <summary>
/// The eight EXIF orientations, end to end: a tagged file in, correctly turned pixels out.
///
/// Worth testing directly because the transforms are the one piece of the image path with no
/// visible failure mode. A photograph that comes out rotated or mirrored still renders as
/// perfectly good ASCII art, so nothing downstream notices, and phone photographs - which is
/// most of what a Dropbox folder holds - are exactly the ones that carry the tag.
///
/// The source is deliberately not square, so a transform that is right about the pixels but
/// wrong about which way round the image ends up still fails.
/// </summary>
public class ExifOrientationTests
{
    private const int UprightWidth = 64;
    private const int UprightHeight = 32;

    /// <summary>Four distinguishable quadrants, so no two orientations produce the same image.</summary>
    private static readonly SKColor[] Upright =
    {
        new(220, 20, 20),    // 0 top-left
        new(20, 200, 20),    // 1 top-right
        new(20, 20, 220),    // 2 bottom-left
        new(230, 230, 230)   // 3 bottom-right
    };

    /// <summary>
    /// Where the transform for an orientation sends each stored quadrant, as an index into
    /// <see cref="Upright"/>. A file says "turn me this way to display me", so the stored
    /// pixels are the upright ones with that turn undone.
    /// </summary>
    private static int[] QuadrantMap(int orientation) => orientation switch
    {
        1 => new[] { 0, 1, 2, 3 },  // as stored
        2 => new[] { 1, 0, 3, 2 },  // mirrored horizontally
        3 => new[] { 3, 2, 1, 0 },  // rotated 180
        4 => new[] { 2, 3, 0, 1 },  // mirrored vertically
        5 => new[] { 0, 2, 1, 3 },  // transposed
        6 => new[] { 1, 3, 0, 2 },  // displayed by rotating 90 clockwise
        7 => new[] { 3, 1, 2, 0 },  // transverse
        8 => new[] { 2, 0, 3, 1 },  // displayed by rotating 90 anticlockwise
        _ => throw new ArgumentOutOfRangeException(nameof(orientation))
    };

    private static bool IsTransposed(int orientation) => orientation >= 5;

    private static SKBitmap StoredFor(int orientation)
    {
        var map = QuadrantMap(orientation);

        int width = IsTransposed(orientation) ? UprightHeight : UprightWidth;
        int height = IsTransposed(orientation) ? UprightWidth : UprightHeight;

        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);

        for (int qy = 0; qy < 2; qy++)
        for (int qx = 0; qx < 2; qx++)
        {
            using var paint = new SKPaint { Color = Upright[map[qx + qy * 2]] };
            canvas.DrawRect(
                qx * width / 2f, qy * height / 2f, width / 2f, height / 2f, paint);
        }

        return bmp;
    }

    /// <summary>
    /// Writes a JPEG carrying an EXIF orientation tag. JPEG rather than PNG because Skia
    /// only reads the tag out of a JPEG's APP1 segment; it ignores a PNG's eXIf chunk, which
    /// matches the formats that carry the tag in practice.
    /// </summary>
    private static string WriteTagged(SKBitmap bitmap, int orientation)
    {
        var path = Path.Combine(Path.GetTempPath(), $"exif-{orientation}-{Guid.NewGuid():N}.jpg");

        using (var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 100))
        {
            var raw = data.ToArray();

            var app1 = new List<byte>();
            app1.AddRange("Exif\0\0"u8.ToArray());
            app1.AddRange(TiffBlock(orientation));

            int length = app1.Count + 2;

            using var fs = File.Create(path);
            fs.Write(raw, 0, 2);                                        // SOI
            fs.Write(new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length });
            fs.Write(app1.ToArray());
            fs.Write(raw, 2, raw.Length - 2);
        }

        return path;
    }

    /// <summary>A little-endian TIFF block whose only tag is orientation.</summary>
    private static byte[] TiffBlock(int orientation)
    {
        var b = new List<byte>();
        b.AddRange("II"u8.ToArray());                      // little-endian
        b.AddRange(BitConverter.GetBytes((ushort)42));     // TIFF magic
        b.AddRange(BitConverter.GetBytes(8u));             // offset of IFD0
        b.AddRange(BitConverter.GetBytes((ushort)1));      // one entry
        b.AddRange(BitConverter.GetBytes((ushort)0x0112)); // Orientation
        b.AddRange(BitConverter.GetBytes((ushort)3));      // SHORT
        b.AddRange(BitConverter.GetBytes(1u));             // count
        b.AddRange(BitConverter.GetBytes((ushort)orientation));
        b.AddRange(BitConverter.GetBytes((ushort)0));      // pad the value field to four bytes
        b.AddRange(BitConverter.GetBytes(0u));             // no next IFD
        return b.ToArray();
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void EveryOrientationDecodesUpright(int orientation)
    {
        using var stored = StoredFor(orientation);
        var path = WriteTagged(stored, orientation);

        try
        {
            using var decoded = FrameBuilder.DecodeOriented(path);

            Assert.Equal(UprightWidth, decoded.Width);
            Assert.Equal(UprightHeight, decoded.Height);

            // Sampled at the centre of each quadrant, away from the block edges where JPEG
            // ringing lives.
            AssertQuadrant(decoded, 0, 0, Upright[0], "top-left");
            AssertQuadrant(decoded, 1, 0, Upright[1], "top-right");
            AssertQuadrant(decoded, 0, 1, Upright[2], "bottom-left");
            AssertQuadrant(decoded, 1, 1, Upright[3], "bottom-right");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertQuadrant(SKBitmap bmp, int qx, int qy, SKColor expected, string where)
    {
        int x = qx * bmp.Width / 2 + bmp.Width / 4;
        int y = qy * bmp.Height / 2 + bmp.Height / 4;
        var actual = bmp.GetPixel(x, y);

        const int Tolerance = 24;   // JPEG at quality 100 over flat blocks

        bool close = Math.Abs(actual.Red - expected.Red) <= Tolerance
                  && Math.Abs(actual.Green - expected.Green) <= Tolerance
                  && Math.Abs(actual.Blue - expected.Blue) <= Tolerance;

        Assert.True(close, $"{where} was {actual}, expected about {expected}");
    }
}
