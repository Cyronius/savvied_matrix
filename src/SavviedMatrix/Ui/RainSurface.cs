using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SavviedMatrix.Ui;

/// <summary>
/// The one thing on screen: a screen-sized bitmap the compositor writes into, drawn at
/// exactly one bitmap pixel per device pixel.
/// <para>
/// Only the rectangle the compositor reports as changed is uploaded, so a rain frame costs
/// a copy of the few rows that moved rather than of the whole screen. The draw itself is a
/// single textured quad, which the platform composites on the GPU.
/// </para>
/// </summary>
public sealed class RainSurface : Control
{
    private readonly WriteableBitmap _bitmap;
    private readonly int _width;
    private readonly int _height;
    private Rect _lastPresented;

    public RainSurface(int width, int height)
    {
        _width = width;
        _height = height;

        // Bgra8888 matches the compositor's 0xAARRGGBB ints byte for byte on a
        // little-endian machine, so the upload is a straight memory copy.
        _bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        // The image is drawn 1:1, so any filtering would only soften the glyph edges that
        // carry the picture.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);

        IsHitTestVisible = false;
    }

    /// <summary>
    /// Copies the changed rectangle of the compositor's buffer into the bitmap and asks for
    /// a repaint. Must be called on the UI thread.
    /// </summary>
    public void Upload(int[] pixels, System.Drawing.Rectangle dirty)
    {
        if (dirty.Width <= 0 || dirty.Height <= 0) return;

        // Clamp, so a geometry mismatch can never walk off the end of either buffer.
        int x0 = Math.Max(0, dirty.X);
        int y0 = Math.Max(0, dirty.Y);
        int x1 = Math.Min(_width, dirty.Right);
        int y1 = Math.Min(_height, dirty.Bottom);

        if (x1 <= x0 || y1 <= y0) return;

        int runLength = x1 - x0;

        using (var frame = _bitmap.Lock())
        {
            unsafe
            {
                byte* destBase = (byte*)frame.Address;

                fixed (int* src = pixels)
                {
                    for (int y = y0; y < y1; y++)
                    {
                        int* srcRow = src + y * _width + x0;
                        byte* destRow = destBase + y * frame.RowBytes + x0 * 4;
                        Buffer.MemoryCopy(srcRow, destRow, runLength * 4L, runLength * 4L);
                    }
                }
            }
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // The grid is built in physical pixels, so the destination is the bitmap's pixel
        // size converted back into the layout's device-independent units. Centring what is
        // left keeps every glyph landing on whole device pixels even if the window ends up
        // a few units bigger than the screen was measured to be, which is what a fractional
        // scale would otherwise destroy.
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (scaling <= 0) scaling = 1.0;

        // Fit inside the window rather than centring at native size. At the size the grid
        // was built for these are the same thing and the draw stays 1:1, which is the case
        // that matters. They differ while a fullscreen window is still animating up to the
        // screen, and centring at native size there puts the top and bottom rows outside
        // the window, where they are silently cropped rather than merely small.
        double nativeWidth = _width / scaling;
        double nativeHeight = _height / scaling;

        double fit = Math.Min(
            1.0,
            Math.Min(bounds.Width / nativeWidth, bounds.Height / nativeHeight));

        double w = nativeWidth * fit;
        double h = nativeHeight * fit;
        double x = Math.Floor((bounds.Width - w) / 2);
        double y = Math.Floor((bounds.Height - h) / 2);

        var presented = new Rect(x, y, w, h);

        if (presented != _lastPresented)
        {
            _lastPresented = presented;

            Log.Info(
                $"Presenting {_width}x{_height}px into {bounds.Width:0.#}x{bounds.Height:0.#} "
                + $"at {scaling:0.##}x as {w * scaling:0.#}x{h * scaling:0.#}px "
                + $"at {x * scaling:0.#},{y * scaling:0.#}"
                + (fit < 1.0 ? $" (scaled to {fit:P0}: the window is smaller than the grid)" : " (1:1)"));
        }

        context.FillRectangle(Brushes.Black, new Rect(bounds.Size));
        context.DrawImage(_bitmap, new Rect(0, 0, _width, _height), new Rect(x, y, w, h));
    }

    public void DisposeBitmap() => _bitmap.Dispose();
}
