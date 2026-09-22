namespace SavviedMatrix.Core;

/// <summary>
/// The character grid laid over the screen. Cells are fixed at a 1:2 aspect so that the
/// ramp font and the katakana font can share one atlas and one grid.
/// </summary>
public sealed class GridGeometry
{
    public int ScreenWidth { get; }
    public int ScreenHeight { get; }
    public int CellWidth { get; }
    public int CellHeight { get; }
    public int Cols { get; }
    public int Rows { get; }

    /// <summary>Pixel offset that centres the grid in the screen, absorbing the remainder.</summary>
    public int OriginX { get; }
    public int OriginY { get; }

    public GridGeometry(int screenWidth, int screenHeight, int requestedColumns)
    {
        if (screenWidth <= 0) throw new ArgumentOutOfRangeException(nameof(screenWidth));
        if (screenHeight <= 0) throw new ArgumentOutOfRangeException(nameof(screenHeight));

        ScreenWidth = screenWidth;
        ScreenHeight = screenHeight;

        int columns = Math.Clamp(requestedColumns, 16, screenWidth);

        CellWidth = Math.Max(2, screenWidth / columns);
        CellHeight = CellWidth * 2;

        Cols = screenWidth / CellWidth;
        Rows = Math.Max(1, screenHeight / CellHeight);

        OriginX = (screenWidth - Cols * CellWidth) / 2;
        OriginY = (screenHeight - Rows * CellHeight) / 2;
    }

    public int CellCount => Cols * Rows;

    public int PixelX(int col) => OriginX + col * CellWidth;
    public int PixelY(int row) => OriginY + row * CellHeight;
}
