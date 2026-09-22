namespace SavviedMatrix.Core;

/// <summary>
/// A plain row-major ARGB pixel buffer, decoupled from System.Drawing so the
/// analysis code is a pure function over an array and can be tested without GDI+.
/// </summary>
public sealed class PixelGrid
{
    public int[] Argb { get; }
    public int Width { get; }
    public int Height { get; }

    public PixelGrid(int[] argb, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "PixelGrid dimensions must be positive.");
        if (argb.Length < width * height)
            throw new ArgumentException("Pixel buffer is smaller than width * height.", nameof(argb));

        Argb = argb;
        Width = width;
        Height = height;
    }

    public int this[int x, int y] => Argb[y * Width + x];

    public static (byte R, byte G, byte B) Split(int argb)
        => ((byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
}
